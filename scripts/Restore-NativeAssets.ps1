[CmdletBinding()]
param([string]$ZipPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content (Join-Path $PSScriptRoot 'native-assets.json') -Raw | ConvertFrom-Json
$assets = Join-Path $root 'src/Desktop/BiliCopilot.UI/Assets'
function Test-Hash([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Expected)
}
$missing = @($manifest.files.PSObject.Properties | Where-Object { -not (Test-Hash (Join-Path $assets $_.Name) $_.Value) })
if ($missing.Count -eq 0) { Write-Host 'Native assets already match all pinned hashes.'; return }
if (-not $ZipPath) {
    $ZipPath = Join-Path $root 'artifacts/native-assets-2611.3.zip'
    New-Item -ItemType Directory -Force -Path (Split-Path $ZipPath -Parent) | Out-Null
    if (-not (Test-Hash $ZipPath $manifest.sha256)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $manifest.url -OutFile $ZipPath
    }
}
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
if (-not (Test-Hash $ZipPath $manifest.sha256)) { throw "Native assets ZIP SHA256 mismatch: $ZipPath" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    $expected = @($manifest.files.PSObject.Properties.Name)
    if (@(Compare-Object $names $expected).Count -ne 0 -or $names.Count -ne $expected.Count) { throw 'Unexpected ZIP file set.' }
    # Check every entry before changing any destination; never trust archive paths.
    foreach ($entry in $archive.Entries) {
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($hash -cne $manifest.files.($entry.FullName)) { throw "Native asset SHA256 mismatch: $($entry.FullName)" }
    }
    foreach ($file in $missing) {
        $to = Join-Path $assets $file.Name
        New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($file.Name), $to, $true)
        if (-not (Test-Hash $to $file.Value)) { throw "Extracted SHA256 mismatch: $to" }
        Write-Host "Restored $($file.Name)"
    }
} finally { $archive.Dispose() }
