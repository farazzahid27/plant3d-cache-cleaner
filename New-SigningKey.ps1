<#
Run this ONCE on your own PC (Windows PowerShell):

    .\New-SigningKey.ps1 -Repository yourname/plant3d-cache-cleaner

- If you already built with the old Build.ps1, your existing key is reused.
  That matters: copies already installed only accept updates signed with it.
- Otherwise a new key is created.
- The key is copied to the clipboard in the form GitHub needs. Paste it into
  the repository secret UPDATE_SIGNING_KEY, then clear the clipboard.

The key file stays in %LOCALAPPDATA%\Plant3DCacheCleanerPublisher, outside the
repository. Back it up somewhere safe (e.g. a password manager). Never commit it.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository
)

$ErrorActionPreference = 'Stop'

# Same location the original Build.ps1 used, so an existing key is found.
$keyDirectory = Join-Path $env:LOCALAPPDATA 'Plant3DCacheCleanerPublisher'
$keyFile = Join-Path $keyDirectory (($Repository -replace '/', '_') + '.private.xml')

New-Item -ItemType Directory -Force -Path $keyDirectory | Out-Null

if (Test-Path -LiteralPath $keyFile) {
    Write-Host "Existing key found and reused: $keyFile" -ForegroundColor Yellow
    $privateKeyXml = [IO.File]::ReadAllText($keyFile)
}
else {
    $csp = New-Object System.Security.Cryptography.CspParameters
    $csp.ProviderType = 24
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList 3072, $csp
    $rsa.PersistKeyInCsp = $false
    try {
        $privateKeyXml = $rsa.ToXmlString($true)
    }
    finally {
        $rsa.Dispose()
    }

    [IO.File]::WriteAllText(
        $keyFile,
        $privateKeyXml,
        (New-Object Text.UTF8Encoding -ArgumentList $false)
    )
    Write-Host "New key created: $keyFile" -ForegroundColor Green
}

$secretValue = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($privateKeyXml))

Set-Clipboard -Value $secretValue

Write-Host ''
Write-Host 'The secret value is now on your clipboard.' -ForegroundColor Green
Write-Host 'In GitHub: repository > Settings > Secrets and variables > Actions >'
Write-Host '           New repository secret'
Write-Host '    Name:  UPDATE_SIGNING_KEY'
Write-Host '    Value: paste (Ctrl+V)'
Write-Host ''
Write-Host 'Afterwards, clear the clipboard (copy something else).'
Write-Host 'Back up the key file. If it is lost, installed copies can no longer be updated.'
