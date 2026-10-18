[CmdletBinding()]
param(
    [switch]$Install,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedManifestSha256
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# Bili.Copilot 本地 Release MSIX 一次性安装脚本。
# 默认只做只读预检；只有显式 -Install 才会请求证书信任（如需要）并为当前用户部署。
# 用法: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install

#region SharedValidation
$script:ReleaseFixed = @{
    Name                 = 'BiliCopilot.LocalRelease'
    Publisher            = 'CN=BiliCopilot Local Release'
    Version              = '2.2611.3.0'
    Architecture         = 'x64'
    CodeSigningEku       = '1.3.6.1.5.5.7.3.3'
    ManifestFileName     = 'release-artifacts.json'
    InstallScriptName    = 'Install.ps1'
    TrustScriptName      = 'Trust-Certificate.ps1'
    AppxNamespace        = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    UntrustedRootHResult = -2146762487
}

function Stop-Release([string]$Message) {
    throw (New-Object System.InvalidOperationException ('BLOCKED: ' + $Message))
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-StreamSha256Hex([System.IO.Stream]$Stream) {
    $Stream.Position = 0
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $Stream.Position = 0 }
}

function Test-BytesEqual([byte[]]$Left, [byte[]]$Right) {
    if ($null -eq $Left -or $null -eq $Right -or $Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) { if ($Left[$i] -ne $Right[$i]) { return $false } }
    return $true
}

function Read-StreamBytes([System.IO.Stream]$Stream) {
    $Stream.Position = 0
    $memory = New-Object System.IO.MemoryStream
    try { $Stream.CopyTo($memory); return , $memory.ToArray() }
    finally { $memory.Dispose(); $Stream.Position = 0 }
}

function Assert-NoReparsePoint([string]$Path) {
    $node = [System.IO.Path]::GetFullPath($Path)
    if (-not ([System.IO.File]::Exists($node) -or [System.IO.Directory]::Exists($node))) { Stop-Release ('文件不存在: ' + $node) }
    while ($node) {
        if (([System.IO.File]::GetAttributes($node) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Release ('拒绝重解析点、联接或云同步占位文件，请复制到普通本地文件夹: ' + $node)
        }
        $parent = [System.IO.Directory]::GetParent($node)
        if ($null -eq $parent) { break }
        $node = $parent.FullName
    }
}

function Assert-PayloadRelativePath($RelativePath, [string]$Kind) {
    if ($RelativePath -isnot [string] -or $RelativePath -cnotmatch '^payload[\\/][A-Za-z0-9][A-Za-z0-9._-]{0,150}[A-Za-z0-9]$' -or $RelativePath.Contains('..')) {
        Stop-Release ('{0} 的 relativePath 必须是 payload 下的单层文件名: {1}' -f $Kind, $RelativePath)
    }
}

function Resolve-PayloadPath([string]$Root, $RelativePath, [string]$Kind) {
    Assert-PayloadRelativePath $RelativePath $Kind
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, $RelativePath))
    Assert-NoReparsePoint $full
    return $full
}

function Open-PinnedFile([string]$Path, [string]$ExpectedSha256, [string]$Kind) {
    # FileShare.Read: 其他进程仍可读取（部署服务需要），但在句柄存续期间不能改写/替换/删除。
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $actual = Get-StreamSha256Hex $stream
        if ($actual -cne $ExpectedSha256) { Stop-Release ('{0} SHA-256 不匹配: 期望 {1}，实际 {2}' -f $Kind, $ExpectedSha256, $actual) }
        return $stream
    } catch { $stream.Dispose(); throw }
}

function Get-PublisherId([string]$Publisher) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($Publisher)) } finally { $sha.Dispose() }
    $bits = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 8; $i++) { [void]$bits.Append([Convert]::ToString($digest[$i], 2).PadLeft(8, '0')) }
    [void]$bits.Append('0')
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    $text = $bits.ToString()
    $out = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 65; $i += 5) { [void]$out.Append($alphabet[[Convert]::ToInt32($text.Substring($i, 5), 2)]) }
    return $out.ToString()
}

function Get-ManifestLeaves($Node, [string]$Path) {
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) { Get-ManifestLeaves $property.Value ($Path + '.' + $property.Name) }
    } elseif ($Node -is [System.Array]) {
        for ($i = 0; $i -lt $Node.Length; $i++) { Get-ManifestLeaves $Node[$i] ('{0}[{1}]' -f $Path, $i) }
    } else {
        [pscustomobject]@{ Path = $Path.TrimStart('.'); Value = $Node }
    }
}

function Assert-ExactKeys($Node, [string[]]$Keys, [string]$Where) {
    if ($Node -isnot [System.Management.Automation.PSCustomObject]) { Stop-Release ($Where + ' 必须是 JSON 对象') }
    $actual = @($Node.PSObject.Properties | ForEach-Object { $_.Name })
    $missing = @($Keys | Where-Object { $actual -cnotcontains $_ })
    $extra = @($actual | Where-Object { $Keys -cnotcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        Stop-Release ('{0} 字段不符；缺少: [{1}]；多余: [{2}]' -f $Where, ($missing -join ','), ($extra -join ','))
    }
}

function Assert-Text($Value, [string]$Pattern, [string]$Where) {
    if ($Value -isnot [string] -or $Value -cnotmatch $Pattern) { Stop-Release ($Where + ' 格式无效') }
}

function Assert-Equal($Value, [string]$Expected, [string]$Where) {
    if ($Value -isnot [string] -or $Value -cne $Expected) { Stop-Release ('{0} 必须为 {1}' -f $Where, $Expected) }
}

function ConvertFrom-UtcText([string]$Text, [string]$Where) {
    $parsed = [DateTime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal
    if (-not [DateTime]::TryParseExact($Text, "yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        Stop-Release ($Where + ' 必须是 yyyy-MM-ddTHH:mm:ssZ (UTC)')
    }
    return $parsed
}

function Assert-ManifestData($Data) {
    $fixed = $script:ReleaseFixed
    Assert-ExactKeys $Data @('schemaVersion', 'package', 'certificate', 'dependencies', 'scripts') '清单根对象'
    $leaves = @(Get-ManifestLeaves $Data '')
    $pending = @($leaves | Where-Object { $_.Value -is [string] -and $_.Value -cmatch '__PENDING' } | ForEach-Object { $_.Path })
    if ($pending.Count -gt 0) { Stop-Release ('清单仍含出包后待父代理填写的占位字段: ' + ($pending -join ', ')) }
    $unsafe = @($leaves | Where-Object { $_.Value -is [string] -and ($_.Value.Contains('://') -or $_.Value.IndexOfAny([char[]]'*?%') -ge 0) } | ForEach-Object { $_.Path })
    if ($unsafe.Count -gt 0) { Stop-Release ('清单不得包含 URL、通配符或环境变量: ' + ($unsafe -join ', ')) }
    if ($Data.schemaVersion -isnot [int] -or $Data.schemaVersion -ne 1) { Stop-Release 'schemaVersion 必须为整数 1' }

    $hex = '^[0-9a-f]{64}$'
    $version = '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}$'
    $publisherId = Get-PublisherId $fixed.Publisher

    $p = $Data.package
    Assert-ExactKeys $p @('relativePath', 'sha256', 'name', 'publisher', 'version', 'architecture', 'packageFamilyName', 'packageFullName', 'applicationId', 'minOsVersion') 'package'
    Assert-Text $p.sha256 $hex 'package.sha256'
    Assert-Equal $p.name $fixed.Name 'package.name'
    Assert-Equal $p.publisher $fixed.Publisher 'package.publisher'
    Assert-Equal $p.version $fixed.Version 'package.version'
    Assert-Equal $p.architecture $fixed.Architecture 'package.architecture'
    Assert-Equal $p.packageFamilyName ('{0}_{1}' -f $fixed.Name, $publisherId) 'package.packageFamilyName'
    Assert-Equal $p.packageFullName ('{0}_{1}_{2}__{3}' -f $fixed.Name, $fixed.Version, $fixed.Architecture, $publisherId) 'package.packageFullName'
    Assert-Text $p.applicationId '^[A-Za-z][A-Za-z0-9]{0,63}(\.[A-Za-z0-9]{1,63}){0,8}$' 'package.applicationId'
    Assert-Text $p.minOsVersion $version 'package.minOsVersion'

    $c = $Data.certificate
    Assert-ExactKeys $c @('relativePath', 'sha256', 'derSha256', 'thumbprint', 'subject', 'notBeforeUtc', 'notAfterUtc', 'codeSigningEku', 'digitalSignature', 'isCertificateAuthority') 'certificate'
    Assert-Text $c.sha256 $hex 'certificate.sha256'
    Assert-Text $c.derSha256 $hex 'certificate.derSha256'
    if ($c.derSha256 -cne $c.sha256) { Stop-Release 'certificate 文件必须是原始 DER（derSha256 必须等于 sha256）' }
    Assert-Text $c.thumbprint '^[0-9A-F]{40}$' 'certificate.thumbprint'
    Assert-Equal $c.subject $fixed.Publisher 'certificate.subject'
    $notBefore = ConvertFrom-UtcText $c.notBeforeUtc 'certificate.notBeforeUtc'
    $notAfter = ConvertFrom-UtcText $c.notAfterUtc 'certificate.notAfterUtc'
    if ($notAfter -le $notBefore) { Stop-Release 'certificate 有效期区间无效' }
    Assert-Equal $c.codeSigningEku $fixed.CodeSigningEku 'certificate.codeSigningEku'
    if ($c.digitalSignature -isnot [bool] -or -not $c.digitalSignature) { Stop-Release 'certificate.digitalSignature 必须为 true' }
    if ($c.isCertificateAuthority -isnot [bool] -or $c.isCertificateAuthority) { Stop-Release 'certificate.isCertificateAuthority 必须为 false' }

    if ($Data.dependencies -isnot [System.Array]) { Stop-Release 'dependencies 必须是数组' }
    $paths = @($p.relativePath, $c.relativePath)
    $names = @()
    for ($i = 0; $i -lt $Data.dependencies.Length; $i++) {
        $d = $Data.dependencies[$i]
        $where = 'dependencies[{0}]' -f $i
        Assert-ExactKeys $d @('relativePath', 'sha256', 'name', 'publisher', 'version', 'architecture', 'declaredMinVersion') $where
        Assert-Text $d.sha256 $hex ($where + '.sha256')
        Assert-Text $d.name '^[A-Za-z0-9][A-Za-z0-9.-]{1,48}[A-Za-z0-9]$' ($where + '.name')
        Assert-Text $d.publisher '^CN=[^\r\n]{1,8000}$' ($where + '.publisher')
        Assert-Text $d.version $version ($where + '.version')
        Assert-Text $d.architecture '^(x64|neutral)$' ($where + '.architecture')
        Assert-Text $d.declaredMinVersion $version ($where + '.declaredMinVersion')
        if ([version]$d.version -lt [version]$d.declaredMinVersion) { Stop-Release ($where + '.version 低于 declaredMinVersion') }
        if ($names -ccontains $d.name) { Stop-Release ($where + '.name 重复') }
        $names += $d.name
        $paths += $d.relativePath
    }
    Assert-PayloadRelativePath $p.relativePath 'package'
    Assert-PayloadRelativePath $c.relativePath 'certificate'
    for ($i = 0; $i -lt $Data.dependencies.Length; $i++) { Assert-PayloadRelativePath $Data.dependencies[$i].relativePath ('dependencies[{0}]' -f $i) }
    $lowerPaths = @($paths | ForEach-Object { ([string]$_).Replace('/', '\').ToLowerInvariant() })
    if (@($lowerPaths | Select-Object -Unique).Count -ne $lowerPaths.Count) { Stop-Release '清单中的 relativePath 存在重复' }

    Assert-ExactKeys $Data.scripts @('installSha256', 'trustSha256') 'scripts'
    Assert-Text $Data.scripts.installSha256 $hex 'scripts.installSha256'
    Assert-Text $Data.scripts.trustSha256 $hex 'scripts.trustSha256'
}

function Read-ReleaseManifest([string]$Root, [string]$ExpectedSha256) {
    $path = [System.IO.Path]::Combine($Root, $script:ReleaseFixed.ManifestFileName)
    if (-not [System.IO.File]::Exists($path)) { Stop-Release ('缺少 ' + $script:ReleaseFixed.ManifestFileName) }
    Assert-NoReparsePoint $path
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 262144) { Stop-Release '清单文件过大' }
    $digest = Get-Sha256Hex $bytes
    if ($ExpectedSha256 -and $digest -cne $ExpectedSha256.ToLowerInvariant()) {
        Stop-Release ('release-artifacts.json SHA-256 与外部记录不一致: 期望 {0}，实际 {1}' -f $ExpectedSha256.ToLowerInvariant(), $digest)
    }
    try {
        $text = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
        $data = $text | ConvertFrom-Json
    } catch { Stop-Release '清单不是有效的 UTF-8 JSON' }
    Assert-ManifestData $data
    return [pscustomobject]@{ Data = $data; Sha256 = $digest }
}

function Get-CertificateFacts([byte[]]$Bytes) {
    if ($null -eq $Bytes -or $Bytes.Length -lt 64 -or $Bytes.Length -gt 16384 -or $Bytes[0] -ne 0x30) {
        Stop-Release '公钥证书必须是原始 DER 编码 X.509（不接受 PEM/PFX/PKCS#7）'
    }
    $length = 0
    $offset = 2
    if ($Bytes[1] -lt 0x80) { $length = [int]$Bytes[1] }
    else {
        $count = $Bytes[1] -band 0x7F
        if ($count -lt 1 -or $count -gt 3) { Stop-Release '公钥证书 DER 长度编码无效' }
        for ($i = 0; $i -lt $count; $i++) { $length = ($length -shl 8) -bor [int]$Bytes[2 + $i] }
        $offset = 2 + $count
    }
    if ($offset + $length -ne $Bytes.Length -or $Bytes[$offset] -ne 0x30) {
        Stop-Release '公钥证书不是单个 DER X.509 结构（PFX/PKCS#7 被拒绝）'
    }
    $cert = $null
    try { $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, $Bytes) }
    catch { Stop-Release '公钥证书无法解析为 X.509' }
    try {
        $ekus = @()
        $keyUsage = $null
        $basic = $null
        foreach ($extension in $cert.Extensions) {
            switch ($extension.Oid.Value) {
                '2.5.29.37' {
                    $eku = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($extension, $extension.Critical)
                    foreach ($oid in $eku.EnhancedKeyUsages) { $ekus += $oid.Value }
                }
                '2.5.29.15' { $keyUsage = (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension($extension, $extension.Critical)).KeyUsages }
                '2.5.29.19' { $basic = New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($extension, $extension.Critical) }
            }
        }
        return [pscustomobject]@{
            RawData                = $cert.RawData
            Thumbprint             = $cert.Thumbprint.ToUpperInvariant()
            Subject                = $cert.Subject
            NotBeforeUtc           = $cert.NotBefore.ToUniversalTime()
            NotAfterUtc            = $cert.NotAfter.ToUniversalTime()
            Ekus                   = $ekus
            DigitalSignature       = ($null -ne $keyUsage -and (($keyUsage -band [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ne 0))
            IsCertificateAuthority = ($null -ne $basic -and $basic.CertificateAuthority)
            HasPrivateKey          = $cert.HasPrivateKey
        }
    } finally { $cert.Reset() }
}

function Assert-CertificateFacts($Facts, [byte[]]$CerBytes, $Pins, [DateTime]$NowUtc) {
    $format = "yyyy-MM-dd'T'HH:mm:ss'Z'"
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    if ($Facts.HasPrivateKey) { Stop-Release '公钥证书文件不得携带私钥' }
    if (-not (Test-BytesEqual $Facts.RawData $CerBytes)) { Stop-Release 'CER 文件字节与解析出的 DER 不一致' }
    if ((Get-Sha256Hex $Facts.RawData) -cne $Pins.derSha256) { Stop-Release '证书 DER SHA-256 与清单不一致' }
    if ($Facts.Thumbprint -cne $Pins.thumbprint) { Stop-Release ('证书 SHA-1 指纹不一致: 实际 ' + $Facts.Thumbprint) }
    if ($Facts.Subject -cne $Pins.subject -or $Facts.Subject -cne $script:ReleaseFixed.Publisher) { Stop-Release ('证书 Subject 不一致: 实际 ' + $Facts.Subject) }
    if ($Facts.NotBeforeUtc.ToString($format, $culture) -cne $Pins.notBeforeUtc -or $Facts.NotAfterUtc.ToString($format, $culture) -cne $Pins.notAfterUtc) {
        Stop-Release '证书有效期与清单不一致'
    }
    if ($NowUtc -lt $Facts.NotBeforeUtc -or $NowUtc -gt $Facts.NotAfterUtc) { Stop-Release '证书当前不在有效期内' }
    if (@($Facts.Ekus) -cnotcontains $script:ReleaseFixed.CodeSigningEku) { Stop-Release '证书缺少 Code Signing EKU' }
    if (-not $Facts.DigitalSignature) { Stop-Release '证书 KeyUsage 缺少 DigitalSignature' }
    if ($Facts.IsCertificateAuthority) { Stop-Release '证书 BasicConstraints 为 CA=true，拒绝' }
}

function Read-ZipEntryBytes($Entry, [long]$Maximum, [string]$Kind) {
    if ($Entry.Length -gt $Maximum) { Stop-Release ($Kind + ' 过大') }
    $source = $Entry.Open()
    $memory = New-Object System.IO.MemoryStream
    try { $source.CopyTo($memory); return , $memory.ToArray() }
    finally { $source.Dispose(); $memory.Dispose() }
}

function Get-AppxManifestFacts([System.IO.Stream]$Stream, [string]$Kind) {
    Add-Type -AssemblyName System.IO.Compression
    $Stream.Position = 0
    $zip = $null
    try {
        try { $zip = New-Object System.IO.Compression.ZipArchive($Stream, [System.IO.Compression.ZipArchiveMode]::Read, $true) }
        catch { Stop-Release ($Kind + ' 不是有效的 ZIP/MSIX 容器') }
        $manifestEntries = @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        $signatureEntries = @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxSignature.p7x' })
        if ($manifestEntries.Count -ne 1) { Stop-Release ($Kind + ' 中 AppxManifest.xml 缺失或重复（不支持 bundle）') }
        if ($signatureEntries.Count -ne 1) { Stop-Release ($Kind + ' 中 AppxSignature.p7x 缺失或重复') }
        $xmlBytes = Read-ZipEntryBytes $manifestEntries[0] 4194304 ($Kind + ' AppxManifest.xml')
        $signatureBytes = Read-ZipEntryBytes $signatureEntries[0] 1048576 ($Kind + ' AppxSignature.p7x')
    } finally { if ($null -ne $zip) { $zip.Dispose() }; $Stream.Position = 0 }

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $memory = New-Object System.IO.MemoryStream(, $xmlBytes)
    $reader = [System.Xml.XmlReader]::Create($memory, $settings)
    $doc = New-Object System.Xml.XmlDocument
    $doc.XmlResolver = $null
    try { $doc.Load($reader) } catch { Stop-Release ($Kind + ' 的 AppxManifest.xml 无法解析') } finally { $reader.Dispose(); $memory.Dispose() }

    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('f', $script:ReleaseFixed.AppxNamespace)
    $root = $doc.DocumentElement
    if ($null -eq $root -or $root.LocalName -cne 'Package' -or $root.NamespaceURI -cne $script:ReleaseFixed.AppxNamespace) { Stop-Release ($Kind + ' 不是 Windows 10 Appx 包清单') }
    $identity = @($root.SelectNodes('f:Identity', $ns))
    if ($identity.Count -ne 1) { Stop-Release ($Kind + ' Identity 缺失或重复') }
    $desktop = @($root.SelectNodes("f:Dependencies/f:TargetDeviceFamily[@Name='Windows.Desktop']", $ns))
    $framework = $root.SelectSingleNode('f:Properties/f:Framework', $ns)
    $dependencies = @($root.SelectNodes('f:Dependencies/f:PackageDependency', $ns) | ForEach-Object {
            [pscustomobject]@{ Name = $_.GetAttribute('Name'); Publisher = $_.GetAttribute('Publisher'); MinVersion = $_.GetAttribute('MinVersion') }
        })
    return [pscustomobject]@{
        Name                  = $identity[0].GetAttribute('Name')
        Publisher             = $identity[0].GetAttribute('Publisher')
        Version               = $identity[0].GetAttribute('Version')
        ProcessorArchitecture = $identity[0].GetAttribute('ProcessorArchitecture')
        IsFramework           = ($null -ne $framework -and $framework.InnerText.Trim() -ceq 'true')
        ApplicationIds        = @($root.SelectNodes('f:Applications/f:Application', $ns) | ForEach-Object { $_.GetAttribute('Id') })
        DesktopMinVersions    = @($desktop | ForEach-Object { $_.GetAttribute('MinVersion') })
        Dependencies          = $dependencies
        SignatureBytes        = $signatureBytes
    }
}

function Assert-PackageIdentity($Facts, $Pins, [string]$CertificateSubject) {
    if ($Facts.Name -cne $Pins.name -or $Facts.Publisher -cne $Pins.publisher -or $Facts.Version -cne $Pins.version -or $Facts.ProcessorArchitecture -cne $Pins.architecture) {
        Stop-Release ('MSIX 包身份与清单不一致: {0} / {1} / {2} / {3}' -f $Facts.Name, $Facts.Publisher, $Facts.Version, $Facts.ProcessorArchitecture)
    }
    if ($Facts.Publisher -cne $CertificateSubject) { Stop-Release 'MSIX Publisher 与证书 Subject 不一致' }
    if ($Facts.IsFramework) { Stop-Release 'MSIX 不得是框架包' }
    if (@($Facts.ApplicationIds | Where-Object { $_ -ceq $Pins.applicationId }).Count -ne 1) { Stop-Release 'MSIX ApplicationId 与清单不一致' }
    if ($Facts.DesktopMinVersions.Count -ne 1 -or $Facts.DesktopMinVersions[0] -cne $Pins.minOsVersion) { Stop-Release 'MSIX Windows.Desktop MinVersion 与清单不一致' }
}

function Get-NativeSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    $raw = $null
    if ($null -ne $signature.SignerCertificate) { $raw = $signature.SignerCertificate.RawData }
    return [pscustomobject]@{ Status = [string]$signature.Status; StatusMessage = [string]$signature.StatusMessage; SignerRawData = $raw }
}

function Assert-SignerBinding($Signature, [byte[]]$CerBytes) {
    # 仅接受: Valid，或 UnknownError 且状态文本精确等于 CERT_E_UNTRUSTEDROOT(0x800B0109) 的系统消息
    # （PowerShell 将该错误映射为 UnknownError；NotTrusted 表示 TRUST_E_EXPLICIT_DISTRUST，必须拒绝）。
    $untrustedRoot = (New-Object System.ComponentModel.Win32Exception ([int]$script:ReleaseFixed.UntrustedRootHResult)).Message
    $kind = $null
    if ($Signature.Status -ceq 'Valid') { $kind = 'Valid' }
    elseif ($Signature.Status -ceq 'UnknownError' -and $Signature.StatusMessage -ceq $untrustedRoot) { $kind = 'UntrustedRootOnly' }
    if ($null -eq $kind) { Stop-Release ('MSIX 平台签名状态不可接受: {0} ({1})' -f $Signature.Status, $Signature.StatusMessage) }
    if ($null -eq $Signature.SignerRawData -or -not (Test-BytesEqual $Signature.SignerRawData $CerBytes)) {
        Stop-Release 'MSIX 实际签名者证书 DER 与固定公钥 CER 不完全一致'
    }
    return $kind
}

function Test-ProcessElevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
}

function Get-CallerSid {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try { return $identity.User.Value } finally { $identity.Dispose() }
}

function Test-MachineTrustedPeopleExact([byte[]]$CerBytes, [string]$Thumbprint) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor [System.Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
    try {
        foreach ($candidate in $store.Certificates) {
            if ($candidate.Thumbprint -ceq $Thumbprint -and (Test-BytesEqual $candidate.RawData $CerBytes)) { return $true }
        }
        return $false
    } finally { $store.Close() }
}
#endregion SharedValidation

function Get-OsFacts {
    $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    return [pscustomobject]@{
        Version        = [version]('{0}.{1}.{2}.0' -f $key.CurrentMajorVersionNumber, $key.CurrentMinorVersionNumber, $key.CurrentBuildNumber)
        Architecture   = [string]$architecture
        Is64BitProcess = [Environment]::Is64BitProcess
        PSEdition      = [string]$PSVersionTable.PSEdition
        PSVersion      = $PSVersionTable.PSVersion
    }
}

function Get-SideloadPolicy {
    # 只读；不修改任何旁加载或组策略设置。
    $path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $value = Get-ItemProperty -LiteralPath $path -Name 'AllowAllTrustedApps' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    return [int]$value.AllowAllTrustedApps
}

function ConvertTo-PackageRecord($Package) {
    return [pscustomobject]@{
        Name              = [string]$Package.Name
        Publisher         = [string]$Package.Publisher
        PackageFamilyName = [string]$Package.PackageFamilyName
        PackageFullName   = [string]$Package.PackageFullName
        Version           = [string]$Package.Version
        Architecture      = [string]$Package.Architecture
        Status            = [string]$Package.Status
        SignatureKind     = [string]$Package.SignatureKind
        IsFramework       = [bool]$Package.IsFramework
        InstallLocation   = [string]$Package.InstallLocation
    }
}

function Get-UserPackagesByName([string]$Name) {
    return @(Get-AppxPackage -Name $Name -ErrorAction Stop | ForEach-Object { ConvertTo-PackageRecord $_ })
}

function Read-InstalledSignatureBytes($Record) {
    $storeRoot = [System.IO.Path]::Combine([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles), 'WindowsApps') + '\'
    if (-not $Record.InstallLocation.StartsWith($storeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Release ('已安装包不在受保护的 WindowsApps 目录，来源未知: ' + $Record.InstallLocation)
    }
    return , [System.IO.File]::ReadAllBytes([System.IO.Path]::Combine($Record.InstallLocation, 'AppxSignature.p7x'))
}

function Get-P7xSignerRawData([byte[]]$Bytes) {
    # 有界适配器：仅用于识别已安装旧版本的签名者；平台已在部署时校验包内容。
    if ($null -eq $Bytes -or $Bytes.Length -lt 8 -or $Bytes[0] -ne 0x50 -or $Bytes[1] -ne 0x4B -or $Bytes[2] -ne 0x43 -or $Bytes[3] -ne 0x58) {
        Stop-Release '已安装包签名不是 PKCX 格式'
    }
    Add-Type -AssemblyName System.Security
    $body = New-Object byte[] ($Bytes.Length - 4)
    [Array]::Copy($Bytes, 4, $body, 0, $body.Length)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    try { $cms.Decode($body); $cms.CheckSignature($true) } catch { Stop-Release '已安装包签名 CMS 无法解析或签名无效' }
    if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) { Stop-Release '已安装包签名者不唯一' }
    return , $cms.SignerInfos[0].Certificate.RawData
}

function Get-PackageApplicationIds([string]$PackageFullName) {
    $manifest = Get-AppxPackageManifest -Package $PackageFullName -ErrorAction Stop
    return @($manifest.Package.Applications.Application | ForEach-Object { [string]$_.Id })
}

function Get-ExistingPackageState($Context) {
    $pins = $Context.Data.package
    $target = [version]$pins.version
    $same = @(Get-UserPackagesByName $pins.name | Where-Object { $_.Name -ceq $pins.name })
    if ($same.Count -eq 0) { return 'Absent' }
    $state = 'Absent'
    foreach ($item in $same) {
        if ($item.Publisher -cne $pins.publisher -or $item.PackageFamilyName -cne $pins.packageFamilyName) {
            Stop-Release ('已存在同名但 Publisher/PFN 不同的包，禁止覆盖: ' + $item.PackageFullName)
        }
        if ($item.SignatureKind -cne 'Developer' -or $item.Architecture -cne 'X64' -or $item.IsFramework) {
            Stop-Release ('已存在来源未知或类型不符的同名包 (SignatureKind={0}, Architecture={1})，禁止覆盖' -f $item.SignatureKind, $item.Architecture)
        }
        $installed = [version]$item.Version
        if ($installed -gt $target) { Stop-Release ('已安装较高版本 {0}，不降级' -f $item.Version) }
        $installedSignature = Read-InstalledSignatureBytes $item
        if ($installed -eq $target) {
            if ($item.PackageFullName -cne $pins.packageFullName) { Stop-Release '同版本已安装包 FullName 与清单不一致' }
            if (-not (Test-BytesEqual $installedSignature $Context.PackageSignatureBytes)) { Stop-Release '同版本已安装包的签名与最终 MSIX 不同，来源未知，禁止覆盖' }
            $state = 'AlreadyPresent'
        } else {
            if (-not (Test-BytesEqual (Get-P7xSignerRawData $installedSignature) $Context.CerBytes)) { Stop-Release '已安装旧版本的签名者不是固定证书，禁止覆盖' }
            if ($state -cne 'AlreadyPresent') { $state = 'Upgrade' }
        }
    }
    return $state
}

function Resolve-Dependencies($Context, [string]$Root) {
    $pinned = @($Context.Data.dependencies)
    $declared = @($Context.Appx.Dependencies)
    foreach ($pin in $pinned) {
        if (@($declared | Where-Object { $_.Name -ceq $pin.name }).Count -ne 1) { Stop-Release ('清单列出了 MSIX 未声明的本地依赖: ' + $pin.name) }
    }
    $report = @()
    $paths = @()
    foreach ($dependency in $declared) {
        if ($dependency.Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]{1,48}[A-Za-z0-9]$' -or $dependency.MinVersion -cnotmatch '^\d+\.\d+\.\d+\.\d+$') { Stop-Release ('MSIX 依赖声明格式无效: ' + $dependency.Name) }
        $minimum = [version]$dependency.MinVersion
        $installed = @(Get-UserPackagesByName $dependency.Name | Where-Object {
                $_.Name -ceq $dependency.Name -and $_.Publisher -ceq $dependency.Publisher -and $_.IsFramework -and
                ($_.Architecture -ceq 'X64' -or $_.Architecture -ceq 'Neutral') -and $_.Status -ceq 'Ok' -and [version]$_.Version -ge $minimum
            })
        if ($installed.Count -gt 0) {
            $report += [pscustomobject]@{ name = $dependency.Name; minVersion = $dependency.MinVersion; state = 'Satisfied'; installed = @($installed | ForEach-Object { $_.PackageFullName }) }
            continue
        }
        $local = @($pinned | Where-Object { $_.name -ceq $dependency.Name })
        if ($local.Count -ne 1) { Stop-Release ('缺少依赖 {0} >= {1}，且没有已固定的本地依赖包；在请求 UAC 之前停止' -f $dependency.Name, $dependency.MinVersion) }
        $pin = $local[0]
        if ($pin.publisher -cne $dependency.Publisher -or $pin.declaredMinVersion -cne $dependency.MinVersion) { Stop-Release ('本地依赖 {0} 的 Publisher/declaredMinVersion 与 MSIX 声明不一致' -f $pin.name) }
        $path = Resolve-PayloadPath $Root $pin.relativePath ('依赖 ' + $pin.name)
        $stream = Open-PinnedFile $path $pin.sha256 ('依赖 ' + $pin.name)
        $Context.Streams.Add($stream)
        $facts = Get-AppxManifestFacts $stream ('依赖 ' + $pin.name)
        if ($facts.Name -cne $pin.name -or $facts.Publisher -cne $pin.publisher -or $facts.Version -cne $pin.version -or $facts.ProcessorArchitecture -cne $pin.architecture -or -not $facts.IsFramework) {
            Stop-Release ('本地依赖包身份与清单不一致: ' + $pin.name)
        }
        $signature = Get-NativeSignature $path
        if ($signature.Status -cne 'Valid') { Stop-Release ('本地依赖 {0} 的平台签名不是 Valid: {1}' -f $pin.name, $signature.Status) }
        $paths += $path
        $report += [pscustomobject]@{ name = $pin.name; minVersion = $dependency.MinVersion; state = 'WillAddPinnedLocal'; version = $pin.version; relativePath = $pin.relativePath }
    }
    $Context.DependencyPaths = $paths
    $Context.DependencyReport = $report
}

function Assert-PayloadInventory([string]$Root, $Data) {
    $payload = [System.IO.Path]::Combine($Root, 'payload')
    Assert-NoReparsePoint $payload
    $expected = @(@($Data.package.relativePath, $Data.certificate.relativePath) + @($Data.dependencies | ForEach-Object { $_.relativePath }) |
        ForEach-Object { [System.IO.Path]::GetFileName(([string]$_).Replace('/', '\')).ToLowerInvariant() })
    if (@([System.IO.Directory]::GetDirectories($payload)).Count -gt 0) { Stop-Release 'payload 目录不得包含子目录' }
    $extra = @([System.IO.Directory]::GetFiles($payload) | Where-Object { $expected -notcontains [System.IO.Path]::GetFileName($_).ToLowerInvariant() })
    if ($extra.Count -gt 0) { Stop-Release ('payload 含清单未列出的文件: ' + (($extra | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ', ')) }
    $secrets = @([System.IO.Directory]::GetFiles($Root, '*', [System.IO.SearchOption]::AllDirectories) | Where-Object { $_ -match '(?i)\.(pfx|p12|pvk|snk|key|pem)$' })
    if ($secrets.Count -gt 0) { Stop-Release '交付目录中发现私钥/PFX 类文件，拒绝继续' }
}

function Close-PreflightContext($Context) {
    if ($null -eq $Context) { return }
    foreach ($stream in $Context.Streams) { $stream.Dispose() }
    $Context.Streams.Clear()
}

function Invoke-Preflight([string]$Root, [string]$ExpectedManifestSha256, [string]$InstallScriptPath) {
    $ctx = [pscustomobject]@{
        Root = $Root; Data = $null; ManifestSha256 = $null; ExternalDigestVerified = [bool]$ExpectedManifestSha256
        Streams = New-Object System.Collections.Generic.List[System.IDisposable]
        CerBytes = $null; Certificate = $null; PackagePath = $null; PackageSignatureBytes = $null; Appx = $null
        SignerStatusKind = $null; Os = $null; DependencyPaths = @(); DependencyReport = @(); ExistingState = $null; TrustAlreadyPresent = $false
    }
    try {
        $os = Get-OsFacts
        $ctx.Os = $os
        if ($os.PSEdition -cne 'Desktop' -or $os.PSVersion.Major -ne 5) { Stop-Release '请使用系统自带 Windows PowerShell 5.1 (powershell.exe)' }
        if (-not $os.Is64BitProcess -or $os.Architecture -cne 'AMD64') { Stop-Release ('需要 x64 Windows 与 64 位 PowerShell；当前 ' + $os.Architecture) }

        $manifest = Read-ReleaseManifest $Root $ExpectedManifestSha256
        $ctx.Data = $manifest.Data
        $ctx.ManifestSha256 = $manifest.Sha256
        $data = $manifest.Data

        $installPath = [System.IO.Path]::Combine($Root, $script:ReleaseFixed.InstallScriptName)
        $trustPath = [System.IO.Path]::Combine($Root, $script:ReleaseFixed.TrustScriptName)
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($InstallScriptPath), [System.IO.Path]::GetFullPath($installPath), [StringComparison]::OrdinalIgnoreCase)) { Stop-Release 'Install.ps1 必须从交付根目录运行' }
        foreach ($pinnedScript in @(@($installPath, $data.scripts.installSha256, 'Install.ps1'), @($trustPath, $data.scripts.trustSha256, 'Trust-Certificate.ps1'))) {
            Assert-NoReparsePoint $pinnedScript[0]
            if ((Get-Sha256Hex ([System.IO.File]::ReadAllBytes($pinnedScript[0]))) -cne $pinnedScript[1]) { Stop-Release ($pinnedScript[2] + ' SHA-256 与清单不一致') }
        }
        Assert-PayloadInventory $Root $data

        $cerPath = Resolve-PayloadPath $Root $data.certificate.relativePath '证书'
        $cerStream = Open-PinnedFile $cerPath $data.certificate.sha256 '证书'
        try { $ctx.CerBytes = Read-StreamBytes $cerStream } finally { $cerStream.Dispose() }
        $ctx.Certificate = Get-CertificateFacts $ctx.CerBytes
        Assert-CertificateFacts $ctx.Certificate $ctx.CerBytes $data.certificate ([DateTime]::UtcNow)

        $ctx.PackagePath = Resolve-PayloadPath $Root $data.package.relativePath 'MSIX'
        $packageStream = Open-PinnedFile $ctx.PackagePath $data.package.sha256 'MSIX'
        $ctx.Streams.Add($packageStream)
        $ctx.Appx = Get-AppxManifestFacts $packageStream 'MSIX'
        $ctx.PackageSignatureBytes = $ctx.Appx.SignatureBytes
        Assert-PackageIdentity $ctx.Appx $data.package $ctx.Certificate.Subject
        $ctx.SignerStatusKind = Assert-SignerBinding (Get-NativeSignature $ctx.PackagePath) $ctx.CerBytes

        if ($os.Version -lt [version]$data.package.minOsVersion) { Stop-Release ('系统版本 {0} 低于包要求 {1}' -f $os.Version, $data.package.minOsVersion) }
        $policy = Get-SideloadPolicy
        if ($null -ne $policy -and $policy -eq 0) { Stop-Release '组策略 AllowAllTrustedApps=0 禁止旁加载；脚本不会修改该策略' }

        Resolve-Dependencies $ctx $Root
        $ctx.ExistingState = Get-ExistingPackageState $ctx
        $ctx.TrustAlreadyPresent = [bool](Test-MachineTrustedPeopleExact $ctx.CerBytes $ctx.Certificate.Thumbprint)
        return $ctx
    } catch { Close-PreflightContext $ctx; throw }
}

function Get-PreflightSummary($Context) {
    $p = $Context.Data.package
    return [ordered]@{
        status                         = 'Passed'
        manifestSha256                 = $Context.ManifestSha256
        externalManifestDigestVerified = $Context.ExternalDigestVerified
        packageFullName                = $p.packageFullName
        packageFamilyName              = $p.packageFamilyName
        version                        = $p.version
        packageSha256                  = $p.sha256
        certificateThumbprint          = $Context.Certificate.Thumbprint
        signerStatusKind               = $Context.SignerStatusKind
        osVersion                      = [string]$Context.Os.Version
        dependencies                   = @($Context.DependencyReport)
        existingPackage                = $Context.ExistingState
        trustAlreadyPresent            = $Context.TrustAlreadyPresent
    }
}

function Get-TrustBootstrapScript([string]$EncodedData) {
    # 固定引导文本：只有 Base64 JSON 数据被替换进来；读取助手字节、校验 SHA-256、执行同一缓冲区。
    $fixed = @'
$ErrorActionPreference = 'Stop'
try {
    $d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__DATA__')) | ConvertFrom-Json
    if ([string]$d.HelperSha256 -cnotmatch '^[0-9a-f]{64}$' -or [string]$d.ManifestSha256 -cnotmatch '^[0-9a-f]{64}$') { exit 20 }
    $node = [IO.Path]::GetFullPath([string]$d.HelperPath)
    while ($node) {
        if (([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 22 }
        $p = [IO.Directory]::GetParent($node); if ($null -eq $p) { break }; $node = $p.FullName
    }
    $b = [IO.File]::ReadAllBytes([string]$d.HelperPath)
    $h = [Security.Cryptography.SHA256]::Create()
    try { $v = ([BitConverter]::ToString($h.ComputeHash($b))).Replace('-', '').ToLowerInvariant() } finally { $h.Dispose() }
    if ($v -cne [string]$d.HelperSha256) { exit 21 }
    $t = (New-Object Text.UTF8Encoding($false, $true)).GetString($b)
    if ($t.Length -gt 0 -and $t[0] -eq [char]0xFEFF) { $t = $t.Substring(1) }
    & ([scriptblock]::Create($t)) -DeliveryRoot ([string]$d.Root) -ManifestSha256 ([string]$d.ManifestSha256) -HelperSha256 $v -OriginalCallerSid ([string]$d.CallerSid)
    exit 23
} catch { exit 24 }
'@
    return $fixed.Replace('__DATA__', $EncodedData)
}

function Get-TrustBootstrapCommand([string]$Root, [string]$ManifestSha256, [string]$HelperSha256, [string]$CallerSid) {
    $payload = [ordered]@{
        Root = $Root; HelperPath = [System.IO.Path]::Combine($Root, $script:ReleaseFixed.TrustScriptName)
        ManifestSha256 = $ManifestSha256; HelperSha256 = $HelperSha256; CallerSid = $CallerSid
    }
    $data = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress)))
    return [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes((Get-TrustBootstrapScript $data)))
}

function Test-UacCancelled($Exception) {
    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [System.ComponentModel.Win32Exception] -and $current.NativeErrorCode -eq 1223) { return $true }
        if (($current.HResult -band 0xFFFFFFFFL) -eq 0x800704C7L) { return $true }
        $current = $current.InnerException
    }
    return $false
}

function Invoke-ElevatedTrustHelper([string]$Root, [string]$ManifestSha256, [string]$HelperSha256, [string]$CallerSid) {
    $powershell = [System.IO.Path]::Combine([Environment]::GetFolderPath([Environment+SpecialFolder]::System), 'WindowsPowerShell\v1.0\powershell.exe')
    if (-not [System.IO.File]::Exists($powershell)) { Stop-Release '找不到系统 Windows PowerShell' }
    $command = Get-TrustBootstrapCommand $Root $ManifestSha256 $HelperSha256 $CallerSid
    try {
        $child = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $command)
    } catch {
        if (Test-UacCancelled $_.Exception) { throw (New-Object System.OperationCanceledException '用户取消了 UAC；未添加证书，未部署') }
        throw
    }
    return [int]$child.ExitCode
}

function Invoke-PackageDeployment([string]$PackagePath, [string[]]$DependencyPaths) {
    # 不使用 -ForceApplicationShutdown / -ForceUpdateFromAnyVersion / -AllowUnsigned；仅附带缺失且已固定的依赖。
    if ($DependencyPaths.Count -gt 0) { Add-AppxPackage -Path $PackagePath -DependencyPath $DependencyPaths -ErrorAction Stop }
    else { Add-AppxPackage -Path $PackagePath -ErrorAction Stop }
}

function Get-FailureFacts($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    $message = [string]$exception.Message
    $hresult = '0x{0:X8}' -f ($exception.HResult -band 0xFFFFFFFFL)
    if ($message -match '(?i)HRESULT\s*[:：]?\s*(0x[0-9a-f]{8})') { $hresult = '0x' + $Matches[1].Substring(2).ToUpperInvariant() }
    $activity = $null
    if ($message -match '(?i)ActivityId\]?\s*[:：]?\s*\{?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $activity = $Matches[1].ToLowerInvariant() }
    return [ordered]@{ hresult = $hresult; activityId = $activity; message = $message }
}

function Assert-PostInstallIdentity($Context) {
    $pins = $Context.Data.package
    $items = @(Get-UserPackagesByName $pins.name | Where-Object { $_.Name -ceq $pins.name })
    if ($items.Count -ne 1) { Stop-Release ('部署后当前用户下应恰好有 1 个 {0}，实际 {1}' -f $pins.name, $items.Count) }
    $item = $items[0]
    if ($item.PackageFullName -cne $pins.packageFullName -or $item.PackageFamilyName -cne $pins.packageFamilyName -or $item.Publisher -cne $pins.publisher -or
        $item.Version -cne $pins.version -or $item.Architecture -cne 'X64' -or $item.Status -cne 'Ok' -or $item.SignatureKind -cne 'Developer' -or $item.IsFramework) {
        Stop-Release ('部署后实际包身份/状态不符: {0} {1} {2}' -f $item.PackageFullName, $item.Status, $item.SignatureKind)
    }
    $installedSignature = Read-InstalledSignatureBytes $item
    if (-not (Test-BytesEqual $installedSignature $Context.PackageSignatureBytes)) { Stop-Release '已部署包的签名与最终 MSIX 不同' }
    if (@(Get-PackageApplicationIds $item.PackageFullName | Where-Object { $_ -ceq $pins.applicationId }).Count -ne 1) { Stop-Release '部署后 ApplicationId 不匹配' }
    return [ordered]@{
        status = 'Verified'; packageFullName = $item.PackageFullName; packageFamilyName = $item.PackageFamilyName
        version = $item.Version; publisher = $item.Publisher; installLocation = $item.InstallLocation; applicationId = $pins.applicationId
    }
}

function New-InstallResult([bool]$InstallRequested) {
    $mode = 'Preflight'
    if ($InstallRequested) { $mode = 'Install' }
    return [ordered]@{
        resultSchema      = 'BiliCopilot.LocalRelease.InstallResult/1'
        mode              = $mode
        outcome           = 'Blocked'
        originalCallerSid = $null
        phases            = [ordered]@{
            Preflight           = [ordered]@{ status = 'NotRun' }
            CertAdded           = [ordered]@{ status = 'NotAttempted' }
            Deployment          = [ordered]@{ status = 'NotAttempted'; hresult = $null; activityId = $null }
            PostInstallIdentity = [ordered]@{ status = 'NotAttempted' }
            AlreadyPresent      = [ordered]@{ status = 'No' }
            PartialFailure      = [ordered]@{ status = 'No' }
        }
        hresult           = $null
        activityId        = $null
        failure           = $null
        notes             = @('脚本不会撤销证书、卸载包或框架、关闭应用、降级或修改旁加载/组策略设置。')
    }
}

function Invoke-ReleaseInstaller([string]$DeliveryRoot, [bool]$InstallRequested, [string]$ExpectedManifestSha256, [string]$InstallScriptPath) {
    $result = New-InstallResult $InstallRequested
    $ctx = $null
    $certAdded = $false
    $deploymentAttempted = $false
    try {
        if (Test-ProcessElevated) { Stop-Release '父安装脚本拒绝在已提权（管理员）PowerShell 中运行；请在普通 PowerShell 中重新执行' }
        $sid = Get-CallerSid
        $result.originalCallerSid = $sid
        $root = [System.IO.Path]::GetFullPath($DeliveryRoot)
        $ctx = Invoke-Preflight $root $ExpectedManifestSha256 $InstallScriptPath
        $result.phases.Preflight = Get-PreflightSummary $ctx
        if ($ctx.ExistingState -ceq 'AlreadyPresent') {
            $result.phases.AlreadyPresent = [ordered]@{ status = 'Yes'; packageFullName = $ctx.Data.package.packageFullName; note = '同版本、同签名包已为当前用户安装；不是新安装' }
            $result.outcome = 'AlreadyPresent'
            return $result
        }
        if (-not $InstallRequested) { $result.outcome = 'PreflightPassed'; return $result }

        if ($ctx.TrustAlreadyPresent) {
            $result.phases.CertAdded = [ordered]@{ status = 'NotNeeded'; thumbprint = $ctx.Certificate.Thumbprint; store = 'LocalMachine\TrustedPeople' }
        } else {
            Write-Host ('即将通过 UAC 以管理员身份，把固定公钥证书 {0} 添加到本机 LocalMachine\TrustedPeople。' -f $ctx.Certificate.Thumbprint)
            Write-Host '此信任对本机所有用户、以及任何由该证书签名的包生效，并不只限于本应用；私钥不可导出不能阻止同一用户下的恶意进程使用该私钥签名。'
            Write-Host '助手不会写入 Root、不会安装包、不会启动应用。取消 UAC 则不做任何更改。'
            $result.phases.CertAdded = [ordered]@{ status = 'Requested'; thumbprint = $ctx.Certificate.Thumbprint; store = 'LocalMachine\TrustedPeople' }
            $code = Invoke-ElevatedTrustHelper $root $ctx.ManifestSha256 $ctx.Data.scripts.trustSha256 $sid
            $result.phases.CertAdded.helperExitCode = $code
            if (-not (Test-MachineTrustedPeopleExact $ctx.CerBytes $ctx.Certificate.Thumbprint)) {
                Stop-Release ('证书助手退出码 {0}；原用户复查 LocalMachine\TrustedPeople 未发现完全相同证书；未部署' -f $code)
            }
            $certAdded = $true
            $result.phases.CertAdded.status = 'Added'
            $result.phases.CertAdded.verifiedBySid = $sid
        }

        $fresh = Invoke-Preflight $root $ctx.ManifestSha256 $InstallScriptPath
        Close-PreflightContext $ctx
        $ctx = $fresh
        if (-not $ctx.TrustAlreadyPresent) { Stop-Release '部署前复查未发现完全相同的信任证书' }
        if ($ctx.ExistingState -ceq 'AlreadyPresent') {
            $result.phases.AlreadyPresent = [ordered]@{ status = 'Yes'; packageFullName = $ctx.Data.package.packageFullName }
            $result.outcome = 'AlreadyPresent'
            return $result
        }

        $deploymentAttempted = $true
        $result.phases.Deployment = [ordered]@{ status = 'Attempted'; hresult = $null; activityId = $null; dependencyPaths = @($ctx.DependencyPaths | ForEach-Object { [System.IO.Path]::GetFileName($_) }) }
        try { Invoke-PackageDeployment $ctx.PackagePath ([string[]]@($ctx.DependencyPaths)) }
        catch {
            $facts = Get-FailureFacts $_
            $result.phases.Deployment.status = 'Failed'
            $result.phases.Deployment.hresult = $facts.hresult
            $result.phases.Deployment.activityId = $facts.activityId
            throw
        }
        $result.phases.Deployment.status = 'Succeeded'
        $result.phases.PostInstallIdentity = Assert-PostInstallIdentity $ctx
        $result.outcome = 'Installed'
    } catch {
        $failure = $_
        $facts = Get-FailureFacts $failure
        $result.failure = $facts.message
        $result.hresult = $facts.hresult
        $result.activityId = $facts.activityId
        if ($deploymentAttempted) {
            if ($result.phases.Deployment.activityId) { $result.activityId = $result.phases.Deployment.activityId; $result.hresult = $result.phases.Deployment.hresult }
            if ($result.phases.Deployment.status -ceq 'Succeeded' -and $result.phases.PostInstallIdentity.status -ceq 'NotAttempted') {
                $result.phases.PostInstallIdentity = [ordered]@{ status = 'Failed'; reason = $facts.message }
            }
        }
        if (-not $certAdded -and $null -ne $ctx -and -not $ctx.TrustAlreadyPresent -and $result.phases.CertAdded.status -ceq 'Requested') {
            try { $certAdded = [bool](Test-MachineTrustedPeopleExact $ctx.CerBytes $ctx.Certificate.Thumbprint) } catch { }
            if ($certAdded) { $result.phases.CertAdded.status = 'AddedObservedAfterFailure' }
            elseif ($failure.Exception -is [System.OperationCanceledException]) { $result.phases.CertAdded.status = 'UacCancelled' }
            else { $result.phases.CertAdded.status = 'NotAdded' }
        }
        if ($result.phases.Preflight.status -ceq 'NotRun') { $result.phases.Preflight = [ordered]@{ status = 'Failed'; reason = $facts.message } }
        if ($certAdded -or $deploymentAttempted) {
            $result.outcome = 'PartialFailure'
            $result.phases.PartialFailure = [ordered]@{
                status = 'Yes'; certificateAdded = $certAdded; deploymentAttempted = $deploymentAttempted
                hresult = $result.hresult; activityId = $result.activityId; reason = $facts.message
                note = '已发生的更改不会被自动撤销；修复原因后重新运行会复查全部条件并跳过已存在的证书和已满足的依赖。'
            }
        } elseif ($failure.Exception -is [System.OperationCanceledException]) {
            $result.outcome = 'UacCancelled'
        } else {
            $result.outcome = 'Blocked'
        }
    } finally { Close-PreflightContext $ctx }
    return $result
}

function Get-InstallerExitCode($Result) {
    switch ($Result.outcome) {
        'PreflightPassed' { return 0 }
        'AlreadyPresent' { return 0 }
        'Installed' { return 0 }
        'PartialFailure' { return 2 }
        'UacCancelled' { return 3 }
        default { return 1 }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $exitCode = 1
    try {
        $result = Invoke-ReleaseInstaller -DeliveryRoot $PSScriptRoot -InstallRequested ([bool]$Install) -ExpectedManifestSha256 $ExpectedManifestSha256 -InstallScriptPath $PSCommandPath
        Write-Host ('结果: {0}' -f $result.outcome)
        if ($result.failure) { Write-Host ('原因: ' + $result.failure) }
        if ($result.outcome -ceq 'PreflightPassed') { Write-Host '预检通过，未做任何更改。确认后执行: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install' }
        $exitCode = Get-InstallerExitCode $result
    } catch {
        Write-Output ('BLOCKED: ' + $_.Exception.Message)
    }
    exit $exitCode
}
