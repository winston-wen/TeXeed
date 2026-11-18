param(
    [Parameter(Mandatory)] [string] $Product,
    [Parameter(Mandatory)] [string] $Output,
    [switch] $AfterInput
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
$seeds = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../quote-scroll-20261116/math-svg')).Path
$kinds = if ($AfterInput) { @('panel') } else { @('panel', 'mixed') }
foreach ($kind in $kinds) {
    $name = if ($kind -eq 'mixed') { 'mixed-counterfactual.cs.txt' } elseif ($AfterInput) { 'resize-after-input-counterfactual.cs.txt' } else { 'resize-counterfactual.cs.txt' }
    $fixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $name)).Path
    $hostDir = Join-Path $destination ("host-" + $kind)
    $intermediate = (Join-Path $destination ("obj-" + $kind)) + [IO.Path]::DirectorySeparatorChar
    Write-Host "Building $kind counterfactual host"
    & dotnet build $hostProject -c Release -p:ProductDir=$productDir -p:Fixture=$fixture -p:BaseIntermediateOutputPath=$intermediate -p:NuGetAudit=false -o $hostDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to build $kind counterfactual host." }
}
foreach ($kind in $kinds) {
    foreach ($phase in @('new-a', 'old-a', 'old-b', 'new-b')) {
        $label = $kind + '-' + $phase
        $run = Join-Path $destination ($label + '-app'); $data = Join-Path $destination ($label + '-data')
        New-Item -ItemType Directory -Path $run | Out-Null
        Copy-Item -Path (Join-Path $productDir '*') -Destination $run -Recurse
        Get-ChildItem -LiteralPath (Join-Path $destination ('host-' + $kind)) -File -Filter 'TeXeed.AppTests*' | Copy-Item -Destination $run
        if ((Get-FileHash -LiteralPath (Join-Path $run 'TeXeed.dll') -Algorithm SHA256).Hash -ne $productHash) { throw "Unexpected product assembly in $label." }
        $mode = if ($phase.StartsWith('old')) { 'old-font' } else { 'new-font' }
        Write-Host "Sampling $label"
        if ($kind -eq 'panel') { & dotnet (Join-Path $run 'TeXeed.AppTests.dll') $data $mode }
        else { & dotnet (Join-Path $run 'TeXeed.AppTests.dll') $data $seeds $mode }
        if ($LASTEXITCODE -ne 0) {
            $errorPath = Join-Path $data 'error.txt'
            if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath | Write-Host }
            throw "$label sampling failed."
        }
        $result = Get-Content -LiteralPath (Join-Path $data 'results.json') -Raw | ConvertFrom-Json
        $expected = if ($kind -eq 'panel' -and !$AfterInput) { 7 } else { 14 }
        if (@($result.samples).Count -ne $expected -or @($result.samples | Where-Object { $_.visibleControls -le 0 }).Count -gt 0 -or $result.oldFont -ne ($mode -eq 'old-font')) { throw "Invalid $label sample set." }
        $prefix = if ($AfterInput) { 'counter-input-' } else { 'counter-' }
        Copy-Item -LiteralPath (Join-Path $data 'results.json') -Destination (Join-Path $PSScriptRoot ($prefix + $label + '.json'))
    }
}
$panelFixture = if ($AfterInput) { 'resize-after-input-counterfactual.cs.txt' } else { 'resize-counterfactual.cs.txt' }
$fingerprints = [ordered]@{
    assembly = $productHash; hiragino = $previous.hiragino; ibm = $previous.ibm
    panelFixture = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $panelFixture) -Algorithm SHA256).Hash
    panelHost = (Get-FileHash -LiteralPath (Join-Path $destination 'host-panel/TeXeed.AppTests.dll') -Algorithm SHA256).Hash
}
if (!$AfterInput) {
    $fingerprints['mixedFixture'] = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'mixed-counterfactual.cs.txt') -Algorithm SHA256).Hash
    $fingerprints['mixedHost'] = (Get-FileHash -LiteralPath (Join-Path $destination 'host-mixed/TeXeed.AppTests.dll') -Algorithm SHA256).Hash
}
$fingerprintName = if ($AfterInput) { 'counter-input-fingerprints.json' } else { 'counter-fingerprints.json' }
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot $fingerprintName) -Encoding utf8
Write-Host 'Completed reading-font counterfactual samples.'
