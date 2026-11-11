param([string]$PerfOutput = 'artifacts/claude-attachments-perf-20261111/native')
$ErrorActionPreference='Stop'
$root = (Resolve-Path '.').Path
$perfRoot = Join-Path $root $PerfOutput
$mentionRunner = Join-Path $root 'artifacts/claude-attachments-perf-20261111/mention-runner/bin/Release/net10.0-windows/win-x64/Runner.dll'
foreach ($version in @('before','after','optimized')) { Copy-Item -LiteralPath $mentionRunner -Destination (Join-Path $perfRoot "mention-$version/Runner.dll") -Force }
function Run([string]$fixture, [string]$batch) {
 $version = $batch.Split('-')[0]
 $runnerDir = Join-Path $perfRoot "$fixture-$version"
 $output = Join-Path $perfRoot "results-deep-$fixture-$batch"
 New-Item -ItemType Directory -Force -Path $output | Out-Null
 $runnerArgs = @('"' + $output + '"')
 if ($fixture -eq 'image') { $runnerArgs += 'short' }
 $process = Start-Process -FilePath (Join-Path $runnerDir 'Runner.exe') -ArgumentList $runnerArgs -WorkingDirectory $runnerDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'stdout.txt') -RedirectStandardError (Join-Path $output 'stderr.txt') -Wait -PassThru
 if ($process.ExitCode -ne 0) { throw "Fixture $fixture $batch failed with $($process.ExitCode)." }
 Write-Output "Done $fixture $batch"
}
foreach ($batch in @('before-1','after-1','after-2','before-2')) { Run 'image' $batch }
foreach ($batch in @('before-1','after-1','optimized-1','optimized-2','after-2','before-2')) { Run 'mention' $batch }
