param(
 [Parameter(Mandatory=$true)][string]$AfterAppDir,
 [string]$BeforeAppDir = 'artifacts/TeXeed-0.4.10-20261111',
 [string]$PerfOutput = 'artifacts/claude-attachments-perf-20261111/native'
)
$ErrorActionPreference='Stop'
$root = (Resolve-Path '.').Path
$beforeApp = (Resolve-Path -LiteralPath $BeforeAppDir).Path
$afterApp = (Resolve-Path -LiteralPath $AfterAppDir).Path
$performanceRoot = Join-Path $root $PerfOutput
New-Item -ItemType Directory -Force -Path $performanceRoot | Out-Null
foreach ($fixture in @('panel','list','image','path','mention')) {
 $fixtureDir = if ($fixture -eq 'panel') { 'panel-after' } else { 'after' }
 $runnerSource = if ($fixture -in @('image','path','mention')) { Join-Path $root "artifacts/claude-attachments-perf-20261111/$fixture-runner/bin/Release/net10.0-windows/win-x64" } else { Join-Path $root "artifacts/folder-slash-perf-20261111/$fixtureDir/bin/Release/net10.0-windows/win-x64" }
 foreach ($version in @('before','after')) {
  $runnerDest = Join-Path $performanceRoot "$fixture-$version"
  New-Item -ItemType Directory -Force -Path $runnerDest | Out-Null
  Copy-Item -Path (Join-Path $runnerSource '*') -Destination $runnerDest -Recurse -Force
  $appDir = if ($version -eq 'before') { $beforeApp } else { $afterApp }
  Copy-Item -LiteralPath (Join-Path $appDir 'TeXeed.dll'),(Join-Path $appDir 'TeXeed.Core.dll') -Destination $runnerDest -Force
 }
}
function Invoke-Fixture([string]$Fixture,[string]$Version,[string]$PathCase='') {
 $runnerDir = Join-Path $performanceRoot "$Fixture-$Version"
 $out = Join-Path $performanceRoot "results-$Fixture-$Version-$PathCase"
 New-Item -ItemType Directory -Force -Path $out | Out-Null
 $arguments = @('"' + $out + '"')
 if ($PathCase) { $arguments += $PathCase }
 $process = Start-Process -FilePath (Join-Path $runnerDir 'Runner.exe') -ArgumentList $arguments -WorkingDirectory $runnerDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $out 'stdout.txt') -RedirectStandardError (Join-Path $out 'stderr.txt') -Wait -PassThru
 if ($process.ExitCode -ne 0) { throw "Fixture $Fixture $Version $PathCase failed with $($process.ExitCode)." }
}
Invoke-Fixture 'list' 'before' 'short'
Invoke-Fixture 'list' 'after' 'short'
Invoke-Fixture 'list' 'before' 'long'
Invoke-Fixture 'list' 'after' 'long'
Invoke-Fixture 'path' 'before' 'long'
Invoke-Fixture 'path' 'after' 'long'
Invoke-Fixture 'image' 'before' 'short'
Invoke-Fixture 'image' 'after' 'short'
Invoke-Fixture 'mention' 'before'
Invoke-Fixture 'mention' 'after'
Invoke-Fixture 'panel' 'before'
Invoke-Fixture 'panel' 'after'
