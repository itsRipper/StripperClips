param(
    [string]$SptRoot = $env:SPT_ROOT,
    [switch]$SkipBuild
)
# Require a clean tracked tree so the checksum file can identify the source commit.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = ''
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    Push-Location $root
    try {
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $dirty = @(git status --porcelain --untracked-files=no 2>$null | Where-Object { $_ })
        $commit = (git rev-parse HEAD 2>$null | Out-String).Trim()
        $ErrorActionPreference = $previous
    }
    finally { Pop-Location }
    if ($dirty.Count -ne 0) { throw "Uncommitted changes:`n$($dirty -join "`n")" }
}
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -SptRoot $SptRoot -IncludeFika }

$version = ([xml](Get-Content (Join-Path $root 'client\StripperClips.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (!$version) { throw 'Version not found in client\StripperClips.csproj.' }
$docs = if (Test-Path -LiteralPath (Join-Path $root 'release\CHANGELOG.md')) { Join-Path $root 'release' } else { $root }
$declared = [ordered]@{
    'server\StripperClips.Server.csproj' = "<Version>$version</Version>"
    'fika\StripperClips.Fika.csproj'    = "<Version>$version</Version>"
    'client\Plugin.cs'                   = "`"StripperClips`", `"$version`")]"
    'server\ModMetadata.cs'              = "new(`"$version`")"
}
foreach ($entry in $declared.GetEnumerator()) {
    if (!(Get-Content (Join-Path $root $entry.Key) -Raw).Contains($entry.Value)) { throw "$($entry.Key) does not declare version $version." }
}
foreach ($doc in 'README.md', 'CHANGELOG.md') {
    if (!(Get-Content (Join-Path $docs $doc) -Raw).Contains($version)) { throw "$doc does not mention version $version." }
}

$name = "StripperClips-$version"
$dist = Join-Path $root 'dist'
$zip = Join-Path $dist "$name.zip"

$plugin = 'BepInEx/plugins/StripperClips'
$mod = 'SPT_Runtime/user/mods/StripperClips'
$layout = [ordered]@{
    "$plugin/StripperClips.dll"         = 'client\bin\Release\StripperClips.dll'
    "$plugin/StripperClips.Fika.dll"    = 'fika\bin\Release\StripperClips.Fika.dll'
    "$mod/StripperClips.Server.dll"     = 'server\bin\Release\StripperClips.Server.dll'
    "$mod/bundles.json"                 = 'server\bundles.json'
}
foreach ($bundle in Get-ChildItem (Join-Path $root 'items') -Filter '*.bundle' | Sort-Object Name) {
    $layout["$mod/bundles/stripperclips/$($bundle.Name)"] = "items\$($bundle.Name)"
}
$families = 'mosin', 'mosin_sniper', 'sks', 'svt'
foreach ($family in $families) {
    foreach ($suffix in '', '_open') {
        $clip = "${family}_stripper_reload$suffix"
        $layout["$plugin/clips/$clip.bundle"] = "clips\$clip.bundle"
        $layout["$plugin/clips/$clip.clip.json"] = "clips\$clip.clip.json"
    }
}
$clip = 'stanag_magazine_load'
$layout["$plugin/clips/$clip.bundle"] = "clips\$clip.bundle"
$layout["$plugin/clips/$clip.clip.json"] = "clips\$clip.clip.json"
# Keep the source build guide out of the installation README and carry the MIT text in the notices.
$packageDocs = Join-Path $root 'artifacts\package-docs'
New-Item -ItemType Directory -Force $packageDocs | Out-Null
$readme = [IO.File]::ReadAllText((Join-Path $docs 'README.md'))
$readme = $readme -creplace '(?ms)^## Building\r?\n.*?(?=^## |\z)', ''
$notices = [IO.File]::ReadAllText((Join-Path $docs 'THIRD-PARTY-NOTICES.md'))
$license = [IO.File]::ReadAllText((Join-Path $docs 'LICENSE'))
[IO.File]::WriteAllText((Join-Path $packageDocs 'README.md'), $readme)
[IO.File]::WriteAllText((Join-Path $packageDocs 'THIRD-PARTY-NOTICES.md'), ($notices.TrimEnd() + "`r`n`r`n## StripperClips licence`r`n`r`n" + $license))
foreach ($doc in 'README.md', 'THIRD-PARTY-NOTICES.md') { $layout["$plugin/$doc"] = "artifacts\package-docs\$doc" }
foreach ($source in $layout.Values) {
    $file = Join-Path $root $source
    if (!(Test-Path -LiteralPath $file)) { throw "Missing $source; run scripts\Build.ps1 -IncludeFika first." }
    if ($source -like '*.dll' -and [Reflection.AssemblyName]::GetAssemblyName($file).Version -ne [version]"$version.0") {
        throw "Unexpected assembly version in $source; rebuild for $version."
    }
}
foreach ($entry in $layout.GetEnumerator() | Where-Object { $_.Key -like "$mod/*" -and $_.Key -notlike '*.dll' }) {
    $built = Join-Path $root ('server\bin\Release\' + $entry.Key.Substring($mod.Length + 1).Replace('/', '\'))
    if ((Get-FileHash $built).Hash -ne (Get-FileHash (Join-Path $root $entry.Value)).Hash) { throw "$built differs from $($entry.Value); rebuild." }
}
$clipCount = @($layout.Keys | Where-Object { $_ -like "$plugin/clips/*.bundle" }).Count
$sidecarCount = @($layout.Keys | Where-Object { $_ -like "$plugin/clips/*.clip.json" }).Count
if ($clipCount -eq 0 -or $clipCount -ne $sidecarCount) { throw "Unexpected reload clips: $clipCount bundles, $sidecarCount sidecars." }

New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $layout.GetEnumerator()) {
        # Forward slashes: Windows PowerShell 5.1's Compress-Archive writes backslash entry names.
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $entry.Value), $entry.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    if ($names.Count -ne $layout.Count -or @($names | Where-Object { $_ -match '\\' }).Count -ne 0) { throw 'Unexpected ZIP contents.' }
    if (@($names | Where-Object { $_ -notmatch '^(BepInEx/plugins/StripperClips|SPT_Runtime/user/mods/StripperClips)/' }).Count -ne 0) { throw 'File outside the mod folders in ZIP.' }
    if (@($names | Where-Object { $_ -match '\.(pdb|cs|csproj|ps1|py|blend|exe|log|manifest)$' }).Count -ne 0) { throw 'Non-release file in ZIP.' }
    $sha = [Security.Cryptography.SHA256]::Create()
    foreach ($item in $archive.Entries) {
        $stream = $item.Open()
        try { $hash = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $stream.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $root $layout[$item.FullName]) -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "$($item.FullName) in the ZIP differs from its source." }
    }
}
finally { $archive.Dispose() }

$lines = @()
if ($commit) { $lines += "# source commit $commit" }
$lines += @(foreach ($entry in $layout.GetEnumerator()) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $root $entry.Value) -Algorithm SHA256).Hash.ToLowerInvariant(), $entry.Key
})
$lines += '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), "$name.zip"
$sums = Join-Path $dist "$name.sha256.txt"
[IO.File]::WriteAllLines($sums, [string[]]$lines)
"$($layout.Count) files packaged ($clipCount reload animations)"
$lines[-1]
"ZIP: $zip"
