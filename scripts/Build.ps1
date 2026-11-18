param([string]$SptRoot = $env:SPT_ROOT)
$ErrorActionPreference = 'Stop'
if (!$SptRoot) { throw 'Supply -SptRoot (the SPT 4.1.6 folder) or set SPT_ROOT.' }
$SptRoot = [IO.Path]::GetFullPath($SptRoot)
foreach ($required in 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll', 'BepInEx\core\BepInEx.dll', 'BepInEx\core\0Harmony.dll', 'BepInEx\core\Mono.Cecil.dll', 'SPT_Runtime\SPTarkov.Server.Core.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $SptRoot $required))) { throw "Not an SPT 4.1 installation: $required is missing under $SptRoot." }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root 'artifacts\logs'
$client = Join-Path $root 'client\bin\Release\StripperClips.dll'
$server = Join-Path $root 'server\bin\Release\StripperClips.Server.dll'

function Invoke-Step([string]$name, [scriptblock]$command) {
    Write-Host "== $name"
    $log = Join-Path $logs ($name + '.log')
    # Windows PowerShell treats native stderr as an error under Stop.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command 2>&1 | Tee-Object -FilePath $log | Out-Host; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw "$name failed (exit code $code); see $log." }
}

function Test-UnityBundle([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 1024 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 7) -ne 'UnityFS') {
        throw "$path is not a Unity asset bundle (a Git LFS pointer? run git lfs pull)."
    }
}

foreach ($dir in 'client\bin', 'client\obj', 'server\bin', 'server\obj', 'tests\StripperClips.Tests\bin', 'tests\StripperClips.Tests\obj', 'artifacts\logs') {
    $path = Join-Path $root $dir
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force $logs | Out-Null
$started = Get-Date

Push-Location $root
try {
    $sidecars = @(Get-ChildItem 'clips' -Filter '*.clip.json')
    $bundles = @(Get-ChildItem 'clips' -Filter '*.bundle')
    if ($sidecars.Count -eq 0 -or $sidecars.Count -ne $bundles.Count) { throw "clips\ holds $($sidecars.Count) sidecars and $($bundles.Count) bundles; expected one bundle per sidecar." }
    foreach ($sidecar in $sidecars) {
        $data = Get-Content -LiteralPath $sidecar.FullName -Raw | ConvertFrom-Json
        $name = $sidecar.Name -replace '\.clip\.json$', ''
        if ($data.format -ne 'foundation-clip-sidecar/1' -or $data.name -ne $name -or $data.bundle -ne "$name.bundle") { throw "$($sidecar.Name): unexpected sidecar contents." }
        $bundle = Join-Path 'clips' $data.bundle
        if (!(Test-Path -LiteralPath $bundle)) { throw "$($sidecar.Name) names $($data.bundle), which is missing." }
        Test-UnityBundle (Join-Path $root $bundle)
    }
    $manifest = (Get-Content -LiteralPath 'server\bundles.json' -Raw | ConvertFrom-Json).manifest
    if ($manifest.Count -ne 2) { throw 'server\bundles.json must list the two clip item bundles.' }
    foreach ($entry in $manifest) {
        $file = Join-Path 'items' ($entry.key -replace '^stripperclips/', '')
        if (!(Test-Path -LiteralPath $file)) { throw "bundles.json lists $($entry.key) but $file is missing. Restore the supplied item bundles. In a Git checkout, run git lfs pull." }
        if ((@($entry.dependencyKeys) -join ',') -ne 'shaders,cubemaps') { throw "$($entry.key) must depend on the game's shaders and cubemaps bundles." }
        Test-UnityBundle (Join-Path $root $file)
    }
    Write-Host "== assets: $($bundles.Count) reload animation bundles with sidecars, $($manifest.Count) item bundles"

    Invoke-Step 'rule-tests' { dotnet run --project 'tests\StripperClips.Tests\StripperClips.Tests.csproj' -c Release --nologo }
    Invoke-Step 'build-client' { dotnet build 'client\StripperClips.csproj' -c Release "-p:SptRoot=$SptRoot" -p:ContinuousIntegrationBuild=true --nologo }
    Invoke-Step 'build-server' { dotnet build 'server\StripperClips.Server.csproj' -c Release "-p:SptRoot=$SptRoot" -p:ContinuousIntegrationBuild=true --nologo }

    $version = ([xml](Get-Content 'client\StripperClips.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $serverVersion = ([xml](Get-Content 'server\StripperClips.Server.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($version -ne $serverVersion) { throw "Client version $version and server version $serverVersion differ." }
    Add-Type -Path (Join-Path $SptRoot 'BepInEx\core\Mono.Cecil.dll')
    $allowed = @{
        $client = '^(mscorlib|System|System\.Core|System\.Numerics|netstandard|Assembly-CSharp|Comfort|Comfort\.Unity|Newtonsoft\.Json|BepInEx|0Harmony|AnimationSystem\.Types|ItemComponent\.Types|ItemTemplate\.Types|UnityEngine(\..+)?)$'
        # .NET shared framework (the SPT server's runtime) and the SPT server's own assemblies.
        $server = '^(System(\..+)?|Microsoft\.(Extensions|AspNetCore)\..+|netstandard|SPTarkov\.Server\.Core|SPTarkov\.Common|SPTarkov\.DI|SPTarkov\.Reflection|SemanticVersioning|0Harmony)$'
    }
    $files = [ordered]@{ $client = 'client'; $server = 'server' }
    foreach ($file in @($files.Keys)) {
        $info = Get-Item -LiteralPath $file
        if ($info.LastWriteTime -lt $started) { throw "$file was not written by this build." }
        $bytes = [IO.File]::ReadAllBytes($file)
        foreach ($text in [Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes)) {
            if ($text.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "$file contains the build folder path $root." }
        }
        $module = [Mono.Cecil.ModuleDefinition]::ReadModule($file)
        try {
            if ($module.Assembly.Name.Version.ToString() -ne "$version.0") { throw "$file has assembly version $($module.Assembly.Name.Version); expected $version.0." }
            foreach ($reference in $module.AssemblyReferences) {
                if ($reference.Name -notmatch $allowed[$file]) { throw "$file references $($reference.Name), which is not provided by SPT/EFT." }
            }
            $strings = @($module.Types | ForEach-Object { $_.Methods } | Where-Object { $_.HasBody } | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object { $_.Operand })
            $attributes = @($module.Types | ForEach-Object { $_.CustomAttributes } | Where-Object { $_.AttributeType.Name -eq 'BepInPlugin' } | ForEach-Object { $_.ConstructorArguments[2].Value })
            if ($files[$file] -eq 'client' -and ($attributes.Count -ne 1 -or $attributes[0] -ne $version)) { throw "BepInPlugin version is '$($attributes -join ',')'; expected $version." }
            if ($files[$file] -eq 'server' -and $strings -notcontains $version) { throw "Server mod metadata does not declare version $version." }
        }
        finally { $module.Dispose() }
    }
    foreach ($bundle in Get-ChildItem 'items' -Filter '*.bundle') {
        $copied = Join-Path 'server\bin\Release\bundles\stripperclips' $bundle.Name
        if (!(Test-Path -LiteralPath $copied) -or (Get-FileHash $copied).Hash -ne (Get-FileHash $bundle.FullName).Hash) { throw "The server output does not carry items\$($bundle.Name)." }
    }
    Write-Host "== outputs: version $version, references checked"
    Write-Host ''
    foreach ($output in @($client, $server) + @(Get-ChildItem 'items' -Filter '*.bundle' | ForEach-Object { $_.FullName })) {
        '{0}  {1:yyyy-MM-dd HH:mm:ss}  {2}' -f (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant(), (Get-Item $output).LastWriteTime, $output.Substring($root.Length + 1)
    }
}
finally { Pop-Location }
