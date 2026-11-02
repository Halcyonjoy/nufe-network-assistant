# Shared allowlist for source export and the pre-publication check.
function Get-PublicSourceFiles([string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $rootNames = @('.editorconfig','.gitattributes','.gitignore','AGENTS.md','README.md','LICENSE',
        'CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.md','build.ps1','Directory.Build.props','global.json','NufeCampus.slnx')
    foreach ($name in $rootNames) {
        $path = Join-Path $Root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path }
    }
    foreach ($directory in @('src','checks','assets','tools','installer','.github','licenses','docs')) {
        $path = Join-Path $Root $directory
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Force) {
            $relative = $file.FullName.Substring($Root.Length + 1).Replace('\','/')
            if ($relative -match '(^|/)(bin|obj|local-history|campus-validation-evidence)/') { continue }
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source links are not supported: $relative" }
            if ($relative -match '^(src|checks)/.+\.(cs|csproj|xaml)$' -or
                $relative -match '^assets/[^/]+\.(svg|png|ico)$' -or
                $relative -match '^tools/[^/]+\.ps1$' -or
                $relative -match '^installer/[^/]+\.(ps1|iss|txt)$' -or
                $relative -match '^\.github/.+\.(yml|yaml|md)$' -or
                $relative -match '^licenses/[^/]+\.txt$' -or
                $relative -match '^docs/(README|architecture|development|release)\.md$') { $file }
        }
    }
}
