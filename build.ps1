param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\preview-win-x64'), [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$sdkVersion = & $Dotnet --version
if ($LASTEXITCODE -ne 0 -or [version]($sdkVersion.Split('-')[0]) -lt [version]'10.0.403') { throw 'Use .NET SDK 10.0.403 or newer for maintained runtime packaging; pass -Dotnet with its executable path.' }
$packagePath = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($running in (Get-Process -Name 'NufeCampus*' -ErrorAction SilentlyContinue)) {
    if ($running.Path -and [string]::Equals([IO.Path]::GetDirectoryName($running.Path),$packagePath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Output directory contains a running campus process. Choose a separate preview directory; do not replace live binaries.' }
}
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
& $Dotnet publish (Join-Path $PSScriptRoot 'src\NufeCampus.Agent\NufeCampus.Agent.csproj') -c Release -r win-x64 -o $packagePath --nologo
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT agent publish failed.' }
foreach ($component in @('Cli','Scheduled')) {
    & $Dotnet publish (Join-Path $PSScriptRoot "src\NufeCampus.$component\NufeCampus.$component.csproj") -c Release -r win-x64 -o $packagePath --nologo
    if ($LASTEXITCODE -ne 0) { throw "$component publish failed." }
}
& $Dotnet publish (Join-Path $PSScriptRoot 'src\NufeCampus.App\NufeCampus.App.csproj') -c Release -r win-x64 --self-contained true -o $packagePath --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF app publish failed.' }
& $Dotnet publish (Join-Path $PSScriptRoot 'src\NufeCampus.SelfWorker\NufeCampus.SelfWorker.csproj') -c Release -r win-x64 --self-contained true -o $packagePath --nologo
if ($LASTEXITCODE -ne 0) { throw 'On-demand worker publish failed.' }
& $Dotnet publish (Join-Path $PSScriptRoot 'src\NufeCampus.SetupSupport\NufeCampus.SetupSupport.csproj') -c Release -r win-x64 --self-contained true -o $packagePath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer support publish failed.' }
foreach ($stale in @('NufeCampus.SelfWorker.dll','NufeCampus.SelfWorker.deps.json','NufeCampus.SelfWorker.runtimeconfig.json')) {
    $stalePath = Join-Path $packagePath $stale
    if (Test-Path -LiteralPath $stalePath) { Remove-Item -LiteralPath $stalePath -Force }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $packagePath 'README.md') -Force
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $packagePath $name) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $packagePath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\app.ico') -Destination (Join-Path $packagePath 'app.ico') -Force
Write-Output ('Package ready: ' + $packagePath)
