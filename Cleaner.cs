using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Plant3DCacheCleaner
{
    /// <summary>The cleanup work that used to live in the .bat file.</summary>
    internal static class Cleaner
    {
        // Folder-name patterns under %LOCALAPPDATA%\Autodesk that hold Plant 3D versions.
        private static readonly string[] Plant3DPatterns = { "Autodesk AutoCAD Plant 3D*", "AutoCAD Plant 3D*" };

        // AutoCAD-based processes; caches are skipped while one of these is running.
        private static readonly string[] AutoCadProcesses = { "acad", "accoreconsole" };

        public static string Run(Settings s)
        {
            Log.Write("=== Cleanup started (version " + Updater.CurrentVersion + ") ===");
            var summary = new List<string>();

            if (s.AnyPlant3D)
            {
                if (IsAutoCadRunning())
                {
                    Log.Write("AutoCAD / Plant 3D is running - Plant 3D caches were skipped.");
                    summary.Add("Plant 3D caches skipped (AutoCAD is open)");
                }
                else
                {
                    summary.Add(CleanPlant3D(s));
                }
            }

            if (s.WindowsTemp) summary.Add(CleanWindowsTemp());
            if (s.InternetFiles) summary.Add(ClearInternetFiles());
            if (s.RecycleBin) summary.Add(EmptyRecycleBin());

            Log.Write("=== Cleanup finished ===");
            return summary.Count == 0 ? "Nothing selected." : string.Join(" | ", summary);
        }

        private static bool IsAutoCadRunning()
        {
            foreach (string name in AutoCadProcesses)
                if (Process.GetProcessesByName(name).Length > 0) return true;
            return false;
        }

        private static string CleanPlant3D(Settings s)
        {
            int versions = 0;
            string autodeskLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk");

            var releaseDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(autodeskLocal))
            {
                foreach (string pattern in Plant3DPatterns)
                    foreach (string productDir in Directory.GetDirectories(autodeskLocal, pattern))
                        foreach (string releaseDir in Directory.GetDirectories(productDir, "R*"))
                            releaseDirs.Add(releaseDir);
            }

            foreach (string releaseDir in releaseDirs)
            {
                versions++;
                string product = Path.GetFileName(Path.GetDirectoryName(releaseDir));
                Log.Write("Found " + product + " (" + Path.GetFileName(releaseDir) + ")");

                // Language folders: enu, deu, fra, ...
                foreach (string langDir in Directory.GetDirectories(releaseDir))
                {
                    if (s.ExternalDataCache) EmptyIfExists(Path.Combine(langDir, "ExternalDataCache"));
                    if (s.GraphicsCache) EmptyIfExists(Path.Combine(langDir, "GraphicsCache"));
                    if (s.BrowserCache) EmptyIfExists(Path.Combine(langDir, "BrowserCache"));
                }
            }

            if (versions == 0) Log.Write("No AutoCAD Plant 3D installation found.");

            // Shared across all versions, so it is cleaned once rather than per version.
            if (s.PersistentCache)
            {
                string persistent = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "PnPPersistentCache");
                EmptyIfExists(persistent);
            }

            return versions == 0 ? "No Plant 3D versions found" : "Plant 3D caches cleaned (" + versions + " version(s))";
        }

        private static string CleanWindowsTemp()
        {
            string temp = Path.GetTempPath();
            Log.Write("Purging temporary files in " + temp);
            int skipped = EmptyDirectory(temp);
            return "Temp files cleaned" + (skipped > 0 ? " (" + skipped + " in use, skipped)" : "");
        }

        private static string ClearInternetFiles()
        {
            Log.Write("Purging Temporary Internet Files...");
            try
            {
                var psi = new ProcessStartInfo("RunDll32.exe", "InetCpl.cpl,ClearMyTracksByProcess 8")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null && !p.WaitForExit(120000)) Log.Write("Internet cache cleanup is still running in the background.");
                }
                return "Internet files cleaned";
            }
            catch (Exception ex)
            {
                Log.Write("Internet cache cleanup failed: " + ex.Message);
                return "Internet files: failed";
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
        private const int E_UNEXPECTED = unchecked((int)0x8000FFFF); // returned when the bin is already empty

        private static string EmptyRecycleBin()
        {
            Log.Write("Emptying Recycle Bin...");
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr == 0) return "Recycle Bin emptied";
            if (hr == E_UNEXPECTED) { Log.Write("Recycle Bin was already empty."); return "Recycle Bin already empty"; }
            Log.Write("Emptying Recycle Bin failed (0x" + hr.ToString("X8") + ")");
            return "Recycle Bin: failed";
        }

        private static void EmptyIfExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Log.Write("Folder not found: " + path);
                return;
            }
            int skipped = EmptyDirectory(path);
            Log.Write("Purged " + path + (skipped > 0 ? " (" + skipped + " item(s) in use, skipped)" : ""));
        }

        /// <summary>Deletes everything inside a folder but keeps the folder. Returns the number of items that could not be removed.</summary>
        private static int EmptyDirectory(string path)
        {
            int skipped = 0;
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(path);
                dirs = Directory.GetDirectories(path);
            }
            catch (Exception ex)
            {
                Log.Write("Cannot read " + path + ": " + ex.Message);
                return 1;
            }

            foreach (string f in files)
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                catch { skipped++; }
            }

            foreach (string d in dirs)
            {
                try { DeleteTree(d); }
                catch { skipped++; }
            }
            return skipped;
        }

        private static void DeleteTree(string dir)
        {
            var info = new DirectoryInfo(dir);
            // Never follow junctions/symlinks - remove the link only.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete(false);
                return;
            }
            foreach (FileInfo f in info.GetFiles())
            {
                f.Attributes = FileAttributes.Normal;
                f.Delete();
            }
            foreach (DirectoryInfo sub in info.GetDirectories()) DeleteTree(sub.FullName);
            info.Attributes = FileAttributes.Normal;
            info.Delete(false);
        }
    }
}
