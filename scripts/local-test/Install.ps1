[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedManifestSha256,
    [switch]$Install
)
# Default: read-only preflight. No trust bootstrap, elevation, removal, forced update or app termination.
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$locks = New-Object 'System.Collections.Generic.List[System.IDisposable]'

function Open-BootstrapPin([string]$Path, [string]$Expected) {
    if ($Expected -cnotmatch '^[0-9a-f]{64}$') { throw 'LocalTest/Digest: A lowercase SHA-256 pin is required.' }
    $node = [IO.Path]::GetFullPath($Path)
    while ($node) {
        if (([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'LocalTest/ReparsePoint: Refused.' }
        $parent = [IO.Directory]::GetParent($node)
        if ($null -eq $parent) { break }
        $node = $parent.FullName
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        if ($actual -cne $Expected) { throw 'LocalTest/Digest: External manifest or support-file pin mismatch.' }
        $stream.Position = 0
        return $stream
    } catch { $stream.Dispose(); throw }
    finally { $sha.Dispose() }
}

function Assert-Fields($Value, [string[]]$Expected) {
    if ($Value -isnot [pscustomobject]) { Stop-LT 'ArtifactManifest' 'Expected a JSON object.' }
    $keys = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    if ($keys.Count -ne $Expected.Count -or @($keys | Where-Object { $Expected -cnotcontains $_ }).Count -ne 0) {
        Stop-LT 'ArtifactManifest' 'Unexpected or missing manifest fields.'
    }
}

function Get-ExistingTestState([string]$TargetVersion) {
    $id = Get-LTIdentity
    $existing = @(Get-AppxPackage -Name $id.Name -ErrorAction Stop)
    if ($existing.Count -gt 1) { Stop-LT 'ExistingIdentity' 'Multiple test registrations are not guessed.' }
    if ($existing.Count -eq 0) { return 'Absent' }
    $item = $existing[0]
    if ($item.Name -cne $id.Name -or $item.Publisher -cne $id.Publisher -or $item.PackageFamilyName -cne $id.Family -or
        [string]$item.Architecture -ine 'x64' -or $item.IsFramework -or [string]$item.Status -ine 'Ok' -or [string]$item.SignatureKind -ine 'Developer') {
        Stop-LT 'ExistingIdentity' 'Existing test name/publisher/PFN/architecture/status/signature kind does not match.'
    }
    if ([version]$item.Version -gt [version]$TargetVersion) { Stop-LT 'Downgrade' 'A higher test version is installed; refusing downgrade.' }
    $oldManifest = Get-AppxPackageManifest -Package $item.PackageFullName -ErrorAction Stop
    $null = Assert-LTManifest $oldManifest ([string]$item.Version)
    if ([version]$item.Version -eq [version]$TargetVersion) { return 'SameVersionNoWrite' }
    return 'Upgrade'
}

function Assert-InstalledDependencies([xml]$Document) {
    $n = Get-LTManifestNodes $Document
    $families = @($Document.SelectNodes('/f:Package/f:Dependencies/f:TargetDeviceFamily', $n.Namespace))
    if ($families.Count -eq 0) { Stop-LT 'Dependencies' 'No target device family is declared.' }
    $os = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $osVersion = [version]('{0}.{1}.{2}.0' -f $os.CurrentMajorVersionNumber, $os.CurrentMinorVersionNumber, $os.CurrentBuildNumber)
    foreach ($family in $families) {
        if ($family.Name -cnotin @('Windows.Desktop', 'Windows.Universal') -or $osVersion -lt [version]$family.MinVersion) {
            Stop-LT 'OsVersion' 'The current Windows version/device family does not meet the package requirements.'
        }
    }
    $dependencies = @($Document.SelectNodes('/f:Package/f:Dependencies/*', $n.Namespace))
    $seen = @{}
    foreach ($dep in $dependencies) {
        if ($dep.LocalName -ceq 'TargetDeviceFamily') { continue }
        if ($dep.NamespaceURI -cne 'http://schemas.microsoft.com/appx/manifest/foundation/windows10' -or $dep.LocalName -cne 'PackageDependency' -or
            $dep.GetAttribute('Name') -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]+$' -or -not $dep.GetAttribute('Publisher') -or
            $dep.GetAttribute('MinVersion') -cnotmatch '^\d+\.\d+\.\d+\.\d+$' -or $seen.ContainsKey($dep.GetAttribute('Name'))) {
            Stop-LT 'Dependencies' 'Unknown/ambiguous dependency declarations require parent review.'
        }
        $seen[$dep.GetAttribute('Name')] = $true
        $architecture = $dep.GetAttribute('ProcessorArchitecture')
        if ($architecture -and $architecture -cnotin @('x64', 'neutral')) { Stop-LT 'Dependencies' 'Unsupported dependency architecture.' }
        $found = @(Get-AppxPackage -Name $dep.GetAttribute('Name') -ErrorAction Stop | Where-Object {
            $_.Name -ceq $dep.GetAttribute('Name') -and $_.Publisher -ceq $dep.GetAttribute('Publisher') -and
            $_.IsFramework -and [string]$_.Status -ieq 'Ok' -and [string]$_.SignatureKind -ine 'None' -and
            [string]$_.Architecture -iin @('x64', 'neutral') -and
            (-not $architecture -or [string]$_.Architecture -ieq $architecture) -and
            [version]$_.Version -ge [version]$dep.GetAttribute('MinVersion')
        })
        if ($found.Count -eq 0) { Stop-LT 'MissingDependency' 'A declared signed framework dependency is missing; this bounded installer does not acquire or deploy dependencies.' }
    }
}

try {
    $manifest = Open-BootstrapPin (Join-Path $root 'test-artifacts.json') $ExpectedManifestSha256
    $locks.Add($manifest)
    if ($manifest.Length -gt 1048576) { throw 'LocalTest/ArtifactManifest: Manifest exceeds 1 MiB.' }
    $reader = New-Object IO.StreamReader($manifest, (New-Object Text.UTF8Encoding($false, $true)), $true, 4096, $true)
    try { $data = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose(); $manifest.Position = 0 }
    # Verify support files BEFORE importing code. The externally communicated manifest digest is mandatory.
    foreach ($pin in @(@('Install.ps1', $data.scripts.installSha256), @('LocalTest.psm1', $data.scripts.moduleSha256), @('channel.json', $data.scripts.configSha256))) {
        $locks.Add((Open-BootstrapPin (Join-Path $root $pin[0]) ([string]$pin[1])))
    }
    Import-Module (Join-Path $root 'LocalTest.psm1') -Force
    Assert-LTWindows
    Assert-Fields $data @('schemaVersion', 'package', 'signerCertificateSha256', 'scripts')
    Assert-Fields $data.scripts @('installSha256', 'moduleSha256', 'configSha256')
    Assert-Fields $data.package @('file', 'sha256', 'name', 'publisher', 'version', 'architecture', 'applicationId', 'packageFamilyName', 'packageFullName', 'toastActivatorClsid', 'exeSha256', 'fileVersion', 'productVersion')
    if ($data.schemaVersion -ne 1) { Stop-LT 'ArtifactManifest' 'Unsupported schema.' }
    $p = $data.package
    Assert-LTVersion ([string]$p.version)
    $id = Get-LTIdentity
    $expected = @{
        name = $id.Name; publisher = $id.Publisher; architecture = 'x64'; applicationId = 'App'
        packageFamilyName = $id.Family; packageFullName = "BiliCopilot.LocalTest_$($p.version)_x64__p8tmrechhd89c"
        toastActivatorClsid = $id.Toast; file = "BiliCopilot.LocalTest_$($p.version)_x64.msix"
        fileVersion = $p.version; productVersion = $p.version
    }
    foreach ($field in $expected.Keys) {
        if ($p.$field -isnot [string] -or $p.$field -cne $expected[$field]) { Stop-LT 'Identity' ("Fixed test artifact field mismatch: $field") }
    }
    if ($p.exeSha256 -cnotmatch '^[0-9a-f]{64}$') { Stop-LT 'Digest' 'Missing native EXE parity pin.' }
    $allowed = @('Install.ps1', 'LocalTest.psm1', 'channel.json', 'test-artifacts.json', $p.file)
    $items = @(Get-ChildItem -LiteralPath $root -Force)
    if ($items.Count -ne $allowed.Count -or @($items | Where-Object { $_.PSIsContainer -or $allowed -cnotcontains $_.Name }).Count -ne 0) {
        Stop-LT 'DeliveryInventory' 'Only the five sealed files are admitted; no extra private material or dependencies.'
    }
    $packagePath = Join-Path $root $p.file
    $package = Open-LTPinned $packagePath $p.sha256
    $locks.Add($package)
    $doc = Read-LTPackage $package $p.version $p.exeSha256
    Assert-LTSignature $packagePath $data.signerCertificateSha256
    Assert-InstalledDependencies $doc
    $state = Get-ExistingTestState $p.version
    if (-not $Install) {
        [pscustomobject]@{ Status = 'PreflightOnly'; ExistingState = $state; Version = $p.version; ExpectedFamily = $id.Family; Writes = $false; RuntimeValidated = $false }
        return
    }
    # Re-read immediately before ordinary deployment; all delivery bytes remain locked.
    $state = Get-ExistingTestState $p.version
    if ($state -ceq 'SameVersionNoWrite') { Stop-LT 'VersionUnchanged' 'Same-version deployment is refused. No repair/reinstall write is attempted.' }
    Add-AppxPackage -Path $packagePath -ErrorAction Stop
    $installed = @(Get-AppxPackage -Name $id.Name -ErrorAction Stop)
    if ($installed.Count -ne 1 -or $installed[0].PackageFamilyName -cne $id.Family -or
        $installed[0].Publisher -cne $id.Publisher -or $installed[0].PackageFullName -cne $p.packageFullName) {
        Stop-LT 'PostDeployIdentity' 'OS identity did not match after deployment. No rollback/removal is attempted.'
    }
    $null = Assert-LTManifest (Get-AppxPackageManifest -Package $installed[0].PackageFullName -ErrorAction Stop) $p.version
    [pscustomobject]@{ Status = 'DeployedIdentityChecked'; Version = $p.version; Family = $id.Family; RuntimeValidated = $false; DataRetentionValidated = $false }
} finally { foreach ($handle in $locks) { $handle.Dispose() } }
