[CmdletBinding()]
param(
    [string]$DeliveryRoot,
    [string]$ManifestSha256,
    [string]$HelperSha256,
    [string]$OriginalCallerSid
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# 提权证书助手：只由 Install.ps1 的固定引导程序在 UAC 提权后执行（读取本文件字节、校验 SHA-256、执行同一缓冲区）。
# 唯一写操作：把已重新校验的固定公钥证书（同一份已读入字节）加入 LocalMachine\TrustedPeople。
# 不写 Root、不处理私钥、不改策略、不安装包或依赖、不启动应用。
# 退出码: 0 已添加；10 已存在完全相同证书（未更改）；1 拒绝/失败（未更改）。

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

function Add-CertificateToMachineTrustedPeople([byte[]]$CerBytes) {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, $CerBytes)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        if ($certificate.HasPrivateKey -or -not (Test-BytesEqual $certificate.RawData $CerBytes)) { Stop-Release '待导入对象不是已校验的同一份公钥证书字节' }
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite -bor [System.Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
        $store.Add($certificate)
    } finally { $store.Close(); $certificate.Reset() }
}

function Invoke-TrustHelper([string]$DeliveryRoot, [string]$ManifestSha256, [string]$HelperSha256, [string]$OriginalCallerSid) {
    $packageStream = $null
    try {
        if (-not (Test-ProcessElevated)) { Stop-Release '证书助手只能由 Install.ps1 经 UAC 提权启动' }
        if ($ManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or $HelperSha256 -cnotmatch '^[0-9a-f]{64}$') { Stop-Release '父进程固定的清单/助手摘要无效' }
        if ([string]::IsNullOrEmpty($DeliveryRoot)) { Stop-Release '缺少交付目录' }
        $root = [System.IO.Path]::GetFullPath($DeliveryRoot)
        $manifest = Read-ReleaseManifest $root $ManifestSha256
        $data = $manifest.Data
        if ($data.scripts.trustSha256 -cne $HelperSha256) { Stop-Release '正在执行的助手字节与清单 trustSha256 不一致' }

        $cerPath = Resolve-PayloadPath $root $data.certificate.relativePath '证书'
        $cerStream = Open-PinnedFile $cerPath $data.certificate.sha256 '证书'
        try { $cerBytes = Read-StreamBytes $cerStream } finally { $cerStream.Dispose() }
        $facts = Get-CertificateFacts $cerBytes
        Assert-CertificateFacts $facts $cerBytes $data.certificate ([DateTime]::UtcNow)

        $packagePath = Resolve-PayloadPath $root $data.package.relativePath 'MSIX'
        $packageStream = Open-PinnedFile $packagePath $data.package.sha256 'MSIX'
        $appx = Get-AppxManifestFacts $packageStream 'MSIX'
        Assert-PackageIdentity $appx $data.package $facts.Subject
        $null = Assert-SignerBinding (Get-NativeSignature $packagePath) $cerBytes

        if (Test-MachineTrustedPeopleExact $cerBytes $facts.Thumbprint) {
            Write-Host ('证书 {0} 已受信任，未更改。' -f $facts.Thumbprint)
            return 10
        }
        Add-CertificateToMachineTrustedPeople $cerBytes
        if (-not (Test-MachineTrustedPeopleExact $cerBytes $facts.Thumbprint)) { Stop-Release '写入后复查未发现完全相同证书' }
        Write-Host ('已将证书 {0} 添加到 LocalMachine\TrustedPeople。' -f $facts.Thumbprint)
        return 0
    } catch {
        Write-Host ('证书助手未完成: ' + $_.Exception.Message)
        return 1
    } finally { if ($null -ne $packageStream) { $packageStream.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-TrustHelper -DeliveryRoot $DeliveryRoot -ManifestSha256 $ManifestSha256 -HelperSha256 $HelperSha256 -OriginalCallerSid $OriginalCallerSid)
}
