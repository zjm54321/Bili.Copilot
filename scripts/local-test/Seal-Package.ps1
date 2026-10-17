[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DeliveryRoot,
    [Parameter(Mandatory = $true)][string]$LooseExePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedLooseExeSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedSignerCertificateSha256,
    [string]$Version = '2.2611.2.0'
)
# FINAL BATCH ONLY. Does not sign, find keys, import trust, install or extract a package.
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LocalTest.psm1') -Force
Assert-LTWindows
Assert-LTVersion $Version
$root = [IO.Path]::GetFullPath($DeliveryRoot)
Assert-LTPlainPath $root
$manifestPath = Join-Path $root 'test-artifacts.json'
if (Test-Path -LiteralPath $manifestPath) { Stop-LT 'AlreadySealed' 'Never overwrite a sealed version; allocate a new output and increment the version for an upgrade.' }
$packageName = "BiliCopilot.LocalTest_${Version}_x64.msix"
$allowed = @('Install.ps1', 'LocalTest.psm1', 'channel.json', $packageName)
$items = @(Get-ChildItem -LiteralPath $root -Force)
if ($items.Count -ne $allowed.Count -or @($items | Where-Object { $_.PSIsContainer -or $allowed -cnotcontains $_.Name }).Count -ne 0) {
    Stop-LT 'DeliveryInventory' 'Delivery must contain exactly the signed versioned MSIX and three new installer support files, with no private material.'
}
$locks = New-Object 'System.Collections.Generic.List[System.IDisposable]'
try {
    $exe = Open-LTPinned $LooseExePath $ExpectedLooseExeSha256
    $locks.Add($exe)
    Assert-LTNativePe $exe
    Assert-LTPeVersion $LooseExePath $Version
    $hashes = @{}
    foreach ($name in $allowed) {
        $path = Join-Path $root $name
        Assert-LTPlainPath $path
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $locks.Add($stream)
        $hashes[$name] = Get-LTSha256 $stream
        if ($name -ceq $packageName) {
            $null = Read-LTPackage $stream $Version $ExpectedLooseExeSha256
            Assert-LTSignature $path $ExpectedSignerCertificateSha256
        }
    }
    $id = Get-LTIdentity
    $data = [ordered]@{
        schemaVersion = 1
        package = [ordered]@{
            file = $packageName; sha256 = $hashes[$packageName]; name = $id.Name; publisher = $id.Publisher
            version = $Version; architecture = $id.Architecture; applicationId = $id.AppId
            packageFamilyName = $id.Family; packageFullName = "BiliCopilot.LocalTest_${Version}_x64__p8tmrechhd89c"
            toastActivatorClsid = $id.Toast; exeSha256 = $ExpectedLooseExeSha256
            fileVersion = $Version; productVersion = $Version
        }
        signerCertificateSha256 = $ExpectedSignerCertificateSha256
        scripts = [ordered]@{ installSha256 = $hashes['Install.ps1']; moduleSha256 = $hashes['LocalTest.psm1']; configSha256 = $hashes['channel.json'] }
    }
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($data | ConvertTo-Json -Depth 6))
    $out = [IO.File]::Open($manifestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $out.Write($bytes, 0, $bytes.Length) } finally { $out.Dispose() }
    $m = [IO.File]::OpenRead($manifestPath)
    try { [pscustomobject]@{ Status = 'SealedStaticFactsOnly'; ManifestSha256 = (Get-LTSha256 $m); FamilyOsValidation = 'Pending'; RuntimeValidation = 'Pending' } }
    finally { $m.Dispose() }
} finally { foreach ($handle in $locks) { $handle.Dispose() } }
