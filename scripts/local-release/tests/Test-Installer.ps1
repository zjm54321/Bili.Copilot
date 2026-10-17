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
$selfPath = $MyInvocation.MyCommand.Path

# 语法与共享校验一致性
foreach ($file in @($installSource, $trustSource, $selfPath)) {
    $tokens = $null; $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    Add-Result ('PS5.1 parser ' + [IO.Path]::GetFileName($file)) ($errors.Count -eq 0) (($errors | ForEach-Object { $_.Message }) -join '; ')
}
Invoke-Case 'shared validation region byte-identical in both scripts' {
    $pattern = '(?s)#region SharedValidation\r?\n.*?#endregion SharedValidation'
    $a = [regex]::Match([IO.File]::ReadAllText($installSource), $pattern).Value
    $b = [regex]::Match([IO.File]::ReadAllText($trustSource), $pattern).Value
    Assert-That ($a.Length -gt 1000 -and $a -ceq $b) 'region differs'
    'length=' + $a.Length
}
. $installSource
$untrustedRootMessage = (New-Object System.ComponentModel.Win32Exception ([int]-2146762487)).Message
$publisherId = Get-PublisherId 'CN=BiliCopilot Local Release'
$fullName = 'BiliCopilot.LocalRelease_2.2611.1.0_x64__' + $publisherId
$familyName = 'BiliCopilot.LocalRelease_' + $publisherId
$fixtureThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
$msixSignature = [Text.Encoding]::ASCII.GetBytes('PKCX-INERT-FIXTURE-NOT-A-SIGNATURE')
function Reset-Mock {
    $script:M = @{
        Elevated = $false; Sid = 'S-1-5-21-1000-1000-1000-1001'
        Packages = New-Object System.Collections.ArrayList; OldSigner = $null
        Trusted = $false; UacCancel = $false; HelperCalls = 0
        DeployCalls = 0; DeployArgs = $null; DeployThrow = $null
        Os = [pscustomobject]@{ Version = [version]'10.0.22631.0'; Architecture = 'AMD64'; Is64BitProcess = $true; PSEdition = 'Desktop'; PSVersion = [version]'5.1.22621.4391' }
        Policy = $null; CerBytes = $null
    }
}
function Test-ProcessElevated { return [bool]$script:M.Elevated }
function Get-CallerSid { return $script:M.Sid }
function Get-OsFacts { return $script:M.Os }
function Get-SideloadPolicy { return $script:M.Policy }
function Get-CertificateFacts([byte[]]$Bytes) {
    $facts = @{ RawData = $Bytes; Thumbprint = $fixtureThumbprint; Subject = 'CN=BiliCopilot Local Release'
        NotBeforeUtc = [DateTime]::SpecifyKind([DateTime]'2026-01-01T00:00:00', 'Utc'); NotAfterUtc = [DateTime]::SpecifyKind([DateTime]'2027-12-31T00:00:00', 'Utc')
        Ekus = @('1.3.6.1.5.5.7.3.3'); DigitalSignature = $true; IsCertificateAuthority = $false; HasPrivateKey = $false }
    return [pscustomobject]$facts
}
function Get-NativeSignature([string]$Path) {
    return [pscustomobject]@{ Status = 'UnknownError'; StatusMessage = $untrustedRootMessage; SignerRawData = $script:M.CerBytes }
}
function Get-UserPackagesByName([string]$Name) { return @($script:M.Packages | Where-Object { $_.Name -ceq $Name }) }
function Read-InstalledSignatureBytes($Record) {
    return , $msixSignature
}
function Get-P7xSignerRawData([byte[]]$Bytes) { return , $script:M.OldSigner }
function Get-PackageApplicationIds([string]$PackageFullName) { return @('App') }
function Test-MachineTrustedPeopleExact([byte[]]$CerBytes, [string]$Thumbprint) { return [bool]$script:M.Trusted }
function Invoke-ElevatedTrustHelper([string]$Root, [string]$ManifestSha256, [string]$HelperSha256, [string]$CallerSid) {
    $script:M.HelperCalls++
    if ($script:M.UacCancel) { throw (New-Object System.OperationCanceledException 'UAC cancelled (fixture)') }
    $script:M.Trusted = $true
    return 0
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
    [void]$script:M.Packages.Add($record)
}
# 核心流程共用的惰性夹具
function Get-AppxXml([hashtable]$Id) {
    $apps = '<Applications><Application Id="{0}" Executable="Inert.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>' -f $Id.AppId
    return ('<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="{0}" Publisher="{1}" Version="{2}" ProcessorArchitecture="{3}" /><Properties><DisplayName>INERT</DisplayName><PublisherDisplayName>INERT</PublisherDisplayName><Logo>a.png</Logo></Properties><Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="{4}" MaxVersionTested="10.0.26100.0" /></Dependencies>{5}</Package>' -f
        $Id.Name, [Security.SecurityElement]::Escape($Id.Publisher), $Id.Version, $Id.Arch, $Id.MinOs, $apps)
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
    $script:FixtureIndex++
    $dir = Join-Path $WorkRoot ('fx{0:D3}' -f $script:FixtureIndex)
    [void][IO.Directory]::CreateDirectory((Join-Path $dir 'payload'))
    [IO.File]::Copy($installSource, (Join-Path $dir 'Install.ps1'))
    [IO.File]::Copy($trustSource, (Join-Path $dir 'Trust-Certificate.ps1'))
    $id = @{ Name = 'BiliCopilot.LocalRelease'; Publisher = 'CN=BiliCopilot Local Release'; Version = '2.2611.1.0'; Arch = 'x64'; AppId = 'App'; MinOs = '10.0.19041.0' }
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
    $json = ConvertTo-Json -InputObject $manifest -Depth 6
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
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
Invoke-Case 'valid inert fixture default precheck passes without any writes' {
    Reset-Mock; $dir = New-Fixture @{}
    $r = Invoke-Fixture $dir $false ''
    Assert-That ($r.outcome -ceq 'PreflightPassed') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-NoChanges $r
    Assert-That ($r.phases.Preflight.signerStatusKind -ceq 'UntrustedRootOnly' -and -not $r.phases.Preflight.externalManifestDigestVerified) 'summary'
    'existing=' + $r.phases.Preflight.existingPackage
}
Invoke-Case 'MSIX digest mismatch blocks before trust/deploy' {
    Reset-Mock
    $dir = New-Fixture @{ After = { param($d) $p = Join-Path $d 'payload\BiliCopilot.LocalRelease_2.2611.1.0_x64.msix'; $b = [IO.File]::ReadAllBytes($p); $b[$b.Length - 1] = $b[$b.Length - 1] -bxor 1; [IO.File]::WriteAllBytes($p, $b) } }
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'Blocked' -and $r.failure -match 'MSIX SHA-256 不匹配') $r.failure
    Assert-NoChanges $r
}
Invoke-Case 'same version + identical signature reports AlreadyPresent (no trust/deploy, even with -Install)' {
    Reset-Mock; $dir = New-Fixture @{}
    [void]$script:M.Packages.Add((New-PackageRecord 'BiliCopilot.LocalRelease' 'CN=BiliCopilot Local Release' '2.2611.1.0' $familyName $fullName 'Developer' $false 'X64'))
    $r = Invoke-Fixture $dir $true ''
    Assert-That ($r.outcome -ceq 'AlreadyPresent' -and $r.phases.AlreadyPresent.status -ceq 'Yes') ('outcome=' + $r.outcome + ' ' + $r.failure)
    Assert-NoChanges $r
    Assert-That ((Get-InstallerExitCode $r) -eq 0) 'exit code'
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
Write-Host ('TOTAL_PASS={0} TOTAL_FAIL={1}' -f ($script:Results.Count - $script:Failures), $script:Failures)
if ($script:Failures -gt 0) { exit 1 }
exit 0
