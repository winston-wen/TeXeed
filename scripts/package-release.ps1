param(
    [string]$Output = 'artifacts/TeXeed',
    [string]$ArtifactDirectory = 'artifacts/release',
    [string]$ExpectedCommit = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    [xml]$project = Get-Content 'src/TeXeed.App/TeXeed.App.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid release version: $version" }
    $exe = Join-Path $Output 'TeXeed.exe'
    foreach ($file in @($exe, (Join-Path $Output 'README.md'), (Join-Path $Output 'THIRD-PARTY-NOTICES.md'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release file: $file" }
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $exe)).ProductVersion
    if (-not $productVersion.StartsWith("$version+")) { throw "Executable version $productVersion does not match $version" }
    if ($ExpectedCommit -and $productVersion -ne "$version+$ExpectedCommit") { throw "Executable revision $productVersion does not match $ExpectedCommit" }
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    $date = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, 'China Standard Time').ToString('yyyyMMdd')
    $name = "TeXeed-$version-win-x64-$date.zip"
    $zip = Join-Path $ArtifactDirectory $name
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zip -Force
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip))
    try {
        foreach ($required in @('TeXeed.exe', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
            if (-not $archive.GetEntry($required)) { throw "Archive is missing $required" }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path (Resolve-Path $ArtifactDirectory) "$name.sha256"), "$hash  $name`n")
    if ($env:GITHUB_OUTPUT) { "version=$version" >> $env:GITHUB_OUTPUT }
    Write-Host "Packaged $zip (SHA-256 $hash)"
} finally { Pop-Location }
