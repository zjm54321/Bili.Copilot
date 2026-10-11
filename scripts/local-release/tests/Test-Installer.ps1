param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot
)
# 惰性源码级测试：伪造清单与 ZIP/字节夹具；不生成/读取真实证书或私钥，不签名，不访问信任存储写入，
# 不安装/注册/启动任何包。所有平台写操作接口均被替换为记录调用的桩函数。
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$script:Results = New-Object System.Collections.Generic.List[string]
$script:Failures = 0
function Add-Result([string]$Name, [bool]$Pass, [string]$Detail) {
    $line = '{0}: {1}' -f $(if ($Pass) { 'PASS' } else { 'FAIL' }), $Name
    if ($Detail) { $line += ' -- ' + $Detail }
    $script:Results.Add($line)
    if (-not $Pass) { $script:Failures++ }
    Write-Host $line
}
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw ('ASSERT: ' + $Message) } }
function Invoke-Case([string]$Name, [scriptblock]$Body) {
    try { $detail = & $Body; Add-Result $Name $true ([string]($detail | Select-Object -First 1)) }
    catch { Add-Result $Name $false $_.Exception.Message }
}

$installSource = Join-Path $SourceRoot 'Install.ps1'
$trustSource = Join-Path $SourceRoot 'Trust-Certificate.ps1'
$templateSource = Join-Path $SourceRoot 'release-artifacts.json'
$selfPath = $MyInvocation.MyCommand.Path

# ---------- 1. 语法与静态检查 ----------
$asts = @{}
foreach ($file in @($installSource, $trustSource, $selfPath)) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    $asts[$file] = $ast
    Add-Result ('PS5.1 parser ' + [IO.Path]::GetFileName($file)) ($errors.Count -eq 0) (($errors | ForEach-Object { $_.Message }) -join '; ')
}

function Get-CommandNames($Ast) {
    return @($Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })
}

Invoke-Case 'shared validation region byte-identical in both scripts' {
    $pattern = '(?s)#region SharedValidation\r?\n.*?#endregion SharedValidation'
    $a = [regex]::Match([IO.File]::ReadAllText($installSource), $pattern).Value
    $b = [regex]::Match([IO.File]::ReadAllText($trustSource), $pattern).Value
    Assert-That ($a.Length -gt 1000 -and $a -ceq $b) 'region differs'
    'length=' + $a.Length
}

$forbiddenEverywhere = @('Import-Certificate', 'Import-PfxCertificate', 'Export-PfxCertificate', 'Export-Certificate', 'New-SelfSignedCertificate', 'certutil', 'certutil.exe', 'signtool', 'signtool.exe',
    'Remove-AppxPackage', 'Remove-AppxProvisionedPackage', 'Add-AppxProvisionedPackage', 'Set-ItemProperty', 'New-ItemProperty', 'Remove-ItemProperty', 'New-Item', 'Remove-Item', 'Set-Content', 'Out-File',
    'Set-ExecutionPolicy', 'Invoke-Expression', 'iex', 'Stop-Process', 'Invoke-WebRequest', 'Invoke-RestMethod', 'Start-BitsTransfer', 'reg', 'reg.exe', 'Set-AuthenticodeSignature', 'Invoke-Item', 'explorer.exe')
Invoke-Case 'Install.ps1 command inventory has no forbidden operations' {
    $names = Get-CommandNames $asts[$installSource]
    $bad = @($names | Where-Object { $forbiddenEverywhere -contains $_ })
    Assert-That ($bad.Count -eq 0) ('forbidden: ' + ($bad -join ','))
    $starts = @($asts[$installSource].FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-Process' }, $true))
    Assert-That ($starts.Count -eq 1 -and $starts[0].Extent.Text -match '-Verb RunAs' -and $starts[0].Extent.Text -match '\$powershell') 'Start-Process must be the single RunAs system powershell'
    $adds = @($asts[$installSource].FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Add-AppxPackage' }, $true))
    Assert-That ($adds.Count -eq 2) 'two Add-AppxPackage forms expected'
    foreach ($add in $adds) { Assert-That ($add.Extent.Text -notmatch '(?i)-Force|-AllowUnsigned|-Register|-DeferRegistration|-Stage|-Volume|-ExternalLocation') ('unsafe Add-AppxPackage: ' + $add.Extent.Text) }
    $text = [IO.File]::ReadAllText($installSource)
    Assert-That ($text -notmatch 'OpenFlags\]::ReadWrite' -and $text -notmatch 'StoreName\]::Root' -and $text -notmatch "'Root'" -and $text -notmatch '\.Add\(\$cert') 'parent must not write any certificate store'
    'commands=' + (@($names | Select-Object -Unique).Count)
}
Invoke-Case 'Trust-Certificate.ps1 command inventory: trust-only helper' {
    $names = Get-CommandNames $asts[$trustSource]
    $bad = @($names | Where-Object { ($forbiddenEverywhere + @('Add-AppxPackage', 'Start-Process', 'Get-AppxPackage', 'Get-AppxPackageManifest')) -contains $_ })
    Assert-That ($bad.Count -eq 0) ('forbidden: ' + ($bad -join ','))
    $text = [IO.File]::ReadAllText($trustSource)
    Assert-That ([regex]::Matches($text, 'OpenFlags\]::ReadWrite').Count -eq 1) 'exactly one ReadWrite store open'
    Assert-That ([regex]::Matches($text, '\$store\.Add\(').Count -eq 1) 'exactly one store Add'
    Assert-That ($text -notmatch 'StoreName\]::Root' -and $text -notmatch "'Root'" -and $text -notmatch 'CurrentUser') 'never Root / CurrentUser store'
    $storeNames = @([regex]::Matches($text, 'StoreName\]::(\w+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $locations = @([regex]::Matches($text, 'StoreLocation\]::(\w+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    Assert-That (($storeNames -join ',') -ceq 'TrustedPeople' -and ($locations -join ',') -ceq 'LocalMachine') 'only LocalMachine\TrustedPeople'
    'stores=' + ($storeNames -join ',')
}
Invoke-Case 'top-level entrypoint rejects elevation before any delivery-folder processing' {
    $text = [IO.File]::ReadAllText($installSource)
    $body = [regex]::Match($text, '(?s)function Invoke-ReleaseInstaller.*?\n\}').Value
    $first = [regex]::Match($body, '(?s)try \{\s*(.*?)\n').Groups[1].Value
    Assert-That ($first -match '^if \(Test-ProcessElevated\)') ('first statement: ' + $first)
    'first=' + $first.Substring(0, 30)
}

# ---------- 2. 加载被测函数并替换平台接口 ----------
. $installSource
$realGetCertificateFacts = ${function:Get-CertificateFacts}
$realGetNativeSignature = ${function:Get-NativeSignature}
$untrustedRootMessage = (New-Object System.ComponentModel.Win32Exception ([int]-2146762487)).Message
$publisherId = Get-PublisherId 'CN=BiliCopilot Local Release'
$fullName = 'BiliCopilot.LocalRelease_2.2611.1.0_x64__' + $publisherId
$familyName = 'BiliCopilot.LocalRelease_' + $publisherId
$fixtureThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
$msixSignature = [Text.Encoding]::ASCII.GetBytes('PKCX-INERT-FIXTURE-NOT-A-SIGNATURE')
$depPublisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'

function Reset-Mock {
    $script:M = @{
        Elevated = $false; Sid = 'S-1-5-21-1000-1000-1000-1001'; SidAfterHelper = $null
        UseRealCert = $false; Cert = @{}; UseRealSignature = $false
        Signature = $null; Packages = New-Object System.Collections.ArrayList; InstalledSig = @{}; OldSigner = $null
        Trusted = $false; HelperAddsTrust = $true; HelperCode = 0; UacCancel = $false; HelperCalls = 0
        DeployCalls = 0; DeployArgs = $null; DeployThrow = $null; DeployStatus = 'Ok'; DeploySideEffect = $null
        Os = [pscustomobject]@{ Version = [version]'10.0.22631.0'; Architecture = 'AMD64'; Is64BitProcess = $true; PSEdition = 'Desktop'; PSVersion = [version]'5.1.22621.4391' }
        Policy = $null; CerBytes = $null
    }
}
function Test-ProcessElevated { return [bool]$script:M.Elevated }
function Get-CallerSid { return $script:M.Sid }
function Get-OsFacts { return $script:M.Os }
function Get-SideloadPolicy { return $script:M.Policy }
function Get-CertificateFacts([byte[]]$Bytes) {
    if ($script:M.UseRealCert) { return & $realGetCertificateFacts $Bytes }
    $facts = @{ RawData = $Bytes; Thumbprint = $fixtureThumbprint; Subject = 'CN=BiliCopilot Local Release'
        NotBeforeUtc = [DateTime]::SpecifyKind([DateTime]'2026-01-01T00:00:00', 'Utc'); NotAfterUtc = [DateTime]::SpecifyKind([DateTime]'2027-12-31T00:00:00', 'Utc')
        Ekus = @('1.3.6.1.5.5.7.3.3'); DigitalSignature = $true; IsCertificateAuthority = $false; HasPrivateKey = $false }
    foreach ($key in $script:M.Cert.Keys) { $facts[$key] = $script:M.Cert[$key] }
    return [pscustomobject]$facts
}
function Get-NativeSignature([string]$Path) {
    if ($script:M.UseRealSignature) { return & $realGetNativeSignature $Path }
    if ($Path -match '\\payload\\dep-') { return [pscustomobject]@{ Status = 'Valid'; StatusMessage = 'Signature verified.'; SignerRawData = [byte[]](1, 2, 3) } }
    if ($null -ne $script:M.Signature) { return $script:M.Signature }
    return [pscustomobject]@{ Status = 'UnknownError'; StatusMessage = $untrustedRootMessage; SignerRawData = $script:M.CerBytes }
}
function Get-UserPackagesByName([string]$Name) { return @($script:M.Packages | Where-Object { $_.Name -ceq $Name }) }
function Get-UserPackageSnapshot { return @($script:M.Packages | Sort-Object PackageFullName) }
function Read-InstalledSignatureBytes($Record) {
    if ($script:M.InstalledSig.ContainsKey($Record.PackageFullName)) { return , $script:M.InstalledSig[$Record.PackageFullName] }
    return , $msixSignature
}
function Get-P7xSignerRawData([byte[]]$Bytes) { return , $script:M.OldSigner }
function Get-PackageApplicationIds([string]$PackageFullName) { return @('App') }
function Test-MachineTrustedPeopleExact([byte[]]$CerBytes, [string]$Thumbprint) { return [bool]$script:M.Trusted }
function Invoke-ElevatedTrustHelper([string]$Root, [string]$ManifestSha256, [string]$HelperSha256, [string]$CallerSid) {
    $script:M.HelperCalls++
    if ($script:M.UacCancel) { throw (New-Object System.OperationCanceledException 'UAC cancelled (fixture)') }
    if ($script:M.HelperAddsTrust) { $script:M.Trusted = $true }
    if ($script:M.SidAfterHelper) { $script:M.Sid = $script:M.SidAfterHelper }
    return $script:M.HelperCode
}
function New-PackageRecord([string]$Name, [string]$Publisher, [string]$Version, [string]$Family, [string]$Full, [string]$Kind, [bool]$Framework, [string]$Arch) {
    return [pscustomobject]@{ Name = $Name; Publisher = $Publisher; PackageFamilyName = $Family; PackageFullName = $Full; Version = $Version; Architecture = $Arch
        Status = 'Ok'; SignatureKind = $Kind; IsFramework = $Framework; InstallLocation = 'C:\Program Files\WindowsApps\' + $Full }
}
function Invoke-PackageDeployment([string]$PackagePath, [string[]]$DependencyPaths) {
    $script:M.DeployCalls++
    $script:M.DeployArgs = @{ Path = $PackagePath; Dependencies = @($DependencyPaths) }
    if ($script:M.DeployThrow) { throw (New-Object System.Exception $script:M.DeployThrow) }
    foreach ($old in @($script:M.Packages | Where-Object { $_.Name -ceq 'BiliCopilot.LocalRelease' })) { $script:M.Packages.Remove($old) }
    $record = New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.1.0' $familyName $fullName 'Developer' $false 'X64'
    $record.Status = $script:M.DeployStatus
    [void]$script:M.Packages.Add($record)
    foreach ($dep in @($DependencyPaths)) {
        [void]$script:M.Packages.Add((New-PackageRecord 'Microsoft.Fixture.Framework' $depPublisher '1.6.0.0' 'Microsoft.Fixture.Framework_8wekyb3d8bbwe' 'Microsoft.Fixture.Framework_1.6.0.0_x64__8wekyb3d8bbwe' 'System' $true 'X64'))
    }
    if ($script:M.DeploySideEffect) { & $script:M.DeploySideEffect }
}

# ---------- 3. 惰性夹具 ----------
function Get-AppxXml([hashtable]$Id) {
    $deps = ''
    foreach ($d in @($Id.Deps)) { $deps += ('<PackageDependency Name="{0}" Publisher="{1}" MinVersion="{2}" />' -f $d.Name, [Security.SecurityElement]::Escape($d.Publisher), $d.MinVersion) }
    $framework = ''
    if ($Id.Framework) { $framework = '<Framework>true</Framework>' }
    $apps = '<Applications><Application Id="{0}" Executable="Inert.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>' -f $Id.AppId
    if ($Id.Framework) { $apps = '' }
    return ('<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="{0}" Publisher="{1}" Version="{2}" ProcessorArchitecture="{3}" /><Properties><DisplayName>INERT</DisplayName><PublisherDisplayName>INERT</PublisherDisplayName><Logo>a.png</Logo>{4}</Properties><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="{5}" MaxVersionTested="10.0.26100.0" />{6}</Dependencies>{7}</Package>' -f
        $Id.Name, [Security.SecurityElement]::Escape($Id.Publisher), $Id.Version, $Id.Arch, $framework, $Id.MinOs, $deps, $apps)
}
function Write-InertZip([string]$Path, [string]$Xml, [byte[]]$Signature) {
    $fs = [IO.File]::Create($Path)
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entry in @(@('AppxManifest.xml', [Text.Encoding]::UTF8.GetBytes($Xml)), @('AppxSignature.p7x', $Signature), @('AppxBlockMap.xml', [Text.Encoding]::ASCII.GetBytes('<INERT/>')))) {
            $stream = $zip.CreateEntry($entry[0]).Open()
            try { $stream.Write($entry[1], 0, $entry[1].Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose(); $fs.Dispose() }
}
function Get-FileSha([string]$Path) { return Get-Sha256Hex ([IO.File]::ReadAllBytes($Path)) }
$script:FixtureIndex = 0
function New-Fixture([hashtable]$Options) {
    if ($null -eq $Options) { $Options = @{} }
    $script:FixtureIndex++
    $dir = Join-Path $WorkRoot ('fx{0:D3}' -f $script:FixtureIndex)
    [void][IO.Directory]::CreateDirectory((Join-Path $dir 'payload'))
    [IO.File]::Copy($installSource, (Join-Path $dir 'Install.ps1'))
    [IO.File]::Copy($trustSource, (Join-Path $dir 'Trust-Certificate.ps1'))
    $id = @{ Name = 'BiliCopilot.LocalRelease'; Publisher = 'CN=BiliCopilot Local Release'; Version = '2.2611.1.0'; Arch = 'x64'; AppId = 'App'; MinOs = '10.0.19041.0'; Deps = @(); Framework = $false }
    if ($Options.ContainsKey('Id')) { foreach ($k in $Options.Id.Keys) { $id[$k] = $Options.Id[$k] } }
    $msixPath = Join-Path $dir 'payload\BiliCopilot.LocalRelease_2.2611.1.0_x64.msix'
    Write-InertZip $msixPath (Get-AppxXml $id) $msixSignature
    $cerPath = Join-Path $dir 'payload\BiliCopilot.LocalRelease.cer'
    $cerBytes = [Text.Encoding]::ASCII.GetBytes('INERT-CER-FIXTURE: not DER, not a certificate, never trust or import ' + ('0' * 64))
    [IO.File]::WriteAllBytes($cerPath, $cerBytes)
    $script:M.CerBytes = $cerBytes
    $manifest = [ordered]@{
        schemaVersion = 1
        package       = [ordered]@{ relativePath = 'payload\BiliCopilot.LocalRelease_2.2611.1.0_x64.msix'; sha256 = (Get-FileSha $msixPath); name = 'BiliCopilot.LocalRelease'; publisher = 'CN=BiliCopilot Local Release'; version = '2.2611.1.0'; architecture = 'x64'
            packageFamilyName = $familyName; packageFullName = $fullName; applicationId = 'App'; minOsVersion = '10.0.19041.0' }
        certificate   = [ordered]@{ relativePath = 'payload\BiliCopilot.LocalRelease.cer'; sha256 = (Get-FileSha $cerPath); derSha256 = (Get-FileSha $cerPath); thumbprint = $fixtureThumbprint; subject = 'CN=BiliCopilot Local Release'
            notBeforeUtc = '2026-01-01T00:00:00Z'; notAfterUtc = '2027-12-31T00:00:00Z'; codeSigningEku = '1.3.6.1.5.5.7.3.3'; digitalSignature = $true; isCertificateAuthority = $false }
        dependencies  = @()
        scripts       = [ordered]@{ installSha256 = (Get-FileSha (Join-Path $dir 'Install.ps1')); trustSha256 = (Get-FileSha (Join-Path $dir 'Trust-Certificate.ps1')) }
    }
    if ($Options.ContainsKey('LocalDependency') -and $Options.LocalDependency) {
        $depPath = Join-Path $dir 'payload\dep-Microsoft.Fixture.Framework.msix'
        $depId = @{ Name = 'Microsoft.Fixture.Framework'; Publisher = $depPublisher; Version = '1.6.0.0'; Arch = 'x64'; AppId = ''; MinOs = '10.0.17763.0'; Deps = @(); Framework = $true }
        Write-InertZip $depPath (Get-AppxXml $depId) ([Text.Encoding]::ASCII.GetBytes('INERT-DEP-SIG'))
        $manifest.dependencies = @([ordered]@{ relativePath = 'payload\dep-Microsoft.Fixture.Framework.msix'; sha256 = (Get-FileSha $depPath); name = 'Microsoft.Fixture.Framework'; publisher = $depPublisher; version = '1.6.0.0'; architecture = 'x64'; declaredMinVersion = '1.6.0.0' })
    }
    if ($Options.ContainsKey('Mutate') -and $Options.Mutate) { & $Options.Mutate $manifest $dir }
    $json = ConvertTo-Json -InputObject $manifest -Depth 6
    if ($Options.ContainsKey('RawManifest')) { $json = $Options.RawManifest }
    [IO.File]::WriteAllText((Join-Path $dir 'release-artifacts.json'), $json, (New-Object Text.UTF8Encoding($false)))
    if ($Options.ContainsKey('After') -and $Options.After) { & $Options.After $dir }
    return $dir
}
function Invoke-Fixture([string]$Dir, [bool]$Install, [string]$Digest) {
    return Invoke-ReleaseInstaller -DeliveryRoot $Dir -InstallRequested $Install -ExpectedManifestSha256 $Digest -InstallScriptPath (Join-Path $Dir 'Install.ps1')
}
function Assert-NoChanges($Result) {
    Assert-That ($script:M.HelperCalls -eq 0) ('helper calls=' + $script:M.HelperCalls)
    Assert-That ($script:M.DeployCalls -eq 0) ('deploy calls=' + $script:M.DeployCalls)
    Assert-That ($Result.phases.CertAdded.status -ceq 'NotAttempted' -and $Result.phases.Deployment.status -ceq 'NotAttempted') 'phases must show no change'
}
function Test-Blocked([string]$Name, [hashtable]$Options, [string]$Expect, [bool]$Install = $true, [scriptblock]$Arrange = $null) {
    Invoke-Case $Name {
        Reset-Mock
        $dir = New-Fixture $Options
        if ($Arrange) { & $Arrange }
        $r = Invoke-Fixture $dir $Install ''
        Assert-That ($r.outcome -ceq 'Blocked') ('outcome=' + $r.outcome + ' failure=' + $r.failure)
        Assert-That ([string]$r.failure -match $Expect) ('unexpected reason: ' + $r.failure)
        Assert-NoChanges $r
        $r.failure
    }
}

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

# ---------- 4. 预检拒绝路径 ----------
Invoke-Case 'publisher ID derivation matches public Microsoft vector 8wekyb3d8bbwe' {
    $v = Get-PublisherId 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
    Assert-That ($v -ceq '8wekyb3d8bbwe') $v
    'fixtureOnlyPublisherId=' + $publisherId
}
Test-Blocked 'shipped template with __PENDING placeholders blocks' @{ RawManifest = [IO.File]::ReadAllText($templateSource) } '占位字段'
Invoke-Case 'valid inert fixture default precheck passes without any writes' {
    Reset-Mock; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $false ''
    Assert-That ($r.outcome -ceq 'PreflightPassed') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-NoChanges $r
    Assert-That ($r.phases.Preflight.signerStatusKind -ceq 'UntrustedRootOnly' -and -not $r.phases.Preflight.externalManifestDigestVerified) 'summary'
    'existing=' + $r.phases.Preflight.existingPackage
}
Invoke-Case 'external manifest digest match is recorded' {
    Reset-Mock; $dir = New-Fixture @{}
    $digest = Get-FileSha (Join-Path $dir 'release-artifacts.json')
    $r = Invoke-Fixture $dir $false $digest.ToUpperInvariant()
    Assert-That ($r.outcome -ceq 'PreflightPassed' -and $r.phases.Preflight.externalManifestDigestVerified) ('outcome=' + $r.outcome)
}
Invoke-Case 'external manifest digest mismatch blocks' {
    Reset-Mock; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ('ab' * 32)
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '外部记录不一致') $r.failure
    Assert-NoChanges $r
}
Test-Blocked 'MSIX digest mismatch blocks before trust/deploy' @{ After = { param($d) $p = Join-Path $d 'payload\BiliCopilot.LocalRelease_2.2611.1.0_x64.msix'; $b = [IO.File]::ReadAllBytes($p); $b[$b.Length - 1] = $b[$b.Length - 1] -bxor 1; [IO.File]::WriteAllBytes($p, $b) } } 'MSIX SHA-256 不匹配'
Test-Blocked 'CER digest mismatch blocks' @{ After = { param($d) [IO.File]::AppendAllText((Join-Path $d 'payload\BiliCopilot.LocalRelease.cer'), 'x') } } '证书 SHA-256 不匹配'
Test-Blocked 'Install.ps1 digest mismatch blocks' @{ Mutate = { param($m, $d) $m.scripts.installSha256 = 'cd' * 32 } } 'Install.ps1 SHA-256'
Test-Blocked 'Trust-Certificate.ps1 digest mismatch blocks' @{ After = { param($d) [IO.File]::AppendAllText((Join-Path $d 'Trust-Certificate.ps1'), "`n# tampered") } } 'Trust-Certificate.ps1 SHA-256'
foreach ($bad in @('..\escape.msix', 'C:\escape.msix', 'payload\..\escape.msix', 'payload\sub\escape.msix', 'payload\a*.msix', 'payload\https://x.invalid/a.msix')) {
    $script:CaseData = $bad
    Test-Blocked ('path blocker ' + $bad) @{ Mutate = { param($m, $d) $m.package.relativePath = $script:CaseData } } '(relativePath|URL|通配符)'
}
Test-Blocked 'unlisted payload file blocks' @{ After = { param($d) [IO.File]::WriteAllText((Join-Path $d 'payload\extra.dll'), 'x') } } '清单未列出'
Test-Blocked 'PFX-like file anywhere in delivery blocks' @{ After = { param($d) [IO.File]::WriteAllText((Join-Path $d 'signing.pfx'), 'x') } } '私钥/PFX'
Test-Blocked 'manifest extra property blocks' @{ Mutate = { param($m, $d) $m.package['installCommand'] = 'x' } } '多余'
Test-Blocked 'historical version pin 2.2511.3.0 blocks' @{ Mutate = { param($m, $d) $m.package.version = '2.2511.3.0' } } 'package.version'
Test-Blocked 'forged PFN not derived from Publisher blocks' @{ Mutate = { param($m, $d) $m.package.packageFamilyName = 'BiliCopilot.LocalRelease_0000000000000' } } 'packageFamilyName'
Test-Blocked 'certificate.isCertificateAuthority=true in manifest blocks' @{ Mutate = { param($m, $d) $m.certificate.isCertificateAuthority = $true } } 'isCertificateAuthority'

foreach ($case in @(
        @{ N = 'subject mismatch'; C = @{ Subject = 'CN=Someone Else' }; E = 'Subject' },
        @{ N = 'thumbprint mismatch'; C = @{ Thumbprint = 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF' }; E = '指纹' },
        @{ N = 'CA=true'; C = @{ IsCertificateAuthority = $true }; E = 'CA=true' },
        @{ N = 'no DigitalSignature'; C = @{ DigitalSignature = $false }; E = 'DigitalSignature' },
        @{ N = 'no Code Signing EKU'; C = @{ Ekus = @('1.3.6.1.5.5.7.3.1') }; E = 'Code Signing' },
        @{ N = 'private key present'; C = @{ HasPrivateKey = $true }; E = '私钥' },
        @{ N = 'validity differs from pin'; C = @{ NotAfterUtc = [DateTime]::SpecifyKind([DateTime]'2028-01-01T00:00:00', 'Utc') }; E = '有效期与清单' })) {
    $script:CaseData = $case
    Test-Blocked ('certificate ' + $case.N + ' blocks') @{} $case.E $true { $script:M.Cert = $script:CaseData.C }
}
Invoke-Case 'certificate expired (pins consistent) blocks' {
    Reset-Mock; $script:M.Cert = @{ NotBeforeUtc = [DateTime]::SpecifyKind([DateTime]'2024-01-01T00:00:00', 'Utc'); NotAfterUtc = [DateTime]::SpecifyKind([DateTime]'2025-01-01T00:00:00', 'Utc') }
    $dir = New-Fixture @{ Mutate = { param($m, $d) $m.certificate.notBeforeUtc = '2024-01-01T00:00:00Z'; $m.certificate.notAfterUtc = '2025-01-01T00:00:00Z' } }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '不在有效期') $r.failure
    Assert-NoChanges $r
}
Invoke-Case 'real X509 parser path rejects inert non-DER CER bytes' {
    Reset-Mock; $script:M.UseRealCert = $true; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match 'DER') $r.failure
    Assert-NoChanges $r
    $r.failure
}

foreach ($case in @(
        @{ S = 'NotSigned'; Msg = 'x' }, @{ S = 'HashMismatch'; Msg = 'x' }, @{ S = 'NotTrusted'; Msg = 'explicit distrust' }, @{ S = 'NotSupportedFileFormat'; Msg = 'x' },
        @{ S = 'Incompatible'; Msg = 'x' }, @{ S = 'UnknownError'; Msg = 'The certificate is revoked.' })) {
    $script:CaseData = $case
    Test-Blocked ('native signer status {0} blocks' -f $case.S) @{} '平台签名状态不可接受' $true { $script:M.Signature = [pscustomobject]@{ Status = $script:CaseData.S; StatusMessage = $script:CaseData.Msg; SignerRawData = $script:M.CerBytes } }
}
Test-Blocked 'native signer DER mismatch (Valid status) blocks' @{} '签名者证书 DER' $true { $script:M.Signature = [pscustomobject]@{ Status = 'Valid'; StatusMessage = 'ok'; SignerRawData = [byte[]](9, 9, 9) } }
Test-Blocked 'untrusted-root status without signer certificate blocks' @{} '签名者证书 DER' $true { $script:M.Signature = [pscustomobject]@{ Status = 'UnknownError'; StatusMessage = $untrustedRootMessage; SignerRawData = $null } }
Invoke-Case 'real Get-AuthenticodeSignature on inert unsigned ZIP blocks (read-only)' {
    Reset-Mock; $script:M.UseRealSignature = $true; $dir = New-Fixture @{}
    $raw = & $realGetNativeSignature (Join-Path $dir 'payload\BiliCopilot.LocalRelease_2.2611.1.0_x64.msix')
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '平台签名状态不可接受') $r.failure
    Assert-NoChanges $r
    'actualStatus=' + $raw.Status
}

Test-Blocked 'AppxManifest identity version mismatch blocks' @{ Id = @{ Version = '2.2511.3.0' } } '包身份与清单不一致'
Test-Blocked 'AppxManifest publisher mismatch blocks' @{ Id = @{ Publisher = 'CN=Other' } } '包身份与清单不一致'
Test-Blocked 'AppxManifest architecture x86 blocks' @{ Id = @{ Arch = 'x86' } } '包身份与清单不一致'
Test-Blocked 'AppxManifest ApplicationId mismatch blocks' @{ Id = @{ AppId = 'Other' } } 'ApplicationId'
Test-Blocked 'AppxManifest MinVersion mismatch blocks' @{ Id = @{ MinOs = '10.0.17763.0' } } 'MinVersion'
Test-Blocked 'OS older than minOsVersion blocks' @{} '低于包要求' $true { $script:M.Os = [pscustomobject]@{ Version = [version]'10.0.17763.0'; Architecture = 'AMD64'; Is64BitProcess = $true; PSEdition = 'Desktop'; PSVersion = [version]'5.1.0.0' } }
Test-Blocked 'PowerShell 7 (Core) blocks' @{} 'Windows PowerShell 5.1' $true { $script:M.Os = [pscustomobject]@{ Version = [version]'10.0.22631.0'; Architecture = 'AMD64'; Is64BitProcess = $true; PSEdition = 'Core'; PSVersion = [version]'7.4.0' } }
Test-Blocked 'ARM64 host blocks' @{} 'x64' $true { $script:M.Os = [pscustomobject]@{ Version = [version]'10.0.22631.0'; Architecture = 'ARM64'; Is64BitProcess = $true; PSEdition = 'Desktop'; PSVersion = [version]'5.1.0.0' } }
Test-Blocked 'sideloading disabled by policy blocks (policy untouched)' @{} 'AllowAllTrustedApps' $true { $script:M.Policy = 0 }

# ---------- 5. 身份冲突 ----------
$conflicts = @(
    @{ N = 'same Name different Publisher'; R = { New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=Other' '2.2611.1.0' 'BiliCopilot.LocalRelease_other' 'BiliCopilot.LocalRelease_2.2611.1.0_x64__other' 'Developer' $false 'X64' }; E = 'Publisher/PFN' },
    @{ N = 'same Name Store/unknown origin'; R = { New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.0.0' $familyName ('BiliCopilot.LocalRelease_2.2611.0.0_x64__' + $publisherId) 'Store' $false 'X64' }; E = '来源未知' },
    @{ N = 'higher version installed'; R = { New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2612.0.0' $familyName ('BiliCopilot.LocalRelease_2.2612.0.0_x64__' + $publisherId) 'Developer' $false 'X64' }; E = '不降级' },
    @{ N = 'same version different signature'; R = { $script:M.InstalledSig[$fullName] = [byte[]](7, 7); New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.1.0' $familyName $fullName 'Developer' $false 'X64' }; E = '签名与最终 MSIX 不同' },
    @{ N = 'older version signed by other certificate'; R = { $script:M.OldSigner = [byte[]](5, 5); New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.0.0' $familyName ('BiliCopilot.LocalRelease_2.2611.0.0_x64__' + $publisherId) 'Developer' $false 'X64' }; E = '签名者不是固定证书' })
foreach ($case in $conflicts) {
    $script:CaseData = $case
    Test-Blocked ('identity conflict: ' + $case.N) @{} $case.E $true { [void]$script:M.Packages.Add((& $script:CaseData.R)) }
}
Invoke-Case 'same version + identical signature reports AlreadyPresent (no trust/deploy, even with -Install)' {
    Reset-Mock; $dir = New-Fixture @{}
    [void]$script:M.Packages.Add((New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.1.0' $familyName $fullName 'Developer' $false 'X64'))
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'AlreadyPresent' -and $r.phases.AlreadyPresent.status -ceq 'Yes') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-NoChanges $r
    Assert-That ((Get-InstallerExitCode $r) -eq 0) 'exit code'
}

# ---------- 6. 依赖 ----------
$depDecl = @(@{ Name = 'Microsoft.Fixture.Framework'; Publisher = $depPublisher; MinVersion = '1.6.0.0' })
Invoke-Case 'missing dependency without pinned local package stops before UAC' {
    Reset-Mock; $dir = New-Fixture @{ Id = @{ Deps = $depDecl } }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '在请求 UAC 之前停止') $r.failure
    Assert-NoChanges $r
    $r.failure
}
Invoke-Case 'manifest dependency not declared by MSIX blocks' {
    Reset-Mock; $dir = New-Fixture @{ LocalDependency = $true }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '未声明的本地依赖') $r.failure
    Assert-NoChanges $r
}
Invoke-Case 'missing dependency with pinned local package: only that dependency is passed' {
    Reset-Mock; $dir = New-Fixture @{ Id = @{ Deps = $depDecl }; LocalDependency = $true }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Installed') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-That ($script:M.DeployCalls -eq 1 -and $script:M.DeployArgs.Dependencies.Count -eq 1 -and $script:M.DeployArgs.Dependencies[0] -like '*\payload\dep-Microsoft.Fixture.Framework.msix') 'dependency path'
    Assert-That (@($r.phases.DependencyChanges.added).Count -eq 1) 'dependency change recorded'
    'deps=' + $script:M.DeployArgs.Dependencies.Count
}
Invoke-Case 'dependency already satisfied: pinned local package is not passed' {
    Reset-Mock; $dir = New-Fixture @{ Id = @{ Deps = $depDecl }; LocalDependency = $true }
    [void]$script:M.Packages.Add((New-PackageRecord 'Microsoft.Fixture.Framework' $depPublisher '1.6.5.0' 'Microsoft.Fixture.Framework_8wekyb3d8bbwe' 'Microsoft.Fixture.Framework_1.6.5.0_x64__8wekyb3d8bbwe' 'System' $true 'X64'))
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Installed' -and $script:M.DeployArgs.Dependencies.Count -eq 0) ('outcome=' + $r.outcome + ' ' + $r.failure)
}

# ---------- 7. 权限与安装流程 ----------
Invoke-Case 'already elevated PowerShell is rejected before reading the delivery' {
    Reset-Mock; $script:M.Elevated = $true; $dir = New-Fixture @{}
    [IO.File]::Delete((Join-Path $dir 'release-artifacts.json'))
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '已提权') $r.failure
    Assert-NoChanges $r
    Assert-That ($null -eq $r.originalCallerSid) 'must stop before anything else'
    $r.failure
}
Invoke-Case 'install: trust needed -> one helper, parent recheck, original-user deploy, identity verified' {
    Reset-Mock; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Installed') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-That ($script:M.HelperCalls -eq 1 -and $script:M.DeployCalls -eq 1) 'calls'
    Assert-That ($r.phases.CertAdded.status -ceq 'Added' -and $r.phases.CertAdded.verifiedBySid -ceq $script:M.Sid) 'cert phase'
    Assert-That ($r.phases.Deployment.status -ceq 'Succeeded' -and $r.phases.PostInstallIdentity.status -ceq 'Verified' -and $r.phases.PostInstallIdentity.packageFullName -ceq $fullName) 'deploy/identity'
    Assert-That ($script:M.DeployArgs.Dependencies.Count -eq 0) 'no dependencies'
    Assert-That ((Get-InstallerExitCode $r) -eq 0) 'exit'
}
Invoke-Case 'install: exact certificate already trusted -> no UAC helper' {
    Reset-Mock; $script:M.Trusted = $true; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Installed' -and $script:M.HelperCalls -eq 0 -and $r.phases.CertAdded.status -ceq 'NotNeeded') ('outcome=' + $r.outcome + ' ' + $r.failure)
}
Invoke-Case 'install: older own version is upgraded (old version removal allowed)' {
    Reset-Mock; $script:M.Trusted = $true; $dir = New-Fixture @{}
    $script:M.OldSigner = $script:M.CerBytes
    [void]$script:M.Packages.Add((New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.0.0' $familyName ('BiliCopilot.LocalRelease_2.2611.0.0_x64__' + $publisherId) 'Developer' $false 'X64'))
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Installed' -and $r.phases.Preflight.existingPackage -ceq 'Upgrade') ('outcome=' + $r.outcome + ' ' + $r.failure)
}
Invoke-Case 'UAC cancelled -> no deployment, outcome UacCancelled' {
    Reset-Mock; $script:M.UacCancel = $true; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'UacCancelled' -and $r.phases.CertAdded.status -ceq 'UacCancelled' -and $script:M.DeployCalls -eq 0) ('outcome=' + $r.outcome + ' cert=' + $r.phases.CertAdded.status)
    Assert-That ((Get-InstallerExitCode $r) -eq 3) 'exit 3'
}
Invoke-Case 'helper exit 0 but parent store recheck fails -> no deployment' {
    Reset-Mock; $script:M.HelperAddsTrust = $false; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match '复查' -and $script:M.DeployCalls -eq 0 -and $r.phases.CertAdded.status -ceq 'NotAdded') ('outcome=' + $r.outcome + ' ' + $r.failure)
}
Invoke-Case 'caller SID changes after helper -> PartialFailure, no deployment' {
    Reset-Mock; $script:M.SidAfterHelper = 'S-1-5-21-9-9-9-500'; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'PartialFailure' -and $r.failure -match 'SID' -and $script:M.DeployCalls -eq 0 -and $r.phases.CertAdded.status -ceq 'AddedObservedAfterFailure') ('outcome=' + $r.outcome + ' ' + $r.failure)
}
Invoke-Case 'trust added then deployment fails -> PartialFailure with HRESULT and ActivityId, cert not revoked' {
    Reset-Mock; $dir = New-Fixture @{}
    $script:M.DeployThrow = 'Deployment failed with HRESULT: 0x80073CF3, Package failed updates, dependency or conflict validation. NOTE: For additional information, look for [ActivityId] 01234567-89ab-cdef-0123-456789abcdef in the Event Log'
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'PartialFailure' -and $r.phases.PartialFailure.status -ceq 'Yes') ('outcome=' + $r.outcome)
    Assert-That ($r.hresult -ceq '0x80073CF3' -and $r.activityId -ceq '01234567-89ab-cdef-0123-456789abcdef') ('hr=' + $r.hresult + ' act=' + $r.activityId)
    Assert-That ($r.phases.Deployment.status -ceq 'Failed' -and $r.phases.Deployment.hresult -ceq '0x80073CF3' -and $script:M.Trusted) 'deployment phase / trust kept'
    Assert-That ((Get-InstallerExitCode $r) -eq 2) 'exit 2'
    $r.hresult + ' ' + $r.activityId
}
Invoke-Case 'post-install identity mismatch -> PartialFailure' {
    Reset-Mock; $script:M.Trusted = $true; $script:M.DeployStatus = 'NeedsRemediation'; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'PartialFailure' -and $r.phases.PostInstallIdentity.status -ceq 'Failed') ('outcome=' + $r.outcome + ' ' + $r.failure)
}
Invoke-Case 'unexpected change to another package during deployment -> PartialFailure' {
    Reset-Mock; $script:M.Trusted = $true; $dir = New-Fixture @{}
    [void]$script:M.Packages.Add((New-PackageRecord 'Contoso.Other' 'CN=Contoso' '1.0.0.0' 'Contoso.Other_x' 'Contoso.Other_1.0.0.0_x64__x' 'Developer' $false 'X64'))
    $script:M.DeploySideEffect = { foreach ($p in @($script:M.Packages | Where-Object { $_.Name -ceq 'Contoso.Other' })) { $script:M.Packages.Remove($p) } }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'PartialFailure' -and $r.failure -match '受保护包') ('outcome=' + $r.outcome + ' ' + $r.failure)
}

# ---------- 8. 提权助手（同样只用桩函数替代存储写入） ----------
& {
    . $trustSource
    $script:AddCalls = New-Object System.Collections.ArrayList
    function Test-ProcessElevated { return [bool]$script:M.Elevated }
    function Get-CertificateFacts([byte[]]$Bytes) {
        $facts = @{ RawData = $Bytes; Thumbprint = $fixtureThumbprint; Subject = 'CN=BiliCopilot Local Release'
            NotBeforeUtc = [DateTime]::SpecifyKind([DateTime]'2026-01-01T00:00:00', 'Utc'); NotAfterUtc = [DateTime]::SpecifyKind([DateTime]'2027-12-31T00:00:00', 'Utc')
            Ekus = @('1.3.6.1.5.5.7.3.3'); DigitalSignature = $true; IsCertificateAuthority = $false; HasPrivateKey = $false }
        foreach ($key in $script:M.Cert.Keys) { $facts[$key] = $script:M.Cert[$key] }
        return [pscustomobject]$facts
    }
    function Get-NativeSignature([string]$Path) {
        if ($null -ne $script:M.Signature) { return $script:M.Signature }
        return [pscustomobject]@{ Status = 'UnknownError'; StatusMessage = $untrustedRootMessage; SignerRawData = $script:M.CerBytes }
    }
    function Test-MachineTrustedPeopleExact([byte[]]$CerBytes, [string]$Thumbprint) { return [bool]$script:M.Trusted }
    function Add-CertificateToMachineTrustedPeople([byte[]]$CerBytes) { [void]$script:AddCalls.Add($CerBytes); $script:M.Trusted = $true }
    function Invoke-HelperCase([string]$Name, [int]$Expected, [int]$ExpectedAdds, [scriptblock]$Arrange, [scriptblock]$Tamper) {
        Invoke-Case $Name {
            Reset-Mock; $script:M.Elevated = $true; $script:AddCalls.Clear()
            $dir = New-Fixture @{}
            $digest = Get-FileSha (Join-Path $dir 'release-artifacts.json')
            $helperDigest = Get-FileSha (Join-Path $dir 'Trust-Certificate.ps1')
            if ($Arrange) { & $Arrange }
            if ($Tamper) { $values = & $Tamper $digest $helperDigest; $digest = $values[0]; $helperDigest = $values[1] }
            $code = Invoke-TrustHelper -DeliveryRoot $dir -ManifestSha256 $digest -HelperSha256 $helperDigest -OriginalCallerSid 'S-1-5-21-1'
            Assert-That ($code -eq $Expected) ('exit=' + $code)
            Assert-That ($script:AddCalls.Count -eq $ExpectedAdds) ('adds=' + $script:AddCalls.Count)
            if ($ExpectedAdds -eq 1) { Assert-That (Test-BytesEqual $script:AddCalls[0] $script:M.CerBytes) 'imported bytes must be the validated CER bytes' }
            'exit=' + $code
        }
    }
    Invoke-HelperCase 'helper: valid fixture adds exactly the validated CER bytes once' 0 1 $null $null
    Invoke-HelperCase 'helper: exact cert already present -> exit 10, no write' 10 0 { $script:M.Trusted = $true } $null
    Invoke-HelperCase 'helper: not elevated -> refuses' 1 0 { $script:M.Elevated = $false } $null
    Invoke-HelperCase 'helper: parent-pinned manifest digest mismatch -> refuses' 1 0 $null { param($m, $h) @(('ab' * 32), $h) }
    Invoke-HelperCase 'helper: executed helper digest differs from manifest trustSha256 -> refuses' 1 0 $null { param($m, $h) @($m, ('cd' * 32)) }
    Invoke-HelperCase 'helper: signer DER mismatch -> refuses' 1 0 { $script:M.Signature = [pscustomobject]@{ Status = 'Valid'; StatusMessage = 'ok'; SignerRawData = [byte[]](1) } } $null
    Invoke-HelperCase 'helper: NotSigned -> refuses' 1 0 { $script:M.Signature = [pscustomobject]@{ Status = 'NotSigned'; StatusMessage = 'x'; SignerRawData = $null } } $null
    Invoke-HelperCase 'helper: certificate CA=true -> refuses' 1 0 { $script:M.Cert = @{ IsCertificateAuthority = $true } } $null
}

# ---------- 9. 真实 powershell.exe：固定引导程序（无 RunAs、桩助手） ----------
Invoke-Case 'real powershell.exe bootstrap executes the same verified buffer; data-only parameters' {
    $dir = Join-Path $WorkRoot "boot q'uote `$(x) ;&"
    [void][IO.Directory]::CreateDirectory($dir)
    $stub = 'param($DeliveryRoot, $ManifestSha256, $HelperSha256, $OriginalCallerSid) [IO.File]::WriteAllText([IO.Path]::Combine($DeliveryRoot, "stub-ran.txt"), ($ManifestSha256 + "|" + $HelperSha256 + "|" + $OriginalCallerSid)); exit 7'
    [IO.File]::WriteAllText((Join-Path $dir 'Trust-Certificate.ps1'), $stub, (New-Object Text.UTF8Encoding($false)))
    $helperDigest = Get-FileSha (Join-Path $dir 'Trust-Certificate.ps1')
    $manifestDigest = 'ef' * 32
    $ps = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $command = Get-TrustBootstrapCommand $dir $manifestDigest $helperDigest "S-1-5-21-1'; whoami #"
    $p = Start-Process -FilePath $ps -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $command) -Wait -PassThru -WindowStyle Hidden
    Assert-That ($p.ExitCode -eq 7) ('exit=' + $p.ExitCode)
    $ran = [IO.File]::ReadAllText((Join-Path $dir 'stub-ran.txt'))
    Assert-That ($ran -ceq ($manifestDigest + '|' + $helperDigest + "|S-1-5-21-1'; whoami #")) $ran
    [IO.File]::Delete((Join-Path $dir 'stub-ran.txt'))
    $bad = Get-TrustBootstrapCommand $dir $manifestDigest ('00' * 32) 'S-1'
    $q = Start-Process -FilePath $ps -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $bad) -Wait -PassThru -WindowStyle Hidden
    Assert-That ($q.ExitCode -eq 21 -and -not [IO.File]::Exists((Join-Path $dir 'stub-ran.txt'))) ('tampered exit=' + $q.ExitCode)
    'exit=7 then tampered exit=21'
}

# ---------- 10. 真实入口（默认只预检） ----------
Invoke-Case 'real entrypoint default mode on shipped template: exit 1, BLOCKED, no changes' {
    Reset-Mock
    $dir = New-Fixture @{ RawManifest = [IO.File]::ReadAllText($templateSource) }
    $ps = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $out = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'Install.ps1') 2>&1 | Out-String
    $code = $LASTEXITCODE
    Assert-That ($code -eq 1 -and $out -match '"outcome":\s+"Blocked"' -and $out -match '__PENDING|占位') ('exit=' + $code + ' out=' + $out)
    'exit=' + $code
}
Invoke-Case 'real entrypoint default mode on inert fixture: real CER parser blocks, exit 1' {
    Reset-Mock
    $dir = New-Fixture @{}
    $ps = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $out = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'Install.ps1') 2>&1 | Out-String
    $code = $LASTEXITCODE
    Assert-That ($code -eq 1 -and $out -match '"outcome":\s+"Blocked"' -and $out -match 'DER') ('exit=' + $code + ' out=' + $out)
    'exit=' + $code
}
Invoke-Case 'real host facts (read-only): current session is not elevated' {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Assert-That (-not $elevated) 'test host is elevated; elevated-path evidence would be invalid'
    'elevated=' + $elevated + ' ps=' + $PSVersionTable.PSVersion
}

Write-Host ('TOTAL_PASS={0} TOTAL_FAIL={1}' -f ($script:Results.Count - $script:Failures), $script:Failures)
foreach ($f in @($installSource, $trustSource, $templateSource, (Join-Path $SourceRoot 'release-artifacts.schema.json'), $selfPath)) { Write-Host ('SHA256 {0} {1}' -f (Get-FileSha $f), [IO.Path]::GetFileName($f)) }
if ($script:Failures -gt 0) { exit 1 }
exit 0
