param(
    [Parameter(Mandatory)] [string] $Product,
    [Parameter(Mandatory)] [string] $Output
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$productDir = (Resolve-Path -LiteralPath $Product).Path
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $destination) { throw "Output directory already exists: $destination" }
if (!$destination.StartsWith((Join-Path $workspace 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an output directory under workspace artifacts.' }
$font = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'TeXeed/fonts/Hiragino_Sans_GB.ttc'
$ibm = Join-Path $productDir 'Assets/Fonts/IBMPlexSans-Regular.otf'
foreach ($required in @($font, $ibm, (Join-Path $productDir 'TeXeed.dll'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required font or product assembly is unavailable: $required" }
}
$previous = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fingerprints.json') -Raw | ConvertFrom-Json
$productHash = (Get-FileHash -LiteralPath (Join-Path $productDir 'TeXeed.dll') -Algorithm SHA256).Hash
if ($productHash -ne $previous.after) { throw 'Candidate assembly differs from the earlier samples.' }
if ((Get-FileHash -LiteralPath $font -Algorithm SHA256).Hash -ne $previous.hiragino -or (Get-FileHash -LiteralPath $ibm -Algorithm SHA256).Hash -ne $previous.ibm) { throw 'Font assets differ from the earlier samples.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$hostProject = Join-Path $PSScriptRoot '../annotation-modes-20261118/Harness.csproj'
$fixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'history-feature-counterfactual.cs.txt')).Path
$hostDir = Join-Path $destination 'host'; $intermediate = (Join-Path $destination 'obj') + [IO.Path]::DirectorySeparatorChar
& dotnet build $hostProject -c Release -p:ProductDir=$productDir -p:Fixture=$fixture -p:BaseIntermediateOutputPath=$intermediate -p:NuGetAudit=false -o $hostDir
if ($LASTEXITCODE -ne 0) { throw 'Failed to build History typography counterfactual host.' }
foreach ($phase in @('on-a', 'off-a', 'off-b', 'on-b')) {
    $run = Join-Path $destination ($phase + '-app'); $data = Join-Path $destination ($phase + '-data')
    New-Item -ItemType Directory -Path $run | Out-Null
    Copy-Item -Path (Join-Path $productDir '*') -Destination $run -Recurse
    Get-ChildItem -LiteralPath $hostDir -File -Filter 'TeXeed.AppTests*' | Copy-Item -Destination $run
    if ((Get-FileHash -LiteralPath (Join-Path $run 'TeXeed.dll') -Algorithm SHA256).Hash -ne $productHash) { throw "Unexpected product assembly in $phase." }
    $mode = if ($phase.StartsWith('off')) { 'no-history-ss04' } else { 'history-ss04' }
    Write-Host "Sampling History ss04 $phase"
    & dotnet (Join-Path $run 'TeXeed.AppTests.dll') $data $mode
    if ($LASTEXITCODE -ne 0) {
        $errorPath = Join-Path $data 'error.txt'
        if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath | Write-Host }
        throw "History ss04 $phase sampling failed."
    }
    $result = Get-Content -LiteralPath (Join-Path $data 'results.json') -Raw | ConvertFrom-Json
    if (@($result.samples).Count -ne 14 -or @($result.samples | Where-Object { $_.visibleControls -le 0 }).Count -gt 0 -or $result.featureOff -ne ($mode -eq 'no-history-ss04')) { throw "Invalid History ss04 $phase sample set." }
    Copy-Item -LiteralPath (Join-Path $data 'results.json') -Destination (Join-Path $PSScriptRoot ('counter-history-' + $phase + '.json'))
}
$fingerprints = [ordered]@{
    assembly = $productHash; hiragino = $previous.hiragino; ibm = $previous.ibm
    fixture = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    host = (Get-FileHash -LiteralPath (Join-Path $hostDir 'TeXeed.AppTests.dll') -Algorithm SHA256).Hash
}
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'counter-history-fingerprints.json') -Encoding utf8
Write-Host 'Completed History ss04 counterfactual samples.'
