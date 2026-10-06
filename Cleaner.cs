using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Plant3DCacheCleaner")]
[assembly: AssemblyVersion("__VERSION__")]
[assembly: AssemblyFileVersion("__VERSION__")]

namespace Plant3DCacheCleaner
{
    public sealed class Settings
    {
        public int Schema = 1;

        public bool RunAtSignIn = true;
        public bool CheckForUpdates = true;

        public bool ExternalDataCache = true;
        public bool GraphicsCache = true;
        public bool BrowserCache = true;
        public bool PersistentCache = true;

        public bool WindowsTemp = false;
        public int TempMinimumAgeDays = 7;
        public bool InternetCache = false;
        public bool RecycleBin = false;
    }

    public sealed class UpdateManifest
    {
        public string version;
        public string sha256;
    }

    internal static class App
    {
        internal const string Repository = "__REPOSITORY__";
        internal const string PublicKeyBase64 = "__PUBLIC_KEY__";

        internal static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Plant3DCacheCleaner");

        internal static readonly string Versions = Path.Combine(Root, "versions");
        internal static readonly string SettingsPath = Path.Combine(Root, "settings.json");
        internal static readonly string LogPath = Path.Combine(Root, "cleanup.log");

        internal static readonly string StartupShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Plant 3D Cache Cleaner.lnk");

        internal static readonly string MenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Plant 3D Cache Cleaner.lnk");

        internal static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer();

        internal static Version Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        internal static string Exe
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool startup = HasArgument(args, "--startup");
            bool skipUpdate = HasArgument(args, "--skip-update");

            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(Versions);

                // Install into a per-user, versioned directory. An old shortcut
                // or downloaded copy redirects to the newest installed version.
                string installed = InstallAndFindNewest();

                if (!SamePath(installed, Exe))
                {
                    Start(installed, Arguments(startup, skipUpdate));
                    return;
                }

                CreateShortcut(MenuShortcut, Exe, "");

                if (!startup)
                {
                    Settings settings = LoadSettings();
                    Application.Run(new SettingsWindow(settings));
                    return;
                }

                // Missing settings must never trigger unattended deletion.
                if (!File.Exists(SettingsPath))
                {
                    Log("Startup skipped: settings have not been saved.");
                    return;
                }

                string nextExe = null;

                using (Mutex mutex = new Mutex(
                    false, @"Local\Plant3DCacheCleaner.AutomaticRun"))
                {
                    bool acquired = false;

                    try
                    {
                        try
                        {
                            acquired = mutex.WaitOne(0);
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }

                        if (!acquired)
                            return;

                        Settings settings = LoadSettings();

                        if (!settings.RunAtSignIn)
                        {
                            Log("Startup skipped: automatic cleanup is disabled.");
                            return;
                        }

                        if (!skipUpdate)
                        {
                            // Give Windows and the network a little time.
                            Thread.Sleep(20000);
                        }

                        // Re-read in case the user changed settings during the delay.
                        settings = LoadSettings();

                        if (!settings.RunAtSignIn)
                            return;

                        if (settings.CheckForUpdates && !skipUpdate)
                            nextExe = TryDownloadUpdate();

                        if (nextExe == null)
                            Cleanup.Run(settings);
                    }
                    finally
                    {
                        if (acquired)
                            mutex.ReleaseMutex();
                    }
                }

                if (nextExe != null)
                {
                    try
                    {
                        Start(nextExe, "--startup --skip-update");
                    }
                    catch (Exception ex)
                    {
                        Log("Could not start updated version: " + ex.Message);
                        // Keep using the existing version for this run.
                        Cleanup.Run(LoadSettings());
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);

                if (!startup)
                {
                    MessageBox.Show(
                        ex.Message +
                        "\r\n\r\nNo cleanup was started by opening this window.",
                        "Plant 3D Cache Cleaner",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        internal static bool HasArgument(string[] args, string value)
        {
            foreach (string arg in args)
                if (String.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private static string Arguments(bool startup, bool skipUpdate)
        {
            return (startup ? "--startup " : "") +
                   (skipUpdate ? "--skip-update" : "");
        }

        internal static void Start(string file, string arguments)
        {
            Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file)
            });
        }

        internal static bool SamePath(string a, string b)
        {
            return String.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string InstallAndFindNewest()
        {
            string ownDirectory = Path.Combine(Versions, Version.ToString());
            string ownInstalled = Path.Combine(
                ownDirectory, "Plant3DCacheCleaner.exe");

            Directory.CreateDirectory(ownDirectory);

            if (!SamePath(Exe, ownInstalled) && !File.Exists(ownInstalled))
            {
                string temporary = ownInstalled + ".installing";
                File.Copy(Exe, temporary, true);
                File.Move(temporary, ownInstalled);
            }

            string newest = ownInstalled;
            Version newestVersion = Version;

            foreach (string directory in Directory.GetDirectories(Versions))
            {
                string candidate = Path.Combine(
                    directory, "Plant3DCacheCleaner.exe");

                if (!File.Exists(candidate))
                    continue;

                try
                {
                    AssemblyName identity = AssemblyName.GetAssemblyName(candidate);

                    if (identity.Name == "Plant3DCacheCleaner" &&
                        identity.Version.CompareTo(newestVersion) > 0)
                    {
                        newest = candidate;
                        newestVersion = identity.Version;
                    }
                }
                catch
                {
                    // Ignore incomplete or invalid local files.
                }
            }

            return newest;
        }

        internal static Settings LoadSettings()
        {
            if (!File.Exists(SettingsPath))
                return new Settings();

            Settings settings;

            try
            {
                settings = Json.Deserialize<Settings>(
                    File.ReadAllText(SettingsPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "The settings file could not be read. Cleanup was not started. " +
                    "Settings location: " + SettingsPath, ex);
            }

            if (settings == null || settings.Schema != 1)
                throw new InvalidDataException("Unsupported or invalid settings file.");

            if (settings.TempMinimumAgeDays < 1 ||
                settings.TempMinimumAgeDays > 365)
                throw new InvalidDataException("Invalid temporary-file age setting.");

            return settings;
        }

        internal static void SaveSettings(Settings settings)
        {
            Directory.CreateDirectory(Root);

            string temporary = SettingsPath + ".tmp";

            File.WriteAllText(
                temporary,
                Json.Serialize(settings),
                new UTF8Encoding(false));

            if (File.Exists(SettingsPath))
                File.Replace(temporary, SettingsPath, null);
            else
                File.Move(temporary, SettingsPath);
        }

        internal static void SyncShortcuts(Settings settings)
        {
            CreateShortcut(MenuShortcut, Exe, "");

            if (settings.RunAtSignIn)
                CreateShortcut(StartupShortcut, Exe, "--startup");
            else if (File.Exists(StartupShortcut))
                File.Delete(StartupShortcut);
        }

        private static void CreateShortcut(
            string shortcutPath, string executable, string arguments)
        {
            object shell = null;
            object shortcut = null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");

                if (shellType == null)
                    throw new InvalidOperationException(
                        "Windows shortcut creation is unavailable.");

                shell = Activator.CreateInstance(shellType);

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });

                Type shortcutType = shortcut.GetType();

                SetComProperty(shortcutType, shortcut, "TargetPath", executable);
                SetComProperty(shortcutType, shortcut, "Arguments", arguments);
                SetComProperty(
                    shortcutType, shortcut, "WorkingDirectory",
                    Path.GetDirectoryName(executable));
                SetComProperty(
                    shortcutType, shortcut, "Description",
                    "Configure or run Plant 3D cache cleanup");
                SetComProperty(
                    shortcutType, shortcut, "IconLocation",
                    executable + ",0");

                shortcutType.InvokeMember(
                    "Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);

                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void SetComProperty(
            Type type, object instance, string name, object value)
        {
            type.InvokeMember(
                name, BindingFlags.SetProperty,
                null, instance, new object[] { value });
        }

        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Root);

                if (File.Exists(LogPath) &&
                    new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                {
                    string previous = LogPath + ".previous";

                    if (File.Exists(previous))
                        File.Delete(previous);

                    File.Move(LogPath, previous);
                }

                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // A logging failure must not crash Windows sign-in.
            }
        }

        private static byte[] Download(string url, int maximumBytes)
        {
            Uri uri = new Uri(url);

            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Only HTTPS downloads are allowed.");

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            HttpWebRequest request =
                (HttpWebRequest)WebRequest.Create(uri);

            request.UserAgent = "Plant3DCacheCleaner/" + Version;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (WebResponse response = request.GetResponse())
            {
                if (response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Insecure download redirect.");

                if (response.ContentLength > maximumBytes)
                    throw new InvalidDataException("Download exceeds size limit.");

                using (Stream input = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int read;

                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maximumBytes)
                            throw new InvalidDataException(
                                "Download exceeds size limit.");

                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
        }

        private static string AssetUrl(
            Dictionary<string, object> release, string assetName)
        {
            object[] assets = release["assets"] as object[];

            if (assets == null)
                throw new InvalidDataException("Release has no readable assets.");

            foreach (object entry in assets)
            {
                Dictionary<string, object> asset =
                    entry as Dictionary<string, object>;

                if (asset != null &&
                    String.Equals(
                        Convert.ToString(asset["name"]),
                        assetName,
                        StringComparison.Ordinal))
                {
                    return Convert.ToString(asset["browser_download_url"]);
                }
            }

            throw new InvalidDataException(
                "Missing release asset: " + assetName);
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(bytes))
                    .Replace("-", "").ToLowerInvariant();
            }
        }

        private static string TryDownloadUpdate()
        {
            try
            {
                Log("Checking GitHub Releases for updates.");

                string api = "https://api.github.com/repos/" +
                    Repository + "/releases/latest";

                Dictionary<string, object> release =
                    Json.DeserializeObject(
                        Encoding.UTF8.GetString(Download(api, 2 * 1024 * 1024)))
                    as Dictionary<string, object>;

                if (release == null)
                    throw new InvalidDataException("Invalid GitHub response.");

                byte[] manifestBytes =
                    Download(AssetUrl(release, "update.json"), 64 * 1024);

                byte[] signature =
                    Download(AssetUrl(release, "update.sig"), 8192);

                CspParameters parameters = new CspParameters(24);

                using (RSACryptoServiceProvider rsa =
                    new RSACryptoServiceProvider(parameters))
                {
                    rsa.PersistKeyInCsp = false;

                    rsa.FromXmlString(
                        Encoding.UTF8.GetString(
                            Convert.FromBase64String(PublicKeyBase64)));

                    if (!rsa.VerifyData(
                        manifestBytes,
                        CryptoConfig.MapNameToOID("SHA256"),
                        signature))
                    {
                        throw new CryptographicException(
                            "Update signature verification failed.");
                    }
                }

                // Parse only after verifying the manifest signature.
                UpdateManifest manifest =
                    Json.Deserialize<UpdateManifest>(
                        Encoding.UTF8.GetString(manifestBytes));

                Version offered;

                if (manifest == null ||
                    !System.Version.TryParse(manifest.version, out offered) ||
                    manifest.sha256 == null ||
                    manifest.sha256.Length != 64)
                {
                    throw new InvalidDataException("Invalid signed update manifest.");
                }

                if (offered.CompareTo(Version) <= 0)
                {
                    Log("Installed version is up to date.");
                    return null;
                }

                byte[] executable = Download(
                    AssetUrl(release, "Plant3DCacheCleaner.exe"),
                    32 * 1024 * 1024);

                if (!String.Equals(
                    Sha256(executable), manifest.sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException(
                        "Downloaded executable checksum does not match.");
                }

                string directory = Path.Combine(Versions, offered.ToString());
                Directory.CreateDirectory(directory);

                string destination = Path.Combine(
                    directory, "Plant3DCacheCleaner.exe");
                string temporary = destination + ".download";

                File.WriteAllBytes(temporary, executable);

                AssemblyName identity = AssemblyName.GetAssemblyName(temporary);

                if (identity.Name != "Plant3DCacheCleaner" ||
                    identity.Version != offered)
                {
                    File.Delete(temporary);

                    throw new InvalidDataException(
                        "Executable identity does not match the signed manifest.");
                }

                if (File.Exists(destination))
                {
                    if (!String.Equals(
                        Sha256(File.ReadAllBytes(destination)),
                        manifest.sha256,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(destination);
                        File.Move(temporary, destination);
                    }
                    else
                    {
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, destination);
                }

                Log("Verified and installed update " + offered + ".");
                return destination;
            }
            catch (Exception ex)
            {
                Log("Update unavailable or rejected; keeping current version. " +
                    ex.Message);
                return null;
            }
        }
    }

    internal static class Cleanup
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(
            IntPtr window, string rootPath, uint flags);

        private static int deleted;
        private static int skipped;

        internal static string Run(Settings settings)
        {
            using (Mutex mutex = new Mutex(
                false, @"Local\Plant3DCacheCleaner.Cleanup"))
            {
                bool acquired = false;

                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        return "Another cleanup is already running.";

                    deleted = 0;
                    skipped = 0;

                    App.Log("Cleanup started. Version " + App.Version);

                    bool acadRunning = IsAutoCadRunning();

                    if (acadRunning)
                    {
                        App.Log(
                            "Autodesk cache tasks skipped because an acad process " +
                            "is running, or its state could not be checked.");
                    }
                    else
                    {
                        RunTask("Plant 3D profile caches", delegate
                        {
                            CleanPlantCaches(settings);
                        });

                        if (settings.PersistentCache)
                        {
                            RunTask("Shared Autodesk persistent cache", delegate
                            {
                                string target = Path.Combine(
                                    Environment.GetFolderPath(
                                        Environment.SpecialFolder.ApplicationData),
                                    @"Autodesk\PnPPersistentCache");

                                ClearDirectory(target, null);
                            });
                        }
                    }

                    if (settings.WindowsTemp)
                    {
                        RunTask("Current user's temporary files", delegate
                        {
                            ClearDirectory(
                                Path.GetTempPath(),
                                DateTime.UtcNow.AddDays(
                                    -settings.TempMinimumAgeDays));
                        });
                    }

                    if (settings.InternetCache)
                    {
                        RunTask("Legacy Windows Internet cache", delegate
                        {
                            string rundll = Path.Combine(
                                Environment.GetFolderPath(
                                    Environment.SpecialFolder.System),
                                "rundll32.exe");

                            using (Process process = Process.Start(
                                new ProcessStartInfo(
                                    rundll,
                                    "InetCpl.cpl,ClearMyTracksByProcess 8")
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                }))
                            {
                                if (process == null)
                                    throw new IOException(
                                        "Could not start the Windows cache command.");

                                if (!process.WaitForExit(60000))
                                    App.Log(
                                        "Internet cache command is still running; " +
                                        "cleanup completion is not verified.");
                                else
                                    App.Log(
                                        "Internet cache command exited with code " +
                                        process.ExitCode +
                                        "; cache contents were not independently verified.");
                            }
                        });
                    }

                    if (settings.RecycleBin)
                    {
                        RunTask("Recycle Bin", delegate
                        {
                            // No confirmation, progress UI, or sound.
                            // Consent is collected when enabling this setting.
                            int result = SHEmptyRecycleBin(
                                IntPtr.Zero, null, 1u | 2u | 4u);

                            App.Log(
                                "Recycle Bin API result: 0x" +
                                result.ToString("X8"));

                            if (result < 0)
                                Marshal.ThrowExceptionForHR(result);
                        });
                    }

                    string summary =
                        "Cleanup finished.\r\n\r\n" +
                        "Files deleted from cache/temp folders: " + deleted +
                        "\r\nSkipped entries or task errors: " + skipped +
                        "\r\n\r\nSee the log for individual task results.";

                    App.Log(summary.Replace("\r\n", " "));
                    return summary;
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static bool IsAutoCadRunning()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("acad");
                bool running = processes.Length > 0;

                foreach (Process process in processes)
                    process.Dispose();

                return running;
            }
            catch
            {
                // Fail closed for Autodesk cache deletion.
                return true;
            }
        }

        private static void RunTask(string name, Action action)
        {
            App.Log("Task: " + name);

            try
            {
                action();
            }
            catch (Exception ex)
            {
                skipped++;
                App.Log("Task error: " + name + ": " + ex.Message);
            }
        }

        private static void CleanPlantCaches(Settings settings)
        {
            if (!settings.ExternalDataCache &&
                !settings.GraphicsCache &&
                !settings.BrowserCache)
                return;

            string autodesk = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Autodesk");

            if (!Directory.Exists(autodesk))
            {
                App.Log("No local Autodesk profile directory found.");
                return;
            }

            string[] products = Directory.GetDirectories(
                autodesk, "Autodesk AutoCAD Plant 3D*");

            foreach (string product in products)
            {
                if (HasReparseAncestor(product))
                {
                    App.Log("Skipped linked profile directory: " + product);
                    continue;
                }

                foreach (string release in Directory.GetDirectories(product, "R*"))
                {
                    if (HasReparseAncestor(release))
                        continue;

                    // Discover existing language/profile children rather than
                    // assuming only an "enu" directory exists.
                    foreach (string language in Directory.GetDirectories(release))
                    {
                        if (HasReparseAncestor(language))
                            continue;

                        if (settings.ExternalDataCache)
                            ClearDirectory(
                                Path.Combine(language, "ExternalDataCache"), null);

                        if (settings.GraphicsCache)
                            ClearDirectory(
                                Path.Combine(language, "GraphicsCache"), null);

                        if (settings.BrowserCache)
                            ClearDirectory(
                                Path.Combine(language, "BrowserCache"), null);
                    }
                }
            }
        }

        private static bool HasReparseAncestor(string path)
        {
            DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(path));

            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;

                current = current.Parent;
            }

            return false;
        }

        private static void ClearDirectory(string directory, DateTime? olderThan)
        {
            if (!Directory.Exists(directory))
            {
                App.Log("Folder not found: " + directory);
                return;
            }

            string fullPath = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(fullPath);

            if (String.Equals(
                fullPath.TrimEnd('\\'),
                root.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Refusing to clean a drive root.");
            }

            if (HasReparseAncestor(fullPath))
            {
                skipped++;
                App.Log("Skipped linked/reparse-point path: " + fullPath);
                return;
            }

            App.Log(
                "Cleaning: " + fullPath +
                (olderThan.HasValue
                    ? " | files older than " + olderThan.Value.ToString("u")
                    : ""));

            DeleteContents(fullPath, olderThan);
        }

        private static void DeleteContents(string directory, DateTime? olderThan)
        {
            string[] entries;

            try
            {
                if ((File.GetAttributes(directory) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    return;
                }

                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex)
            {
                skipped++;
                App.Log("Cannot read directory: " + directory + ": " + ex.Message);
                return;
            }

            foreach (string entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        App.Log("Skipped link: " + entry);
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        DeleteContents(entry, olderThan);

                        // Keep the selected root folder; remove only empty
                        // subdirectories. Do not recursively delete blindly.
                        if (Directory.GetFileSystemEntries(entry).Length == 0)
                            Directory.Delete(entry, false);
                    }
                    else
                    {
                        if (olderThan.HasValue &&
                            File.GetLastWriteTimeUtc(entry) >= olderThan.Value)
                            continue;

                        if ((attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(
                                entry, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.Delete(entry);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    App.Log("Skipped entry: " + entry + ": " + ex.Message);
                }
            }
        }
    }

    internal sealed class SettingsWindow : Form
    {
        private Settings saved;

        private CheckBox external;
        private CheckBox graphics;
        private CheckBox browser;
        private CheckBox persistent;

        private CheckBox temp;
        private NumericUpDown tempAge;
        private CheckBox internet;
        private CheckBox recycle;

        private CheckBox startup;
        private CheckBox updates;

        private Button save;
        private Button run;
        private Label status;
        private bool busy;

        internal SettingsWindow(Settings settings)
        {
            saved = settings;

            Text = "Plant 3D Cache Cleaner - " + App.Version;
            ClientSize = new Size(650, 675);
            MinimumSize = new Size(610, 650);
            StartPosition = FormStartPosition.CenterScreen;

            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.WrapContents = false;
            panel.AutoScroll = true;
            panel.Padding = new Padding(18);
            Controls.Add(panel);

            AddHeading(panel, "Plant 3D caches");

            external = AddCheck(
                panel, "External Data Cache", saved.ExternalDataCache);
            graphics = AddCheck(
                panel, "Graphics Cache", saved.GraphicsCache);
            browser = AddCheck(
                panel, "Browser Cache", saved.BrowserCache);
            persistent = AddCheck(
                panel, "Shared Autodesk Persistent Cache",
                saved.PersistentCache);

            AddText(
                panel,
                "Autodesk cache tasks are skipped when AutoCAD/Plant 3D is " +
                "detected running. The persistent cache can be shared by " +
                "multiple Autodesk profiles.",
                48);

            AddHeading(panel, "Additional cleanup");

            temp = AddCheck(
                panel, "Current user's Windows temporary files",
                saved.WindowsTemp);

            FlowLayoutPanel ageRow = new FlowLayoutPanel();
            ageRow.AutoSize = true;
            ageRow.WrapContents = false;

            Label ageLabel = new Label();
            ageLabel.Text = "Only files older than this many days:";
            ageLabel.AutoSize = true;
            ageLabel.Padding = new Padding(0, 5, 0, 0);

            tempAge = new NumericUpDown();
            tempAge.Minimum = 1;
            tempAge.Maximum = 365;
            tempAge.Value = saved.TempMinimumAgeDays;
            tempAge.Width = 65;

            ageRow.Controls.Add(ageLabel);
            ageRow.Controls.Add(tempAge);
            panel.Controls.Add(ageRow);

            internet = AddCheck(
                panel,
                "Temporary Internet Files - legacy Windows Internet cache",
                saved.InternetCache);

            recycle = AddCheck(
                panel,
                "Empty my Recycle Bin - permanent deletion",
                saved.RecycleBin);

            AddText(
                panel,
                "Internet cache cleanup does not clear Chrome or Edge browsing " +
                "data. Recycle Bin cleanup may include multiple drives.",
                36);

            AddHeading(panel, "Startup and updates");

            startup = AddCheck(
                panel, "Run automatically at Windows sign-in",
                saved.RunAtSignIn);

            updates = AddCheck(
                panel, "Check GitHub Releases for updates at sign-in",
                saved.CheckForUpdates);

            AddText(
                panel,
                "Opening this app normally shows settings without starting " +
                "cleanup. Saved selections are used for automatic runs.",
                38);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;

            save = new Button();
            save.Text = "Save";
            save.AutoSize = true;
            save.Click += delegate { SaveChoices(); };

            run = new Button();
            run.Text = "Run cleanup now";
            run.AutoSize = true;
            run.Click += async delegate
            {
                if (!SaveChoices())
                    return;

                if (MessageBox.Show(
                    this,
                    "Run all selected cleanup tasks now?\r\n\r\n" +
                    "Deleted files may not be recoverable.",
                    "Confirm cleanup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                busy = true;
                save.Enabled = false;
                run.Enabled = false;
                panel.Enabled = false;
                status.Text = "Cleanup is running...";

                try
                {
                    Settings snapshot = saved;
                    string result = await Task.Run(
                        delegate { return Cleanup.Run(snapshot); });

                    status.Text = "Cleanup finished. Review the log for details.";

                    MessageBox.Show(
                        this, result, "Cleanup result",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    App.Log("Manual cleanup error: " + ex);
                    MessageBox.Show(this, ex.Message, "Cleanup error");
                }
                finally
                {
                    panel.Enabled = true;
                    save.Enabled = true;
                    run.Enabled = true;
                    busy = false;
                }
            };

            Button log = new Button();
            log.Text = "View log";
            log.AutoSize = true;
            log.Click += delegate
            {
                try
                {
                    App.Log("Log opened from settings.");

                    Process.Start(
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.System),
                            "notepad.exe"),
                        "\"" + App.LogPath + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not open log");
                }
            };

            buttons.Controls.Add(save);
            buttons.Controls.Add(run);
            buttons.Controls.Add(log);
            panel.Controls.Add(buttons);

            status = new Label();
            status.Width = 590;
            status.Height = 42;
            status.Text = File.Exists(App.SettingsPath)
                ? "Settings loaded. Changes take effect after Save."
                : "Choose your options and click Save to complete setup.";
            panel.Controls.Add(status);

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy)
                {
                    e.Cancel = true;
                    MessageBox.Show(
                        this,
                        "Please wait until the current cleanup finishes.",
                        "Cleanup is running");
                }
            };

            // Repoint installed shortcuts after a version update.
            if (File.Exists(App.SettingsPath))
            {
                try
                {
                    App.SyncShortcuts(saved);
                }
                catch (Exception ex)
                {
                    status.Text = "Shortcut update failed: " + ex.Message;
                    App.Log(status.Text);
                }
            }
        }

        private bool SaveChoices()
        {
            Settings next = new Settings();

            next.ExternalDataCache = external.Checked;
            next.GraphicsCache = graphics.Checked;
            next.BrowserCache = browser.Checked;
            next.PersistentCache = persistent.Checked;

            next.WindowsTemp = temp.Checked;
            next.TempMinimumAgeDays = (int)tempAge.Value;
            next.InternetCache = internet.Checked;
            next.RecycleBin = recycle.Checked;

            next.RunAtSignIn = startup.Checked;
            next.CheckForUpdates = updates.Checked;

            if (next.RecycleBin && !saved.RecycleBin)
            {
                if (MessageBox.Show(
                    this,
                    "Enabling this option permanently empties your Recycle Bin " +
                    "whenever a selected cleanup runs, including automatic " +
                    "sign-in cleanup if enabled.\r\n\r\n" +
                    "This can affect your Recycle Bin on multiple drives. " +
                    "Files cannot be restored through the Recycle Bin afterward." +
                    "\r\n\r\nEnable this option?",
                    "Enable Recycle Bin cleanup?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    recycle.Checked = false;
                    return false;
                }
            }

            try
            {
                App.SaveSettings(next);
                saved = next;
                App.SyncShortcuts(next);

                status.Text =
                    "Settings saved. Future cleanup runs use these selections.";

                return true;
            }
            catch (Exception ex)
            {
                App.Log("Settings/shortcut save error: " + ex);

                MessageBox.Show(
                    this,
                    "Could not complete saving settings and shortcuts:\r\n\r\n" +
                    ex.Message +
                    "\r\n\r\nSome changes may already have been saved.",
                    "Save error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return false;
            }
        }

        private static CheckBox AddCheck(
            FlowLayoutPanel panel, string text, bool value)
        {
            CheckBox checkbox = new CheckBox();
            checkbox.Text = text;
            checkbox.Checked = value;
            checkbox.AutoSize = true;
            checkbox.Margin = new Padding(3, 4, 3, 4);
            panel.Controls.Add(checkbox);
            return checkbox;
        }

        private static void AddHeading(FlowLayoutPanel panel, string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = new Font(
                SystemFonts.MessageBoxFont, FontStyle.Bold);
            label.Margin = new Padding(3, 12, 3, 5);
            panel.Controls.Add(label);
        }

        private static void AddText(
            FlowLayoutPanel panel, string text, int height)
        {
            Label label = new Label();
            label.Text = text;
            label.Width = 590;
            label.Height = height;
            label.ForeColor = SystemColors.GrayText;
            panel.Controls.Add(label);
        }
    }
}
