param([string]$Dotnet = 'dotnet', [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\preview-win-x64'), [string]$OfficialHistoryFile = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$checks = @('PolicyChecks','SelfChecks','FeatureChecks','PlatformChecks','NetworkChecks','WindowsChecks','KeepAliveChecks')
foreach ($check in $checks) {
    $arguments = @('run','--project',(Join-Path $root "checks\$check\$check.csproj"),'-c','Release','--')
    if ($check -in @('WindowsChecks','KeepAliveChecks')) { $arguments += @('--package',([IO.Path]::GetFullPath($PackageDirectory))) }
    & $Dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw ($check + ' failed.') }
}
& $Dotnet run --project (Join-Path $root 'checks\KeepAliveChecks\KeepAliveChecks.csproj') -c Release -- --observation-settings-only
if ($LASTEXITCODE -ne 0) { throw 'Foreground preference checks failed.' }
if ($OfficialHistoryFile) {
    & $Dotnet run --project (Join-Path $root 'checks\HistoryExportChecks\HistoryExportChecks.csproj') -c Release -- --verify-file $OfficialHistoryFile
    if ($LASTEXITCODE -ne 0) { throw 'Official history checks failed.' }
} else { Write-Output 'SKIP HistoryExportChecks: provide -OfficialHistoryFile with an official XLS (never include personal rows in logs).' }
