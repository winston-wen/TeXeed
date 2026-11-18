param(
    [Parameter(Mandatory)] [string] $Before,
    [Parameter(Mandatory)] [string] $After,
    [Parameter(Mandatory)] [string] $Output,
    [switch] $OnlyMixed,
    [string] $PanelRun
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$old = (Resolve-Path -LiteralPath $Before).Path
$new = (Resolve-Path -LiteralPath $After).Path
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $destination) { throw "Output directory already exists: $destination" }
if (!$destination.StartsWith((Join-Path $workspace 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an output directory under workspace artifacts.' }
$font = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'TeXeed/fonts/Hiragino_Sans_GB.ttc'
$ibm = Join-Path $new 'Assets/Fonts/IBMPlexSans-Regular.otf'
foreach ($required in @($font, $ibm, (Join-Path $old 'TeXeed.dll'), (Join-Path $new 'TeXeed.dll'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "A required font or product assembly is unavailable: $required" }
}
$beforeHash = (Get-FileHash -LiteralPath (Join-Path $old 'TeXeed.dll') -Algorithm SHA256).Hash
$afterHash = (Get-FileHash -LiteralPath (Join-Path $new 'TeXeed.dll') -Algorithm SHA256).Hash
if ($beforeHash -eq $afterHash) { throw 'Before and after assemblies are identical; refusing to compare.' }
if ($OnlyMixed) {
    if ([string]::IsNullOrWhiteSpace($PanelRun)) { throw 'Specify the earlier isolated panel run when repeating only the mixed fixture.' }
    $previous = (Resolve-Path -LiteralPath $PanelRun).Path
    $oldPanel = (Get-FileHash -LiteralPath (Join-Path $previous 'before-panel-app/TeXeed.dll') -Algorithm SHA256).Hash
    $newPanel = (Get-FileHash -LiteralPath (Join-Path $previous 'after-panel-app/TeXeed.dll') -Algorithm SHA256).Hash
    if ($oldPanel -ne $beforeHash -or $newPanel -ne $afterHash) { throw 'The products do not match the existing panel samples.' }
}
New-Item -ItemType Directory -Path $destination | Out-Null
$hostProject = Join-Path $PSScriptRoot '../annotation-modes-20261118/Harness.csproj'
$panelFixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../manual-form-unlock-20261118/panel-fixture.cs.txt')).Path
$mathFixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'mixed-math-fixture.cs.txt')).Path
$seeds = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../quote-scroll-20261116/math-svg')).Path
$fixtureNames = if ($OnlyMixed) { @('mixed') } else { @('panel', 'mixed') }
foreach ($name in $fixtureNames) {
    $fixture = if ($name -eq 'panel') { $panelFixture } else { $mathFixture }
    $hostDir = Join-Path $destination ("host-" + $name)
    $intermediate = (Join-Path $destination ("obj-" + $name)) + [IO.Path]::DirectorySeparatorChar
    Write-Host "Building $name test host"
    & dotnet build $hostProject -c Release -p:ProductDir=$old -p:Fixture=$fixture -p:BaseIntermediateOutputPath=$intermediate -p:NuGetAudit=false -o $hostDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to build $name host." }
}
$cases = if ($OnlyMixed) { @(@('before', $old, 'mixed'), @('after', $new, 'mixed')) } else { @(@('before', $old, 'panel'), @('after', $new, 'panel'), @('before', $old, 'mixed'), @('after', $new, 'mixed')) }
foreach ($case in $cases) {
    $label = $case[0]; $product = $case[1]; $name = $case[2]
    $run = Join-Path $destination ($label + '-' + $name + '-app')
    $data = Join-Path $destination ($label + '-' + $name + '-data')
    New-Item -ItemType Directory -Path $run | Out-Null
    Copy-Item -Path (Join-Path $product '*') -Destination $run -Recurse
    Get-ChildItem -LiteralPath (Join-Path $destination ('host-' + $name)) -File -Filter 'TeXeed.AppTests*' | Copy-Item -Destination $run
    $loadedHash = (Get-FileHash -LiteralPath (Join-Path $run 'TeXeed.dll') -Algorithm SHA256).Hash
    $expectedHash = if ($label -eq 'before') { $beforeHash } else { $afterHash }
    if ($loadedHash -ne $expectedHash) { throw "Unexpected product assembly in $label $name." }
    Write-Host "Sampling $label $name"
    if ($name -eq 'panel') { & dotnet (Join-Path $run 'TeXeed.AppTests.dll') $data }
    else { & dotnet (Join-Path $run 'TeXeed.AppTests.dll') $data $seeds }
    if ($LASTEXITCODE -ne 0) {
        $errorPath = Join-Path $data 'error.txt'
        if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath | Write-Host }
        throw "$label $name sampling failed."
    }
    $result = Get-Content -LiteralPath (Join-Path $data 'results.json') -Raw | ConvertFrom-Json
    $expected = if ($name -eq 'panel') { 28 } else { 14 }
    if (@($result.samples).Count -ne $expected -or @($result.samples | Where-Object { $_.visibleControls -le 0 }).Count -gt 0) { throw "Invalid $label $name sample set." }
    if ($label -eq 'after' -and $name -eq 'mixed' -and (!$result.fontAudit.revisedTypography -or !$result.fontAudit.latinGlyphsMatchIbm -or !$result.fontAudit.chineseGlyphsMatchHiragino)) { throw 'The candidate did not render both required fonts.' }
    Copy-Item -LiteralPath (Join-Path $data 'results.json') -Destination (Join-Path $PSScriptRoot ($label + '-' + $name + '.json'))
}
$panelHost = if ($OnlyMixed) { Join-Path $previous 'host-panel/TeXeed.AppTests.dll' } else { Join-Path $destination 'host-panel/TeXeed.AppTests.dll' }
$fingerprints = [ordered]@{
    before = $beforeHash; after = $afterHash
    hiragino = (Get-FileHash -LiteralPath $font -Algorithm SHA256).Hash
    ibm = (Get-FileHash -LiteralPath $ibm -Algorithm SHA256).Hash
    panelFixture = (Get-FileHash -LiteralPath $panelFixture -Algorithm SHA256).Hash
    mathFixture = (Get-FileHash -LiteralPath $mathFixture -Algorithm SHA256).Hash
    panelHost = (Get-FileHash -LiteralPath $panelHost -Algorithm SHA256).Hash
    mathHost = (Get-FileHash -LiteralPath (Join-Path $destination 'host-mixed/TeXeed.AppTests.dll') -Algorithm SHA256).Hash
}
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'fingerprints.json') -Encoding utf8
Write-Host 'Completed reading-font performance samples.'
