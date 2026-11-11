param([string]$PerfOutput = 'artifacts/claude-attachments-perf-20261111/native')
$ErrorActionPreference='Stop'
$root=(Resolve-Path '.').Path
$perf=Join-Path $root $PerfOutput
$mentionRunner=Join-Path $root 'artifacts/claude-attachments-perf-20261111/mention-runner/bin/Release/net10.0-windows/win-x64/Runner.dll'
foreach($variant in @('unoptimized','optimized')){
 $dir=Join-Path $perf "mention-isolated-$variant"
 New-Item -ItemType Directory -Force -Path $dir | Out-Null
 Copy-Item -Path (Join-Path $perf 'mention-after/*') -Destination $dir -Recurse -Force
 $productDir=Join-Path $root "artifacts/claude-attachments-perf-20261111/$variant-source-final/src/TeXeed.App/bin/Release/net10.0-windows/win-x64"
 Copy-Item -LiteralPath (Join-Path $productDir 'TeXeed.dll'),(Join-Path $productDir 'TeXeed.Core.dll'),$mentionRunner -Destination $dir -Force
}
foreach($kind in @('tiny','invalid')){
 foreach($version in @('before','optimized')){
  $dir=Join-Path $perf "image-$kind-$version"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $runner=Join-Path $root "artifacts/claude-attachments-perf-20261111/image-$kind-runner/bin/Release/net10.0-windows/win-x64"
  Copy-Item -Path (Join-Path $runner '*') -Destination $dir -Recurse -Force
  $product=if($version -eq 'before'){Join-Path $root 'artifacts/TeXeed-0.4.10-20261111'}else{Join-Path $root 'artifacts/claude-attachments-perf-20261111/optimized-source-final/src/TeXeed.App/bin/Release/net10.0-windows/win-x64'}
  Copy-Item -LiteralPath (Join-Path $product 'TeXeed.dll'),(Join-Path $product 'TeXeed.Core.dll') -Destination $dir -Force
 }
}
function Run([string]$runnerDirName,[string]$tag,[string]$arg=''){
 $dir=Join-Path $perf $runnerDirName
 $output=Join-Path $perf "results-$tag"
 New-Item -ItemType Directory -Force -Path $output | Out-Null
 $runnerArgs=@('"'+$output+'"');if($arg){$runnerArgs+=$arg}
 $process=Start-Process -FilePath (Join-Path $dir 'Runner.exe') -ArgumentList $runnerArgs -WorkingDirectory $dir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'stdout.txt') -RedirectStandardError (Join-Path $output 'stderr.txt') -Wait -PassThru
 if($process.ExitCode -ne 0){throw "Fixture $tag failed with $($process.ExitCode)"}
 Write-Output "Done $tag"
}
foreach($variant in @('unoptimized-1','optimized-1','optimized-2','unoptimized-2')){
 $name=$variant.Split('-')[0]
 Run "mention-isolated-$name" "isolated-mention-$variant"
}
foreach($kind in @('tiny','invalid')){
 foreach($variant in @('before-1','optimized-1','optimized-2','before-2')){
  $name=$variant.Split('-')[0]
  Run "image-$kind-$name" "isolated-image-$kind-$variant" 'short'
 }
}
