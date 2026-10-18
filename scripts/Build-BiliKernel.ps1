[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'src/Libs/BiliKernel'
if (-not (Test-Path (Join-Path $source '.git'))) { throw 'Initialize the pinned SDK: git submodule update --init --recursive' }
$feed = Join-Path $root 'artifacts/bilikernel-feed'
$logs = Join-Path $root 'artifacts/bilikernel-logs'
New-Item -ItemType Directory -Force -Path $feed, $logs | Out-Null
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.nuget-clean' }
$projects = @('BiliKernel.Abstractions/BiliKernel.Abstractions.csproj',
    'Services/Services.Moment/Services.Moment.csproj',
    'Services/Services.Comment/Services.Comment.csproj',
    'Services/Services.Media/Services.Media.csproj')
Push-Location $root
try {
    foreach ($relative in $projects) {
        $project = Join-Path $source ('src/' + $relative)
        $name = [IO.Path]::GetFileNameWithoutExtension($project)
        $common = @('-p:UseSharedCompilation=false', '-nr:false', '-p:IncludeSourceRevisionInInformationalVersion=false')
        & dotnet restore $project --configfile (Join-Path $root 'nuget.config') --packages $env:NUGET_PACKAGES @common 2>&1 | Tee-Object (Join-Path $logs "$name.restore.log")
        if ($LASTEXITCODE) { throw "SDK restore failed: $name ($LASTEXITCODE)" }
        & dotnet build $project --no-restore -c Release @common 2>&1 | Tee-Object (Join-Path $logs "$name.build.log")
        if ($LASTEXITCODE) { throw "SDK build failed: $name ($LASTEXITCODE)" }
        & dotnet pack $project --no-build --no-restore -c Release -o $feed @common 2>&1 | Tee-Object (Join-Path $logs "$name.pack.log")
        if ($LASTEXITCODE) { throw "SDK pack failed: $name ($LASTEXITCODE)" }
    }
} finally { Pop-Location }
