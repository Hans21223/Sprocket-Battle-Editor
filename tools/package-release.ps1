# Package freshly built Battle Editor and Map Framework. Run the two Release builds and tests first (see README).
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$battleVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'SprocketBattles/SprocketBattles.csproj'))).Project.PropertyGroup.Version
$mapVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'SprocketMaps/SprocketMaps.csproj'))).Project.PropertyGroup.Version
if (-not $battleVersion -or -not $mapVersion) { throw 'Both projects must declare a release version.' }
$payload = [ordered]@{
    'BepInEx/plugins/SprocketBattles.dll' = 'SprocketBattles/bin/Release/net6.0/SprocketBattles.dll'
    'BepInEx/plugins/SprocketBattles-README.txt' = 'Release/BepInEx/plugins/SprocketBattles-README.txt'
    'BepInEx/plugins/SprocketBattles-LICENSE.txt' = 'Release/BepInEx/plugins/SprocketBattles-LICENSE.txt'
    'BepInEx/plugins/SprocketMaps/SprocketMaps.dll' = 'SprocketMaps/bin/Release/net6.0/SprocketMaps.dll'
    'BepInEx/plugins/SprocketMaps/Sandbox.png' = 'SprocketMaps/Sandbox.png'
    'BepInEx/plugins/SprocketMaps/Sandbox (Low performance).png' = 'SprocketMaps/Sandbox (Low performance).png'
    'BepInEx/plugins/SprocketMaps/README.txt' = 'Release/BepInEx/plugins/SprocketMaps/README.txt'
    'BepInEx/plugins/SprocketMaps/LICENSE.txt' = 'LICENSE'
}
foreach ($file in $payload.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $file) -PathType Leaf)) { throw "Missing release file: $file" }
}
foreach ($component in @(@('SprocketBattles', $battleVersion), @('SprocketMaps', $mapVersion))) {
    $dll = Join-Path $repoRoot "$($component[0])/bin/Release/net6.0/$($component[0]).dll"
    $actual = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
    if ($actual.ToString() -ne ([version]$component[1]).ToString(3) + '.0') {
        throw "$($component[0]) was built as $actual; rebuild version $($component[1]) first."
    }
}
$zipName = "Sprocket-Battle-Editor-and-Map-Framework-$battleVersion.zip"
$archivePath = Join-Path $outputRoot $zipName
$checksumPath = $archivePath + '.sha256'
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) { throw "Archive or checksum already exists: $archivePath" }
$stagePath = Join-Path $outputRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
$archiveOwned = $false
$checksumOwned = $false
$boundary = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
try {
    New-Item -ItemType Directory -Path $stagePath | Out-Null
    foreach ($entry in $payload.GetEnumerator()) {
        $destination = Join-Path $stagePath $entry.Key
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $entry.Value) -Destination $destination
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # CreateNew claims this attempt's archive without replacing another file.
    $archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archiveOwned = $true
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entry in $payload.GetEnumerator()) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $stagePath $entry.Key), $entry.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $zip.Dispose() }
    } finally { $archiveStream.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($entries.Count -ne $payload.Count) { throw 'Unexpected file count in release ZIP.' }
        foreach ($entry in $payload.GetEnumerator()) {
            $stored = $archive.GetEntry($entry.Key)
            if (-not $stored) { throw "Missing ZIP entry: $($entry.Key)" }
            $stream = $stored.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($digest -ne (Get-FileHash -LiteralPath (Join-Path $repoRoot $entry.Value) -Algorithm SHA256).Hash) {
                throw "ZIP entry differs from build/source: $($entry.Key)"
            }
        }
    } finally { $archive.Dispose() }
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumStream = [IO.File]::Open($checksumPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $checksumOwned = $true
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes("$checksum  $zipName`n")
        $checksumStream.Write($bytes, 0, $bytes.Length)
    } finally { $checksumStream.Dispose() }
    Write-Output "PACKAGE_OK: Battle Editor $battleVersion + Map Framework $mapVersion; $($payload.Count) files; every ZIP entry hash verified"
    Write-Output $archivePath
    Write-Output "SHA256 $checksum"
} catch {
    # A failed attempt must not leave an invalid archive or checksum blocking the next run.
    foreach ($ownedFile in @(@($archivePath, $archiveOwned), @($checksumPath, $checksumOwned))) {
        if (-not $ownedFile[1]) { continue }
        $resolvedFile = [IO.Path]::GetFullPath($ownedFile[0])
        if (-not $resolvedFile.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release file escaped the output directory.' }
        if (Test-Path -LiteralPath $resolvedFile) { Remove-Item -LiteralPath $resolvedFile -Force }
    }
    throw
} finally {
    # Only remove this script's newly created staging folder, verified beneath the requested output directory.
    $resolvedStage = [IO.Path]::GetFullPath($stagePath)
    if (-not $resolvedStage.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging folder escaped the output directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
