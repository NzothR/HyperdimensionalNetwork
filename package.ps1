$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
& (Join-Path $repoRoot 'build.ps1')
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..\..'))
$output = Join-Path $gameRoot 'Mod发布包'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'PackageStage'))
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not $stage.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clean a staging path outside the project: $stage" }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$mod = Join-Path $stage 'HyperdimensionalNetwork'
New-Item -ItemType Directory -Path $mod -Force | Out-Null
foreach ($fileName in @('HyperdimensionalNetwork.dll', 'HyperdimensionalNetwork.json', 'ModInfo.xml')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $fileName) -Destination (Join-Path $mod $fileName) -Force
}
foreach ($directoryName in @('Config', 'UIAtlases')) {
    $source = Join-Path $repoRoot $directoryName
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $mod -Recurse -Force }
}
$archive = Join-Path $output 'HyperdimensionalNetwork.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Package created at $archive"
