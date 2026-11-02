param([string]$Compiler = '', [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\preview-win-x64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup[0].Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part project version.' }
if (-not $Compiler) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'NufeCampus.BuildTools\InnoSetup-7.1.1\compiler\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe')
    )
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 7, or provide -Compiler with the ISCC.exe path.' }
$package = [IO.Path]::GetFullPath($PackageDirectory)
foreach ($name in @('NufeCampus.App.exe','NufeCampus.Agent.exe','NufeCampus.Cli.exe','NufeCampus.Scheduled.exe','NufeCampus.SelfWorker.exe','NufeCampus.SetupSupport.exe','app.ico','LICENSE','THIRD-PARTY-NOTICES.md','licenses/AngleSharp.txt','licenses/ExcelDataReader.txt','licenses/DotNet-LICENSE.TXT','licenses/DotNet-THIRD-PARTY-NOTICES.TXT','licenses/DotNet-WindowsDesktop-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name))) { throw ('Missing package file: ' + $name) }
}
$output = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $Compiler ('/DPackageDir=' + $package) ('/DOutputDir=' + $output) ('/DAppVersion=' + $version) (Join-Path $PSScriptRoot 'NUFE-Campus.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output ("NUFE-Campus-Setup-$version-x64.exe")
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($setup + '.sha256'), $hash + '  ' + [IO.Path]::GetFileName($setup) + "`n")
Write-Output ('Installer ready: ' + $setup)
