param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0.0'
)

# Signing key source, in this order:
#   1. Environment variable UPDATE_SIGNING_KEY (base64 of the private key XML).
#      GitHub Actions sets this from the repository secret.
#   2. Local key file created by New-SigningKey.ps1 (for building on your own PC).
# This script NEVER creates a key. A new key would break updates for every
# copy that is already installed, so a missing key is an error.

$ErrorActionPreference = 'Stop'

$project = $PSScriptRoot
$sourceFile = Join-Path $project 'Cleaner.cs'
$buildDirectory = Join-Path $project 'build'
$distDirectory = Join-Path $project 'dist'

if (-not (Test-Path -LiteralPath $sourceFile)) {
    throw "Missing source file: $sourceFile"
}

$parsedVersion = [version]$Version

foreach ($part in @(
    $parsedVersion.Major,
    $parsedVersion.Minor,
    $parsedVersion.Build,
    $parsedVersion.Revision
)) {
    if ($part -lt 0 -or $part -gt 65534) {
        throw 'Each version component must be between 0 and 65534.'
    }
}

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}

$compiler = Join-Path $framework 'csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found.'
}

# ---- Load the signing key -------------------------------------------------

if ($env:UPDATE_SIGNING_KEY) {
    try {
        $privateKeyXml = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($env:UPDATE_SIGNING_KEY.Trim())
        )
    }
    catch {
        throw 'UPDATE_SIGNING_KEY is not valid base64. Re-run New-SigningKey.ps1 and update the secret.'
    }
    Write-Host 'Using signing key from UPDATE_SIGNING_KEY.'
}
else {
    $keyDirectory = Join-Path $env:LOCALAPPDATA 'Plant3DCacheCleanerPublisher'
    $keyFile = Join-Path $keyDirectory (($Repository -replace '/', '_') + '.private.xml')

    if (-not (Test-Path -LiteralPath $keyFile)) {
        throw "No signing key found. Run New-SigningKey.ps1 -Repository $Repository first."
    }

    $privateKeyXml = [IO.File]::ReadAllText($keyFile)
    Write-Host "Using local signing key: $keyFile"
}

New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null

$csp = New-Object System.Security.Cryptography.CspParameters
$csp.ProviderType = 24

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
$rsa.PersistKeyInCsp = $false

try {
    $rsa.FromXmlString($privateKeyXml)

    if ($rsa.PublicOnly) {
        throw 'The signing key contains only a public key; the private key is required.'
    }

    # ---- Generate source and compile ---------------------------------------

    $publicKeyBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($false))
    )

    $source = [IO.File]::ReadAllText($sourceFile)
    $source = $source.Replace('__VERSION__', $Version)
    $source = $source.Replace('__REPOSITORY__', $Repository)
    $source = $source.Replace('__PUBLIC_KEY__', $publicKeyBase64)

    $generatedSource = Join-Path $buildDirectory 'Cleaner.generated.cs'
    $executable = Join-Path $distDirectory 'Plant3DCacheCleaner.exe'

    [IO.File]::WriteAllText(
        $generatedSource,
        $source,
        (New-Object Text.UTF8Encoding -ArgumentList $false)
    )

    $compilerArguments = @(
        '/nologo'
        '/target:winexe'
        '/platform:anycpu'
        '/optimize+'
        '/debug-'
        "/out:$executable"
        "/reference:$(Join-Path $framework 'System.dll')"
        "/reference:$(Join-Path $framework 'System.Core.dll')"
        "/reference:$(Join-Path $framework 'System.Drawing.dll')"
        "/reference:$(Join-Path $framework 'System.Windows.Forms.dll')"
        "/reference:$(Join-Path $framework 'System.Web.Extensions.dll')"
        $generatedSource
    )

    & $compiler @compilerArguments

    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE."
    }

    # ---- Sign the update manifest ------------------------------------------

    # If adding an Authenticode signature, sign the EXE BEFORE hashing it.
    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()

    $manifest = [ordered]@{
        version = $Version
        sha256  = $hash
    } | ConvertTo-Json -Compress

    $manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifest)

    $signature = $rsa.SignData(
        $manifestBytes,
        [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    )

    [IO.File]::WriteAllBytes((Join-Path $distDirectory 'update.json'), $manifestBytes)
    [IO.File]::WriteAllBytes((Join-Path $distDirectory 'update.sig'), $signature)

    # The generated source contains nothing secret, but it is not needed after the build.
    Remove-Item -LiteralPath $generatedSource -Force

    Write-Host ''
    Write-Host 'Build completed.' -ForegroundColor Green
    Write-Host "Version:    $Version"
    Write-Host "Repository: $Repository"
    Write-Host "Output:     $distDirectory (Plant3DCacheCleaner.exe, update.json, update.sig)"
}
finally {
    $rsa.Dispose()
}
