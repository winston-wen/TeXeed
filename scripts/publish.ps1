param([string]$Output = "artifacts/TeXeed", [switch]$NoRestore, [switch]$SkipShortcuts)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    $publishArguments = @('publish', 'src/TeXeed.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Output)
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
    if (-not $SkipShortcuts) {
        & (Join-Path $PSScriptRoot 'update-shortcuts.ps1') -Executable (Join-Path $Output 'TeXeed.exe')
    }
    Write-Host "TeXeed is ready: $Output/TeXeed.exe"
} finally { Pop-Location }
