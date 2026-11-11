param([string]$SnapshotRoot = 'artifacts/claude-attachments-perf-reproduction')
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $SnapshotRoot){throw "Choose a new output directory: $SnapshotRoot"}
New-Item -ItemType Directory -Force -Path $SnapshotRoot | Out-Null
foreach($variant in @('unoptimized','optimized')){
 $target=Join-Path $SnapshotRoot "$variant-source-final"
 New-Item -ItemType Directory -Force -Path $target | Out-Null
 robocopy src (Join-Path $target 'src') /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
 if($LASTEXITCODE -gt 7){throw "Source copy failed: $LASTEXITCODE"}
 Copy-Item -LiteralPath Directory.Build.props -Destination $target
 $candidate=if($variant -eq 'unoptimized'){'candidate-unoptimized-source.cs.txt'}else{'candidate-optimized-source.cs.txt'}
 Copy-Item -LiteralPath (Join-Path 'tests/performance-baselines/claude-attachments-20261111' $candidate) -Destination (Join-Path $target 'src/TeXeed.App/Controls/AiPanel.Mentions.cs') -Force
 $project=Join-Path $target 'src/TeXeed.App/TeXeed.App.csproj'
 dotnet restore $project -r win-x64 --locked-mode -p:NuGetAudit=false
 if($LASTEXITCODE -ne 0){throw "Restore failed: $variant"}
 dotnet build $project -c Release -r win-x64 --no-restore -m:1
 if($LASTEXITCODE -ne 0){throw "Build failed: $variant"}
}
