param(
    [Parameter(Mandatory)] [string] $ProductDir,
    [Parameter(Mandatory)] [string] $OutputDir,
    [Parameter(Mandatory)] [string] $PanelHostDir,
    [Parameter(Mandatory)] [string] $InteractionHostDir
)

$ErrorActionPreference = 'Stop'
$product = (Resolve-Path -LiteralPath $ProductDir).Path
$panelHost = (Resolve-Path -LiteralPath $PanelHostDir).Path
$interactionHost = (Resolve-Path -LiteralPath $InteractionHostDir).Path
$destination = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $destination) { throw "Output directory already exists: $destination" }
foreach ($required in @((Join-Path $product 'TeXeed.dll'), (Join-Path $panelHost 'Perf.dll'), (Join-Path $interactionHost 'Harness.dll'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing required file: $required" }
}

New-Item -ItemType Directory -Path $destination | Out-Null
$app = Join-Path $destination 'app'
New-Item -ItemType Directory -Path $app | Out-Null
Copy-Item -Path (Join-Path $product '*') -Destination $app -Recurse
Get-ChildItem -LiteralPath $panelHost -File -Filter 'Perf*' | Copy-Item -Destination $app
Get-ChildItem -LiteralPath $interactionHost -File -Filter 'Harness*' | Copy-Item -Destination $app
$seeds = Join-Path $PSScriptRoot 'math-svg'
$fingerprints = @(
    Get-FileHash -LiteralPath (Join-Path $app 'TeXeed.dll'), (Join-Path $app 'Perf.dll'), (Join-Path $app 'Harness.dll'), (Join-Path $PSScriptRoot 'interaction-deep.cs.txt'), (Join-Path $PSScriptRoot '../steering-20261112/panel-fixture.cs.txt')
    Get-ChildItem -LiteralPath $seeds -Filter '*.svg' -File | Get-FileHash
) | Select-Object Algorithm, Hash, Path
$fingerprints | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'fingerprints.json') -Encoding utf8

& dotnet (Join-Path $app 'Perf.dll') (Join-Path $destination 'panel')
if ($LASTEXITCODE -ne 0) { throw "Panel measurement failed: $LASTEXITCODE" }
$panel = Get-Content -LiteralPath (Join-Path $destination 'panel/results.json') -Raw | ConvertFrom-Json
if (@($panel.samples).Count -ne 28 -or @($panel.samples | Where-Object { $_.visibleControls -le 0 }).Count -gt 0) { throw 'Invalid panel samples: incomplete or invisible window.' }

& dotnet (Join-Path $app 'Harness.dll') (Join-Path $destination 'interaction') $seeds
if ($LASTEXITCODE -ne 0) { throw "Interaction measurement failed: $LASTEXITCODE" }
$interaction = Get-Content -LiteralPath (Join-Path $destination 'interaction/results.json') -Raw | ConvertFrom-Json
if (@($interaction.samples).Count -ne 21 -or @($interaction.samples | Where-Object { $_.visibleControls -le 0 }).Count -gt 0) { throw 'Invalid interaction samples: incomplete or invisible window.' }
if (@($interaction.samples | Where-Object { $_.op -eq 'mixed-citation' -and $_.formulaVisuals -ne 4 }).Count -gt 0) { throw 'Mixed citation did not create all four formula visuals.' }
Write-Host "Raw measurements and fingerprints: $destination"
