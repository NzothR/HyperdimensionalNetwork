$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
& (Join-Path $repoRoot 'build.ps1')
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..\..'))
$destination = Join-Path $gameRoot 'Mods\HyperdimensionalNetwork'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($fileName in @('HyperdimensionalNetwork.dll', 'HyperdimensionalNetwork.json', 'ModInfo.xml')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $fileName) -Destination (Join-Path $destination $fileName) -Force
}
foreach ($directoryName in @('Config', 'UIAtlases')) {
    $source = Join-Path $repoRoot $directoryName
    if (-not (Test-Path -LiteralPath $source)) { continue }
    Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
        $sourceRoot = [System.IO.Path]::GetFullPath($source).TrimEnd('\', '/')
        $filePath = [System.IO.Path]::GetFullPath($_.FullName)
        if (-not $filePath.StartsWith($sourceRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Deployment file escaped source directory: $filePath"
        }
        $relative = $filePath.Substring($sourceRoot.Length + 1)
        $target = Join-Path (Join-Path $destination $directoryName) $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}
Write-Host "Deployed runtime files to $destination"
