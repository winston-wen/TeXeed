$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $dir = 'artifacts/release-policy-test'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $zip = Join-Path $dir 'TeXeed-9.8.7-win-x64-20261113.zip'
    [IO.File]::WriteAllText((Join-Path (Get-Location) $zip), 'test package')
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path (Get-Location) "$zip.sha256"), "$hash  $([IO.Path]::GetFileName($zip))`n")
    $env:GH_TOKEN = 'test-token'
    $global:testRelease = $null
    $global:testRef = $null
    $global:testGhCalls = 0
    function global:Invoke-RestMethod {
        param($Uri, $Headers)
        if ($Uri -like '*/releases/tags/*') { return $global:testRelease }
        if ($Uri -like '*/git/ref/tags/*') { return $global:testRef }
        throw "Unexpected API request: $Uri"
    }
    function global:gh {
        $global:testGhCalls++
        $global:LASTEXITCODE = 0
        $global:testRelease = @{ html_url = 'https://example.invalid/release'; assets = @(@{ name = 'TeXeed-9.8.7-win-x64-20261113.zip' }, @{ name = 'TeXeed-9.8.7-win-x64-20261113.zip.sha256' }) }
    }
    $params = @{ Version = '9.8.7'; Commit = ('a' * 40); Repository = 'test/project'; ArtifactDirectory = $dir }
    & ./scripts/release-github.ps1 @params
    if ($global:testGhCalls -ne 1) { throw 'A new release must be created exactly once.' }
    & ./scripts/release-github.ps1 @params
    if ($global:testGhCalls -ne 1) { throw 'An existing release must not be overwritten.' }
    $global:testRelease.draft = $true
    $rejected = $false
    try { & ./scripts/release-github.ps1 @params } catch { $rejected = $true }
    if (-not $rejected -or $global:testGhCalls -ne 1) { throw 'A draft release must be rejected.' }
    $global:testRelease.draft = $false
    $global:testRelease.assets = @(@{ name = 'TeXeed-9.8.7-win-x64-20261113.zip' })
    $rejected = $false
    try { & ./scripts/release-github.ps1 @params } catch { $rejected = $true }
    if (-not $rejected -or $global:testGhCalls -ne 1) { throw 'A release missing its matching checksum must be rejected.' }
    $global:testRelease = $null
    $global:testRef = @{ object = @{ type = 'commit'; sha = ('b' * 40) } }
    $rejected = $false
    try { & ./scripts/release-github.ps1 @params } catch { $rejected = $true }
    if (-not $rejected -or $global:testGhCalls -ne 1) { throw 'A tag pointing at another commit must be rejected.' }
    $global:testRef = $null
    [IO.File]::WriteAllText((Join-Path (Get-Location) $zip), 'tampered')
    $rejected = $false
    try { & ./scripts/release-github.ps1 @params } catch { $rejected = $true }
    if (-not $rejected -or $global:testGhCalls -ne 1) { throw 'A modified release package must be rejected.' }
    Write-Host 'PASS: new release, existing release, draft, missing checksum, conflicting tag and checksum mismatch'
} finally {
    Remove-Item function:global:Invoke-RestMethod, function:global:gh -ErrorAction SilentlyContinue
    Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue
    Pop-Location
}
