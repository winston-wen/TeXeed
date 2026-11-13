param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$Repository,
    [string]$ArtifactDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid commit.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required.' }
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$api = "https://api.github.com/repos/$Repository"
function Get-OptionalResource([string]$Uri) {
    try { return Invoke-RestMethod -Uri $Uri -Headers $headers }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}
$tag = "v$Version"
$existing = Get-OptionalResource "$api/releases/tags/$tag"
if ($existing) {
    if ($existing.draft) { throw "Release $tag is a draft; publish or repair it before retrying." }
    $releaseZips = @($existing.assets | Where-Object { $_.name -like '*.zip' })
    $releaseChecksums = @($existing.assets | Where-Object { $_.name -like '*.zip.sha256' })
    $completeZip = @($releaseZips | Where-Object { $zipName = $_.name; @($releaseChecksums | Where-Object { $_.name -eq "$zipName.sha256" }).Count -eq 1 })
    if ($completeZip.Count -eq 0) { throw "Release $tag must have a ZIP and its matching SHA-256 asset; repair it before retrying." }
    Write-Host "Release $tag already exists; no existing release or tag will be changed."
    exit 0
}
$ref = Get-OptionalResource "$api/git/ref/tags/$tag"
if ($ref) {
    $object = $ref.object
    for ($i = 0; $object.type -eq 'tag' -and $i -lt 10; $i++) {
        $object = (Invoke-RestMethod -Uri "$api/git/tags/$($object.sha)" -Headers $headers).object
    }
    if ($object.type -ne 'commit' -or $object.sha -ne $Commit) { throw "Existing tag $tag does not point to this commit; refusing to publish." }
}
$zips = @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter "TeXeed-$Version-win-x64-*.zip" -File)
if ($zips.Count -ne 1) { throw 'Expected exactly one release ZIP.' }
$zip = $zips[0]
$checksum = "$($zip.FullName).sha256"
if (-not (Test-Path -LiteralPath $checksum -PathType Leaf)) { throw 'Missing checksum file.' }
$expected = ((Get-Content -LiteralPath $checksum -Raw).Trim() -split '\s+')[0]
if ($expected -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $zip.FullName).Hash) { throw 'Release ZIP checksum mismatch.' }
$arguments = @('release', 'create', $tag, $zip.FullName, $checksum, '--repo', $Repository, '--target', $Commit, '--title', "TeXeed $Version", '--generate-notes')
if ($Version.Contains('-')) { $arguments += '--prerelease' }
& gh @arguments
if ($LASTEXITCODE -ne 0) { throw "Failed to create release $tag." }
$published = Get-OptionalResource "$api/releases/tags/$tag"
if (-not $published -or @($published.assets | Where-Object { $_.name -eq $zip.Name }).Count -ne 1 -or @($published.assets | Where-Object { $_.name -eq (Split-Path -Leaf $checksum) }).Count -ne 1) { throw "Release $tag is missing uploaded assets." }
Write-Host "Published $tag at $($published.html_url)"
