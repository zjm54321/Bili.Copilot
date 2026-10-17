# Functions only: importing this NEW module performs no package/OS/trust operations.
Set-StrictMode -Version 2

function Stop-LT([string]$Code, [string]$Message) {
    $exception = New-Object System.InvalidOperationException ("LocalTest/$Code`: $Message")
    $exception.Data['LocalTestCode'] = $Code
    throw $exception
}

function Get-LTIdentity {
    [pscustomobject]@{
        Name = 'BiliCopilot.LocalTest'; Publisher = 'CN=BiliCopilot Local Release'
        Family = 'BiliCopilot.LocalTest_p8tmrechhd89c'; AppId = 'App'; Architecture = 'x64'
        Toast = 'E0BD9275-CB75-5DAE-AD37-B9C8464F9DAF'; Exe = 'BiliCopilot.UI.exe'
        Arguments = '----AppNotificationActivated:'
        Label = (-join ([char[]](0x54d4, 0x54e9, 0x52a9, 0x7406, 0xff08, 0x6d4b, 0x8bd5, 0xff09)))
    }
}

function Assert-LTVersion([string]$Version) {
    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}$') {
        Stop-LT 'Version' 'Use a canonical four-part MSIX version.'
    }
    foreach ($part in $Version.Split('.')) {
        if ([int]$part -gt 65535) { Stop-LT 'Version' 'MSIX version components must be <= 65535.' }
    }
    if ([version]$Version -lt [version]'2.2611.2.0') { Stop-LT 'Version' 'The new channel starts at 2.2611.2.0 or later; no installed baseline is assumed.' }
}

function Assert-LTFamilyDerivation {
    # Same public publisher-id derivation used by the bounded LocalRelease installer.
    # This is not a substitute for the final package-manager/OS family-name gate.
    $id = Get-LTIdentity
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($id.Publisher)) }
    finally { $sha.Dispose() }
    $bits = ''
    for ($i = 0; $i -lt 8; $i++) { $bits += [Convert]::ToString($digest[$i], 2).PadLeft(8, '0') }
    $bits += '0'
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    $suffix = ''
    for ($i = 0; $i -lt 65; $i += 5) { $suffix += $alphabet[[Convert]::ToInt32($bits.Substring($i, 5), 2)] }
    if (($id.Name + '_' + $suffix) -cne $id.Family) { Stop-LT 'FamilyDerivation' 'Publisher-derived PFN differs from the expected fixed test family.' }
}

function Assert-LTWindows {
    if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion -lt [version]'5.1' -or
        -not [Environment]::Is64BitProcess -or
        ($env:PROCESSOR_ARCHITECTURE -cne 'AMD64') -or $env:PROCESSOR_ARCHITEW6432) {
        Stop-LT 'Platform' 'This bounded recipe requires native x64 Windows PowerShell 5.1.'
    }
}

function Assert-LTPlainPath([string]$Path) {
    $node = [IO.Path]::GetFullPath($Path)
    while ($node) {
        if (Test-Path -LiteralPath $node) {
            if (([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-LT 'ReparsePoint' 'Reparse points, junctions and placeholders are not admitted.'
            }
        }
        $parent = [IO.Directory]::GetParent($node)
        if ($null -eq $parent) { break }
        $node = $parent.FullName
    }
}

function Get-LTSha256([IO.Stream]$Stream) {
    if ($Stream.CanSeek) { $Stream.Position = 0 }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); if ($Stream.CanSeek) { $Stream.Position = 0 } }
}

function Open-LTPinned([string]$Path, [string]$Sha256) {
    if ($Sha256 -cnotmatch '^[0-9a-f]{64}$') { Stop-LT 'Digest' 'A lowercase SHA-256 pin is required.' }
    Assert-LTPlainPath $Path
    $s = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ((Get-LTSha256 $s) -cne $Sha256) { Stop-LT 'Digest' 'Pinned file digest mismatch.' }
        return $s
    } catch { $s.Dispose(); throw }
}

function Read-LTJson([IO.Stream]$Stream) {
    $Stream.Position = 0
    if ($Stream.Length -gt 4194304) { Stop-LT 'Json' 'JSON exceeds the 4 MiB bound.' }
    $reader = New-Object IO.StreamReader($Stream, (New-Object Text.UTF8Encoding($false, $true)), $true, 4096, $true)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose(); $Stream.Position = 0 }
}

function Read-LTXml([IO.Stream]$Stream) {
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1048576
    $reader = [Xml.XmlReader]::Create($Stream, $settings)
    try {
        $doc = New-Object Xml.XmlDocument
        $doc.XmlResolver = $null
        $doc.PreserveWhitespace = $true
        $doc.Load($reader)
        return ,$doc
    } finally { $reader.Dispose(); if ($Stream.CanSeek) { $Stream.Position = 0 } }
}

function Get-LTManifestNodes([xml]$Document) {
    $ns = New-Object Xml.XmlNamespaceManager($Document.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $ns.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $ns.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $paths = @{
        Identity = '/f:Package/f:Identity'; Display = '/f:Package/f:Properties/f:DisplayName'
        PublisherDisplay = '/f:Package/f:Properties/f:PublisherDisplayName'
        App = '/f:Package/f:Applications/f:Application'
        Visual = '/f:Package/f:Applications/f:Application/uap:VisualElements'
        Tile = '/f:Package/f:Applications/f:Application/uap:VisualElements/uap:DefaultTile'
        Toast = '/f:Package/f:Applications/f:Application/f:Extensions/desktop:Extension[@Category="windows.toastNotificationActivation"]/desktop:ToastNotificationActivation'
        Server = '/f:Package/f:Applications/f:Application/f:Extensions/com:Extension[@Category="windows.comServer"]/com:ComServer/com:ExeServer'
        Class = '/f:Package/f:Applications/f:Application/f:Extensions/com:Extension[@Category="windows.comServer"]/com:ComServer/com:ExeServer/com:Class'
    }
    $result = @{ Namespace = $ns }
    foreach ($key in $paths.Keys) {
        $nodes = $Document.SelectNodes($paths[$key], $ns)
        if ($nodes.Count -ne 1) { Stop-LT 'ManifestShape' ("Expected exactly one $key node.") }
        $result[$key] = $nodes[0]
    }
    foreach ($xpath in @('//desktop:ToastNotificationActivation', '//com:ExeServer', '//com:Class', '//com:Extension', '//desktop:Extension[@Category="windows.toastNotificationActivation"]')) {
        if ($Document.SelectNodes($xpath, $ns).Count -ne 1) { Stop-LT 'ManifestShape' 'Extra/missing activation registrations are not guessed.' }
    }
    if ($Document.SelectNodes('/f:Package/f:Properties/f:Framework[text()="true"]', $ns).Count -ne 0 -or
        $result.Identity.GetAttribute('ResourceId')) { Stop-LT 'ManifestShape' 'Only the main non-resource app package is supported.' }
    return $result
}

function Assert-LTManifest([xml]$Document, [string]$Version, [switch]$Source) {
    Assert-LTVersion $Version
    $n = Get-LTManifestNodes $Document
    $id = Get-LTIdentity
    $toast = $id.Toast
    if ($Source) {
        $name = $n.Identity.GetAttribute('Name'); $publisher = $n.Identity.GetAttribute('Publisher')
        if ($name -ceq 'Richasy.BiliCopilot.Dev' -and $publisher -ceq 'CN=Richasy') {
            $toast = 'E3D99E22-57A2-41B9-BB2D-E1547E68DEBA'
        } elseif ($name -ceq 'BiliCopilot.LocalRelease' -and $publisher -ceq $id.Publisher) {
            $toast = '64DB9FDB-EF36-44D3-8DA6-04658D814915'
        } else { Stop-LT 'SourceIdentity' 'Only the parent-final Dev/LocalRelease generated native layout is admitted.' }
    } else {
        if ($n.Identity.GetAttribute('Name') -cne $id.Name -or $n.Identity.GetAttribute('Publisher') -cne $id.Publisher) {
            Stop-LT 'Identity' 'Only the fixed LocalTest identity is admitted; formal/dev packages are refused.'
        }
        if ($n.Display.InnerText -cne $id.Label -or $n.Visual.GetAttribute('DisplayName') -cne $id.Label -or
            $n.Visual.GetAttribute('Description') -cne $id.Label -or $n.Tile.GetAttribute('ShortName') -cne $id.Label -or
            $n.Server.GetAttribute('DisplayName') -cne $id.Label -or $n.PublisherDisplay.InnerText -cne 'BiliCopilot Local Release') {
            Stop-LT 'Label' 'LocalTest manifest labels do not match.'
        }
    }
    if ($n.Identity.GetAttribute('Version') -cne $Version -or $n.Identity.GetAttribute('ProcessorArchitecture') -cne 'x64' -or
        $n.App.GetAttribute('Id') -cne 'App' -or $n.App.GetAttribute('Executable') -cne $id.Exe -or
        $n.Server.GetAttribute('Executable') -cne $id.Exe -or $n.Server.GetAttribute('Arguments') -cne $id.Arguments -or
        $n.Toast.GetAttribute('ToastActivatorCLSID') -cne $toast -or $n.Class.GetAttribute('Id') -cne $toast -or
        $Document.OuterXml.Contains('$target') -or $Document.OuterXml.Contains('x-generate')) {
        Stop-LT 'ManifestContract' 'Require resolved native manifest, exact version/x64/App, flat EXE and matching toast/COM registrations.'
    }
    return $n
}

function Assert-LTNativePe([IO.Stream]$Stream) {
    $Stream.Position = 0
    $reader = New-Object IO.BinaryReader($Stream, [Text.Encoding]::UTF8, $true)
    try {
        if ($Stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) { Stop-LT 'NativePE' 'Missing DOS header.' }
        $Stream.Position = 0x3c; $pe = $reader.ReadInt32()
        if ($pe -lt 64 -or $pe -gt $Stream.Length - 256) { Stop-LT 'NativePE' 'Invalid PE offset.' }
        $Stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { Stop-LT 'NativePE' 'Expected x64 PE.' }
        $Stream.Position = $pe + 20; $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24; $Stream.Position = $optional
        if ($optionalSize -lt 232 -or $optional + $optionalSize -gt $Stream.Length -or $reader.ReadUInt16() -ne 0x20b) { Stop-LT 'NativePE' 'Expected bounded PE32+ optional header.' }
        $Stream.Position = $optional + 108
        if ($reader.ReadUInt32() -lt 15) { Stop-LT 'NativePE' 'Missing CLR directory slot.' }
        $Stream.Position = $optional + 112 + 14 * 8
        if ($reader.ReadUInt32() -ne 0 -or $reader.ReadUInt32() -ne 0) { Stop-LT 'NativePE' 'CLR header is present; managed payload refused.' }
    } finally { $reader.Dispose(); $Stream.Position = 0 }
}

function Assert-LTPeVersion([string]$Path, [string]$Version) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.FileVersion -cne $Version -or $info.ProductVersion -cne $Version -or
        ('{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart) -cne $Version -or
        ('{0}.{1}.{2}.{3}' -f $info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart, $info.ProductPrivatePart) -cne $Version) {
        Stop-LT 'PeVersion' 'Both PE FileVersion and ProductVersion must exactly equal the MSIX version.'
    }
}

function Read-LTPackage([IO.Stream]$Stream, [string]$Version, [string]$ExeSha256) {
    Add-Type -AssemblyName System.IO.Compression
    $zip = New-Object IO.Compression.ZipArchive($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $names = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)' -or $names.ContainsKey($name)) {
                Stop-LT 'Archive' 'Duplicate, rooted, or traversing package entries are refused.'
            }
            $names[$name] = $true
        }
        $manifests = @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        $executables = @($zip.Entries | Where-Object { ($_.FullName.Replace('\', '/').Split('/')[-1]) -ieq 'BiliCopilot.UI.exe' })
        if ($manifests.Count -ne 1 -or $executables.Count -ne 1 -or $executables[0].FullName -cne 'BiliCopilot.UI.exe' -or
            -not $names.ContainsKey('AppxSignature.p7x')) { Stop-LT 'Archive' 'Require one manifest, one flat app EXE, and a package signature.' }
        $ms = $manifests[0].Open()
        try { $doc = Read-LTXml $ms } finally { $ms.Dispose() }
        $null = Assert-LTManifest $doc $Version
        $es = $executables[0].Open()
        try { if ((Get-LTSha256 $es) -cne $ExeSha256) { Stop-LT 'ExeParity' 'Packaged EXE differs from the parent-pinned native loose EXE.' } }
        finally { $es.Dispose() }
        # Native header and PE version were checked on the byte-identical loose EXE at sealing.
        return ,$doc
    } finally { $zip.Dispose(); $Stream.Position = 0 }
}

function Assert-LTSignature([string]$Path, [string]$SignerSha256) {
    if ($SignerSha256 -cnotmatch '^[0-9a-f]{64}$') { Stop-LT 'SignerPin' 'An explicitly provided signer DER SHA-256 is required.' }
    $sig = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    if ([string]$sig.Status -cne 'Valid' -or $null -eq $sig.SignerCertificate) {
        Stop-LT 'SignatureOrTrustUnavailable' 'A platform-valid, already-trusted MSIX signature is required. No trust bootstrap is provided.'
    }
    $cert = $sig.SignerCertificate
    $s = New-Object IO.MemoryStream(,$cert.RawData)
    try { $actual = Get-LTSha256 $s } finally { $s.Dispose() }
    if ($actual -cne $SignerSha256 -or $cert.Subject -cne (Get-LTIdentity).Publisher -or
        [DateTime]::Now -lt $cert.NotBefore -or [DateTime]::Now -gt $cert.NotAfter) {
        Stop-LT 'SignerPin' 'Signer DER/subject/validity differs from the explicitly pinned signer.'
    }
}

Export-ModuleMember -Function Stop-LT, Get-LTIdentity, Assert-LTVersion, Assert-LTFamilyDerivation, Assert-LTWindows, Assert-LTPlainPath, Get-LTSha256, Open-LTPinned, Read-LTJson, Read-LTXml, Get-LTManifestNodes, Assert-LTManifest, Assert-LTNativePe, Assert-LTPeVersion, Read-LTPackage, Assert-LTSignature
