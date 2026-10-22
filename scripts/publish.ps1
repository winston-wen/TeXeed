param([string]$Output = "artifacts/TeXeed", [switch]$NoRestore, [switch]$SkipShortcuts, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    $runtimeConfigPath = Join-Path $Output 'TeXeed.runtimeconfig.json'
    if (Test-Path -LiteralPath $runtimeConfigPath) {
        $previousRuntime = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
        $previousSelfContained = $null -ne $previousRuntime.runtimeOptions.includedFrameworks
        if ($previousSelfContained -ne $SelfContained.IsPresent) { throw 'Use a new output directory when changing the runtime deployment mode; existing releases are preserved.' }
    }
    $publishArguments = @('publish', 'src/TeXeed.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '-o', $Output)
    if ($NoRestore) { $publishArguments += '--no-restore' }
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md,THIRD-PARTY-NOTICES.md -Destination $Output
    if (Test-Path -LiteralPath docs) { Copy-Item -LiteralPath docs -Destination $Output -Recurse -Force }
    if (Test-Path -LiteralPath examples) {
        $exampleRoot = (Resolve-Path -LiteralPath examples).Path
        foreach ($file in Get-ChildItem -LiteralPath $exampleRoot -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($exampleRoot, $file.FullName)
            if ($relative.Split([IO.Path]::DirectorySeparatorChar) -contains '.texeed') { continue }
            $destination = Join-Path $Output (Join-Path 'examples' $relative)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
    if (Test-Path -LiteralPath licenses) { Copy-Item -LiteralPath licenses -Destination $Output -Recurse -Force }
    if (-not $SelfContained) {
        $publishedRuntime = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
        if ('Microsoft.WindowsDesktop.App' -notin $publishedRuntime.runtimeOptions.frameworks.name) { throw 'Published app must reference the shared .NET Desktop Runtime.' }
        foreach ($runtimeFile in @('coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
            if (Test-Path -LiteralPath (Join-Path $Output $runtimeFile)) { throw "Shared-runtime output contains a bundled runtime file: $runtimeFile" }
        }
    }
    if (-not $SkipShortcuts) {
        & (Join-Path $PSScriptRoot 'update-shortcuts.ps1') -Executable (Join-Path $Output 'TeXeed.exe')
    }
    Write-Host "TeXeed is ready: $Output/TeXeed.exe"
} finally { Pop-Location }
