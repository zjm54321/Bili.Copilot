[CmdletBinding()]
param(
    [string]$MSBuildPath,
    [string]$WindowsSdkBin,
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/release'
$publish = Join-Path $out 'publish'
$layout = Join-Path $out 'layout'
$build = (Join-Path $out 'build').Replace('\', '/') + '/'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.nuget-clean' }
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuildPath -or -not (Test-Path $MSBuildPath)) { throw 'Visual Studio MSBuild is required; pass -MSBuildPath.' }
if (-not $WindowsSdkBin) { $WindowsSdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64' }
if (-not (Test-Path (Join-Path $WindowsSdkBin 'makeappx.exe'))) { throw 'Windows SDK 10.0.26100.0 x64 tools required; pass -WindowsSdkBin.' }
$native = Get-Content (Join-Path $PSScriptRoot 'native-assets.json') -Raw | ConvertFrom-Json
foreach ($file in $native.files.PSObject.Properties) {
    $path = Join-Path $root ('src/Desktop/BiliCopilot.UI/Assets/' + $file.Name)
    if (-not (Test-Path $path)) {
        throw "Missing native asset: $($file.Name) ($($file.Value)). Follow scripts/README.md to supply it manually."
    }
}
if (Test-Path $layout) { throw "Existing release layout: $layout. Remove artifacts/release before rebuilding." }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:VSLANG = '1033'
$common = @(
    (Join-Path $root 'src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj'),
    '/nologo', '/nr:false', '/p:Configuration=Release', '/p:Platform=x64', '/p:RuntimeIdentifier=win-x64',
    ('/p:SolutionDir=' + $root.Replace('\', '/') + '/src/'), ('/p:BuildOutDir=' + $build),
    ('/p:CustomBeforeDirectoryBuildProps=' + (Join-Path $PSScriptRoot 'build-config/release.props')),
    ('/p:CustomAfterMicrosoftCommonTargets=' + (Join-Path $PSScriptRoot 'build-config/runtime-pin.targets')),
    ('/p:PublishDir=' + $publish + '\'), ('/p:AppxPackageDir=' + $out + '\package\'),
    '/p:GenerateAppxPackageOnBuild=false', '/p:AppxPackageSigningEnabled=false', '/p:UseSharedCompilation=false',
    '/p:PublishAot=false', '/p:PublishTrimmed=false', '/p:PublishReadyToRun=true', '/p:PublishReadyToRunShowWarnings=true',
    '/p:SelfContained=true', '/p:Optimize=true',
    '/p:RunAnalyzers=true', '/p:RunAnalyzersDuringBuild=true', '/p:EnableNETAnalyzers=true', '/p:TreatWarningsAsErrors=true',
    '/p:AnalysisLevel=latest', '/p:AnalysisMode=AllEnabledByDefault',
    ('/p:RestoreConfigFile=' + (Join-Path $root 'nuget.config')), ('/p:RestorePackagesPath=' + $env:NUGET_PACKAGES),
    '/p:RestoreFallbackFolders=', '/p:RestoreAdditionalProjectFallbackFolders=', '/p:RestoreAdditionalProjectSources=',
    '/p:RestoreIgnoreFailedSources=false', '/p:NuGetInteractive=false'
)
function Invoke-BuildStep([string]$Name, [string]$Exe, [string[]]$Arguments) {
    @{ executable = $Exe; arguments = $Arguments } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "$Name.command.json") -Encoding UTF8
    & $Exe @Arguments 1> (Join-Path $out "$Name.stdout.log") 2> (Join-Path $out "$Name.stderr.log")
    if ($LASTEXITCODE) { throw "$Name failed ($LASTEXITCODE). See $out/$Name.stdout.log" }
    $log = Get-Content (Join-Path $out "$Name.stdout.log") -Raw
    if ($log -notmatch '0 Warning\(s\)\s+0 Error\(s\)') { throw "$Name did not report zero warnings and errors." }
}
Push-Location $root
try {
    Invoke-BuildStep 'restore' 'dotnet' (@('msbuild') + $common + @('/t:Restore', '/v:normal'))
    Invoke-BuildStep 'publish' $MSBuildPath ($common + @('/t:Publish', '/p:PublishProfile=win-x64.pubxml', '/v:normal', ('/bl:' + $out + '/publish.binlog')))
    New-Item -ItemType Directory -Path $layout | Out-Null
    [xml]$recipe = Get-Content (Join-Path $build 'BiliCopilot.UI/bin/BiliCopilot.UI.build.appxrecipe') -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($recipe.NameTable)
    $ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
    $files = foreach ($item in $recipe.SelectNodes('//m:AppxPackagedFile|//m:AppXManifest', $ns)) {
        $relative = [string]$item.PackagePath
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..') { throw 'Invalid recipe path.' }
        $from = [string]$item.Include
        $published = Join-Path $publish $relative
        if (Test-Path -LiteralPath $published -PathType Leaf) { $from = $published }
        $to = Join-Path $layout $relative
        New-Item -ItemType Directory -Path (Split-Path $to -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $from -Destination $to
        [pscustomobject]@{ path = $relative.Replace('\', '/'); sha256 = (Get-FileHash $to -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [xml]$manifest = Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
    $identity = $manifest.Package.Identity
    [xml]$sourceManifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw
    if ($identity.Name -cne $sourceManifest.Package.Identity.Name -or $identity.Publisher -cne $sourceManifest.Package.Identity.Publisher -or
        $identity.Version -cne $sourceManifest.Package.Identity.Version -or $identity.ProcessorArchitecture -cne 'x64') { throw 'Package identity differs from source.' }
    $ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace('c', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $com = $manifest.SelectSingleNode('//c:ExeServer', $ns)
    if (-not (Test-Path (Join-Path $layout ([string]$com.Executable)))) { throw 'COM executable is missing from layout.' }
    foreach ($name in @('libmpv-2.dll', 'ffmpeg.exe', 'BiliCopilot.Visor.exe')) {
        $packed = @(Get-ChildItem $layout -Recurse -File -Filter $name)
        if ($packed.Count -ne 1) { throw "Required x64 asset missing from layout: $name" }
    }
    $msix = Join-Path $out ("BiliCopilot_$($identity.Version)_x64.msix")
    & (Join-Path $WindowsSdkBin 'makeappx.exe') pack /d $layout /p $msix /h SHA256 1> (Join-Path $out 'makeappx.log') 2>&1
    if ($LASTEXITCODE) { throw "MakeAppx failed ($LASTEXITCODE)." }
    if ($CertificateThumbprint) {
        $cert = Get-Item "Cert:/CurrentUser/My/$CertificateThumbprint"
        if (-not $cert.HasPrivateKey -or $cert.Subject -cne $identity.Publisher) { throw 'Signing certificate must have a private key and match the manifest Publisher.' }
        & (Join-Path $WindowsSdkBin 'signtool.exe') sign /fd SHA256 /s My /sha1 $CertificateThumbprint $msix 1> (Join-Path $out 'sign.log') 2>&1
        if ($LASTEXITCODE) { throw "SignTool failed ($LASTEXITCODE)." }
    }
    $files | ConvertTo-Json | Set-Content (Join-Path $out 'layout-files.json') -Encoding UTF8
    [ordered]@{ msix = $msix; sha256 = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant(); warnings = 0; errors = 0; signed = [bool]$CertificateThumbprint } |
        ConvertTo-Json | Set-Content (Join-Path $out 'result.json') -Encoding UTF8
    Write-Host "Release MSIX: $msix (0 warnings, 0 errors)"
} finally { Pop-Location }
