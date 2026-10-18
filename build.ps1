$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$dotnetPath = Join-Path $repoRoot '..\Lib\dotnet-sdk\dotnet.exe'
$projectPath = Join-Path $repoRoot 'Source\HyperdimensionalNetwork.csproj'
$outputPath = Join-Path $repoRoot 'Source\bin\Release\net48'

if (-not (Test-Path -LiteralPath $dotnetPath)) { throw "Bundled .NET SDK not found: $dotnetPath" }
& $dotnetPath build $projectPath -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "HyperdimensionalNetwork build failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $outputPath 'HyperdimensionalNetwork.dll') -Destination (Join-Path $repoRoot 'HyperdimensionalNetwork.dll') -Force
$pdb = Join-Path $outputPath 'HyperdimensionalNetwork.pdb'
if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $repoRoot 'HyperdimensionalNetwork.pdb') -Force }
Write-Host 'Build complete.'
