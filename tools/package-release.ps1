# Package explicit verified outputs, or rebuild against the requested game's current interop first.
param(
    [string]$OutputDirectory = '',
    [string]$BattleBuildDirectory = '',
    [string]$MapBuildDirectory = '',
    [switch]$Rebuild,
    [string]$GameDir = '',
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('Combined', 'BattleEditor', 'MapFramework', 'All')]
    [string]$Package = 'Combined',
    [string]$ArchiveSuffix = '',
    [switch]$IncludeLocalMaps
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($ArchiveSuffix -and $ArchiveSuffix -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw 'ArchiveSuffix must contain only letters, digits, dots, underscores and hyphens.'
}
if ($IncludeLocalMaps -and $ArchiveSuffix -notmatch '^local-') {
    throw '-IncludeLocalMaps requires a local- archive suffix. Third-party scenery is excluded from public packages.'
}
$needBattle = $Package -ne 'MapFramework'
$needMaps = $Package -ne 'BattleEditor'
if ($Rebuild) {
    if (-not $GameDir) { throw '-Rebuild requires -GameDir with interop generated for the game build being packaged.' }
    if ($BattleBuildDirectory -or $MapBuildDirectory) { throw 'Use explicit build directories or -Rebuild, not both.' }
} elseif (($needBattle -and -not $BattleBuildDirectory) -or ($needMaps -and -not $MapBuildDirectory)) {
    throw 'Pass the verified build directories, or -Rebuild -GameDir. Existing bin/Release outputs are not selected automatically.'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$battleVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'SprocketBattles/SprocketBattles.csproj'))).Project.PropertyGroup.Version
$mapVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'SprocketMaps/SprocketMaps.csproj'))).Project.PropertyGroup.Version
if (-not $battleVersion -or -not $mapVersion) { throw 'Both projects must declare a release version.' }
if ($Rebuild) {
    $buildRoot = Join-Path $outputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
    $BattleBuildDirectory = Join-Path $buildRoot 'BattleEditor'
    $MapBuildDirectory = Join-Path $buildRoot 'MapFramework'
    foreach ($component in @(@('SprocketBattles', $BattleBuildDirectory, $needBattle), @('SprocketMaps', $MapBuildDirectory, $needMaps))) {
        if (-not $component[2]) { continue }
        & $DotnetPath build (Join-Path $repoRoot ($component[0] + '/' + $component[0] + '.csproj')) -c Release "-p:GameDir=$GameDir" "-p:OutputPath=$($component[1])/"
        if ($LASTEXITCODE -ne 0) { throw "$($component[0]) build failed; no package was created." }
    }
}
foreach ($component in @(@('SprocketBattles', $battleVersion, $BattleBuildDirectory, $needBattle), @('SprocketMaps', $mapVersion, $MapBuildDirectory, $needMaps))) {
    if (-not $component[3]) { continue }
    $dll = Join-Path $component[2] ($component[0] + '.dll')
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing verified build file: $dll" }
    $actual = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
    if ($actual.ToString() -ne ([version]$component[1]).ToString(3) + '.0') {
        throw "$($component[0]) was built as $actual; rebuild version $($component[1]) first."
    }
}
$releaseMaps = Join-Path $repoRoot 'Release/BepInEx/plugins/SprocketMaps'
$battlePayload = if ($needBattle) { [ordered]@{
    'BepInEx/plugins/SprocketBattles.dll' = (Join-Path $BattleBuildDirectory 'SprocketBattles.dll')
    'BepInEx/plugins/SprocketBattles-README.txt' = (Join-Path $repoRoot 'Release/BepInEx/plugins/SprocketBattles-README.txt')
    'BepInEx/plugins/SprocketBattles-LICENSE.txt' = (Join-Path $repoRoot 'Release/BepInEx/plugins/SprocketBattles-LICENSE.txt')
} } else { [ordered]@{} }
$mapPayload = if ($needMaps) { [ordered]@{
    'BepInEx/plugins/SprocketMaps/SprocketMaps.dll' = (Join-Path $MapBuildDirectory 'SprocketMaps.dll')
    'BepInEx/plugins/SprocketMaps/SharpCompress.dll' = (Join-Path $MapBuildDirectory 'SharpCompress.dll')
    'BepInEx/plugins/SprocketMaps/SharpCompress-LICENSE.txt' = (Join-Path $releaseMaps 'SharpCompress-LICENSE.txt')
    'BepInEx/plugins/SprocketMaps/Sandbox.png' = (Join-Path $repoRoot 'SprocketMaps/Sandbox.png')
    'BepInEx/plugins/SprocketMaps/Sandbox (Low performance).png' = (Join-Path $repoRoot 'SprocketMaps/Sandbox (Low performance).png')
    'BepInEx/plugins/SprocketMaps/README.txt' = (Join-Path $releaseMaps 'README.txt')
    'BepInEx/plugins/SprocketMaps/LICENSE.txt' = (Join-Path $repoRoot 'LICENSE')
    'BepInEx/plugins/SprocketMaps/Map-Asset-Provenance.txt' = (Join-Path $repoRoot 'Release/Map-Asset-Provenance.txt')
} } else { [ordered]@{} }
if ($IncludeLocalMaps -and $needMaps) {
    # Local fixture allowlist only. Never scan installed maps, user sources or converter caches.
    foreach ($file in @('demo_city_night.png', 'CustomMaps/demo_city_night.bundle', 'CustomMaps/demo_city_night.json',
                       'CustomMaps/fnaf2_pizzeria.bundle', 'CustomMaps/fnaf2_pizzeria.json')) {
        $mapPayload['BepInEx/plugins/SprocketMaps/' + $file] = Join-Path $releaseMaps $file
    }
}

function Write-Package([string]$Name, $Payload) {
    foreach ($file in $Payload.Values) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release file: $file" }
    }
    $label = if ($ArchiveSuffix) { '-' + $ArchiveSuffix } else { '' }
    $zipName = $Name + $label + '.zip'
    $archivePath = Join-Path $outputRoot $zipName
    $checksumPath = $archivePath + '.sha256'
    if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) { throw "Archive or checksum already exists: $archivePath" }
    $stagePath = Join-Path $outputRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
    $archiveOwned = $false
    $checksumOwned = $false
    $boundary = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    try {
        New-Item -ItemType Directory -Path $stagePath | Out-Null
        foreach ($entry in $Payload.GetEnumerator()) {
            $destination = Join-Path $stagePath $entry.Key
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Value -Destination $destination
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $archiveOwned = $true
        try {
            $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($entry in $Payload.GetEnumerator()) {
                    # Bundles already contain compressed chunks; retain them without another compression pass.
                    $compression = if ($entry.Key.EndsWith('.bundle')) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $stagePath $entry.Key), $entry.Key, $compression) | Out-Null
                }
            } finally { $zip.Dispose() }
        } finally { $archiveStream.Dispose() }
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entries = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
            if ($entries.Count -ne $Payload.Count) { throw 'Unexpected file count in release ZIP.' }
            foreach ($entry in $Payload.GetEnumerator()) {
                $stored = $archive.GetEntry($entry.Key)
                if (-not $stored) { throw "Missing ZIP entry: $($entry.Key)" }
                $stream = $stored.Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $sha.Dispose(); $stream.Dispose() }
                if ($digest -ne (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash) { throw "ZIP entry differs from build/source: $($entry.Key)" }
            }
        } finally { $archive.Dispose() }
        $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumStream = [IO.File]::Open($checksumPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $checksumOwned = $true
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes("$checksum  $zipName`n")
            $checksumStream.Write($bytes, 0, $bytes.Length)
        } finally { $checksumStream.Dispose() }
        Write-Output "PACKAGE_OK: $Name; $($Payload.Count) allowlisted files; every ZIP entry hash verified"
        Write-Output $archivePath
        Write-Output "SHA256 $checksum"
    } catch {
        foreach ($ownedFile in @(@($archivePath, $archiveOwned), @($checksumPath, $checksumOwned))) {
            if (-not $ownedFile[1]) { continue }
            $resolvedFile = [IO.Path]::GetFullPath($ownedFile[0])
            if (-not $resolvedFile.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release file escaped the output directory.' }
            if (Test-Path -LiteralPath $resolvedFile) { Remove-Item -LiteralPath $resolvedFile -Force }
        }
        throw
    } finally {
        $resolvedStage = [IO.Path]::GetFullPath($stagePath)
        if (-not $resolvedStage.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging folder escaped the output directory.' }
        if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
    }
}
if ($Package -in @('Combined', 'All')) {
    $combined = [ordered]@{}
    foreach ($payload in @($battlePayload, $mapPayload)) {
        foreach ($entry in $payload.GetEnumerator()) { $combined[$entry.Key] = $entry.Value }
    }
    Write-Package "Sprocket-Battle-Editor-and-Map-Framework-$battleVersion" $combined
}
if ($Package -in @('BattleEditor', 'All')) {
    $battlePayload['BepInEx/plugins/SprocketBattles-README.txt'] = Join-Path $repoRoot 'Release/SprocketBattles-standalone-README.txt'
    Write-Package "Sprocket-Battle-Editor-$battleVersion" $battlePayload
}
if ($Package -in @('MapFramework', 'All')) { Write-Package "Sprocket-Map-Framework-$mapVersion" $mapPayload }
