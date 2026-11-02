param([string]$SourceArchive = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'PublicSource.ps1')
$files = @(Get-PublicSourceFiles $root | Sort-Object FullName)
$names = @($files | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\','/') })
$problems = [Collections.Generic.List[string]]::new()
foreach ($required in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md','AGENTS.md','NufeCampus.slnx','Directory.Build.props','global.json')) {
    if ($required -notin $names) { $problems.Add('Required public file missing: ' + $required) }
}
foreach ($file in $files) {
    if ($file.Extension -in @('.png','.ico')) { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    # Report file names only; never echo a detected secret.
    if ($text -match '(?i)[A-Z]:[\\/]+Users[\\/]+[^\s\\/]+' -or
        $text -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
        $text -match '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|AKIA[0-9A-Z]{16})') {
        $problems.Add('Review private path or credential pattern in ' + $file.FullName.Substring($root.Length + 1))
    }
    if ($file.Extension -eq '.md') {
        foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Split('#')[0]
            if (-not $target -or $target -match '^[a-z]+:') { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $target))
            if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                $resolved.Substring($root.Length + 1).Replace('\','/') -notin $names) {
                $problems.Add('Markdown points outside public source: ' + $file.FullName.Substring($root.Length + 1))
            }
        }
    }
}
# Use Git itself to verify .gitignore, without initializing or staging the real project.
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('NufeCampus-public-check-' + [guid]::NewGuid().ToString('N'))))
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
if ([IO.Path]::GetDirectoryName($temporary) -ne $tempParent -or [IO.Path]::GetFileName($temporary) -notmatch '^NufeCampus-public-check-[0-9a-f]{32}$') { throw 'Unsafe temporary check path' }
try {
    & git init --bare --quiet $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Git is required for the publication check.' }
    $candidates = @(& git -c core.excludesFile=NUL --git-dir=$temporary --work-tree=$root ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git candidate files.' }
    foreach ($candidate in $candidates) { if ($candidate -notin $names) { $problems.Add('Git would include an unreviewed file: ' + $candidate) } }
    foreach ($name in $names) { if ($name -notin $candidates) { $problems.Add('Public file is unexpectedly ignored: ' + $name) } }
    $realGit = Join-Path $root '.git'
    if (Test-Path -LiteralPath $realGit) {
        $tracked = @(& git -C $root ls-files)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect tracked files.' }
        foreach ($name in $tracked) { if ($name -notin $names) { $problems.Add('Non-public file is already tracked: ' + $name) } }
    }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
if ($SourceArchive) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($SourceArchive))
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        if (@(Compare-Object $names $entries).Count) { $problems.Add('Source ZIP does not match the public source allowlist.') }
        foreach ($entry in $archive.Entries) {
            $source = Join-Path $root $entry.FullName
            if ($entry.FullName -notin $names) { continue }
            $inputStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($source)
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $actual = [Convert]::ToBase64String($sha.ComputeHash($inputStream))
                $expected = [Convert]::ToBase64String($sha.ComputeHash($sourceStream))
                if ($actual -ne $expected) { $problems.Add('Source ZIP content mismatch: ' + $entry.FullName) }
            } finally { $sha.Dispose(); $sourceStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose() }
}
if ($problems.Count) { throw ($problems -join "`n") }
Write-Output ("PASS public source: {0} files; Git ignore rules, private-pattern scan and requested ZIP comparison." -f $names.Count)
Write-Output 'Automated checks cover listed patterns only; review changes and any existing Git history before pushing.'
