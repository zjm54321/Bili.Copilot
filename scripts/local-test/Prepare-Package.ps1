[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceLayout,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
    [Parameter(Mandatory = $true)][string]$ApprovedInventory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedInventorySha256,
    [string]$Version = '2.2611.2.0',
    [switch]$Prepare,
    [switch]$Pack,
    [string]$MakeAppxPath,
    [string]$ExpectedMakeAppxSha256
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LocalTest.psm1') -Force
Assert-LTWindows
Assert-LTVersion $Version
if ($Pack -and -not $Prepare) { Stop-LT 'Consent' '-Pack requires explicit -Prepare as well.' }

$source = [IO.Path]::GetFullPath($SourceLayout).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
foreach ($path in @($source, $output, $workspace)) {
    if ($path -notmatch '^[A-Za-z]:\\' -or $path.Length -le 3) { Stop-LT 'Path' 'Use explicit ordinary local directories, not a drive root or UNC path.' }
    Assert-LTPlainPath $path
}
foreach ($pair in @(@($output, $source), @($source, $workspace), @($output, $workspace))) {
    if ($pair[0].Equals($pair[1], [StringComparison]::OrdinalIgnoreCase) -or
        $pair[0].StartsWith($pair[1] + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $pair[1].StartsWith($pair[0] + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Stop-LT 'Isolation' 'Source/output/workspace must not overlap. Only a parent-final external native layout is admitted.'
    }
}
if (-not [IO.Directory]::Exists($source) -or (Test-Path -LiteralPath $output)) { Stop-LT 'OutputExists' 'Source must exist and output must be a NEW version/run directory.' }
if ($Pack) {
    if (-not $MakeAppxPath -or -not [IO.File]::Exists($MakeAppxPath)) { Stop-LT 'MakeAppxUnavailable' 'Provide the existing parent-admitted MakeAppx.exe; no SDK discovery/download occurs.' }
    Assert-LTPlainPath $MakeAppxPath
    if ([IO.Path]::GetFileName($MakeAppxPath) -ine 'MakeAppx.exe' -or $ExpectedMakeAppxSha256 -cnotmatch '^[0-9a-f]{64}$') {
        Stop-LT 'MakeAppxPin' 'Pin the parent-admitted MakeAppx from the existing build-tools package; package version is not inferred from tool FileVersion.'
    }
}
$locks = New-Object 'System.Collections.Generic.List[System.IDisposable]'
try {
    if ($Pack) { $locks.Add((Open-LTPinned $MakeAppxPath $ExpectedMakeAppxSha256)) }
    $inventoryStream = Open-LTPinned $ApprovedInventory $ExpectedInventorySha256
    $locks.Add($inventoryStream)
    $inventory = Read-LTJson $inventoryStream
    if ($inventory.schemaVersion -ne 1 -or $inventory.files -isnot [array] -or $inventory.files.Count -eq 0) { Stop-LT 'Inventory' 'Require a parent-approved exhaustive files array.' }
    $actual = @{}
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($source)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            Assert-LTPlainPath $item.FullName
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $actual[$item.FullName.Substring($source.Length + 1).Replace('\', '/')] = $item.FullName }
        }
    }
    $files = @{}
    foreach ($entry in $inventory.files) {
        $relative = [string]$entry.path
        if (-not $relative -or $relative -match '[\\:*?"<>|]' -or $relative.StartsWith('/') -or
            $relative -match '(^|/)(\.|\.\.|[^/]*[. ])(/|$)' -or $files.ContainsKey($relative) -or -not $actual.ContainsKey($relative)) {
            Stop-LT 'InventoryPath' 'Inventory paths must be unique safe package-relative file paths that exist.'
        }
        if ($relative -match '(?i)(^|/)(\.git|\.vs|obj|evidence|before|after|tests|source|src|userprofiles?|users|appdata|localstate|localsettings|logger|logs)(/|$)' -or
            $relative -match '(?i)\.(pfx|p12|pvk|snk|key|pem|pdb|log|etl|dmp|cs|csproj|sln|ps1|psm1|py|user|appx|msix|cer|crt)$' -or
            $relative -match '(?i)(^|/)(AppxSignature\.p7x|AppxBlockMap\.xml|\[Content_Types\]\.xml)$') {
            Stop-LT 'PayloadPrivacy' 'Private/source/log/evidence/packaging material is not payload. Curate the parent layout first; nothing is silently dropped.'
        }
        $stream = Open-LTPinned $actual[$relative] ([string]$entry.sha256)
        $locks.Add($stream)
        $files[$relative] = [pscustomobject]@{ Stream = $stream; Sha256 = [string]$entry.sha256; Path = $actual[$relative] }
    }
    if ($files.Count -ne $actual.Count -or -not $files.ContainsKey('AppxManifest.xml') -or -not $files.ContainsKey('BiliCopilot.UI.exe')) {
        Stop-LT 'Inventory' 'The approved inventory must exactly cover the final layout, including its generated manifest and flat native EXE.'
    }
    if (@($files.Keys | Where-Object { $_.Split('/')[-1] -ieq 'BiliCopilot.UI.exe' }).Count -ne 1) { Stop-LT 'NativeLayout' 'Nested/duplicate app EXEs are refused.' }
    $doc = Read-LTXml $files['AppxManifest.xml'].Stream
    $n = Assert-LTManifest $doc $Version -Source
    Assert-LTNativePe $files['BiliCopilot.UI.exe'].Stream
    Assert-LTPeVersion $files['BiliCopilot.UI.exe'].Path $Version
    if (-not $Prepare) {
        [pscustomobject]@{ Status = 'PreflightOnly'; Version = $Version; Files = $files.Count; OutputCreated = $false }
        return
    }

    $null = New-Item -ItemType Directory -Path $output -ErrorAction Stop
    $layout = Join-Path $output 'layout'
    $delivery = Join-Path $output 'delivery'
    $null = New-Item -ItemType Directory -Path $layout, $delivery -ErrorAction Stop
    foreach ($relative in $files.Keys) {
        $target = Join-Path $layout $relative.Replace('/', '\')
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        $out = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $files[$relative].Stream.Position = 0; $files[$relative].Stream.CopyTo($out) }
        finally { $out.Dispose(); $files[$relative].Stream.Position = 0 }
    }
    # Transform ONLY the fresh copy; keep resources, PRI, capabilities and dependencies intact.
    $id = Get-LTIdentity
    $n.Identity.SetAttribute('Name', $id.Name)
    $n.Identity.SetAttribute('Publisher', $id.Publisher)
    $n.Identity.SetAttribute('Version', $Version)
    $n.Display.InnerText = $id.Label
    $n.PublisherDisplay.InnerText = 'BiliCopilot Local Release'
    $n.Visual.SetAttribute('DisplayName', $id.Label)
    $n.Visual.SetAttribute('Description', $id.Label)
    $n.Tile.SetAttribute('ShortName', $id.Label)
    $n.Server.SetAttribute('DisplayName', $id.Label)
    $n.Toast.SetAttribute('ToastActivatorCLSID', $id.Toast)
    $n.Class.SetAttribute('Id', $id.Toast)
    $null = Assert-LTManifest $doc $Version
    $doc.Save((Join-Path $layout 'AppxManifest.xml'))
    foreach ($relative in $files.Keys) {
        if ((Get-LTSha256 $files[$relative].Stream) -cne $files[$relative].Sha256) { Stop-LT 'SourceChanged' 'The parent source layout changed.' }
        if ($relative -ine 'AppxManifest.xml') {
            $check = Open-LTPinned (Join-Path $layout $relative.Replace('/', '\')) $files[$relative].Sha256
            $check.Dispose()
        }
    }
    foreach ($name in @('Install.ps1', 'LocalTest.psm1', 'channel.json')) {
        [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $delivery $name), $false)
    }
    $package = Join-Path $delivery ("BiliCopilot.LocalTest_${Version}_x64.msix")
    if ($Pack) {
        & $MakeAppxPath pack /d $layout /p $package
        if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($package)) { Stop-LT 'MakeAppxFailed' 'Packing failed; retain this new output for parent diagnosis, do not overwrite/retry in place.' }
    }
    [pscustomobject]@{
        Status = 'PreparedNotSealed'; Version = $Version; Layout = $layout; Package = $package
        Packed = [bool]$Pack; Signed = $false; ExeSha256 = $files['BiliCopilot.UI.exe'].Sha256
        ExpectedFamilyPendingOsGate = $id.Family
    }
} finally { foreach ($handle in $locks) { $handle.Dispose() } }
