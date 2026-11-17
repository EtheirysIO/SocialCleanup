<#
.SYNOPSIS
  Writes repo.json (a Dalamud custom plugin repository: a JSON array with one plugin entry) from the packaged
  manifest that DalamudPackager produced for a Release build.

.PARAMETER Manifest
  The packaged manifest, bin/Release/SocialCleanup/SocialCleanup.json (contains the generated AssemblyVersion,
  InternalName and DalamudApiLevel).

.PARAMETER ExpectedVersion
  Four-part version the release was built with. The script fails if the manifest says anything else.

.PARAMETER DownloadUrl
  URL of the latest.zip release asset.

.PARAMETER OutFile
  Where to write repo.json.
#>
param(
    [Parameter(Mandatory = $true)][string] $Manifest,
    [Parameter(Mandatory = $true)][string] $ExpectedVersion,
    [Parameter(Mandatory = $true)][string] $DownloadUrl,
    [Parameter(Mandatory = $true)][string] $OutFile,
    [int] $ExpectedApiLevel = 15,
    [long] $LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
)

$ErrorActionPreference = 'Stop'

$m = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json

foreach ($field in 'Author', 'Name', 'InternalName', 'AssemblyVersion', 'Description', 'Punchline', 'RepoUrl', 'DalamudApiLevel') {
    if ($null -eq $m.$field -or "$($m.$field)" -eq '') {
        throw "Packaged manifest $Manifest has no $field."
    }
}
if ($m.AssemblyVersion -ne $ExpectedVersion) {
    throw "Packaged manifest AssemblyVersion $($m.AssemblyVersion) != expected $ExpectedVersion."
}
if ([int]$m.DalamudApiLevel -ne $ExpectedApiLevel) {
    throw "Packaged manifest DalamudApiLevel $($m.DalamudApiLevel) != expected $ExpectedApiLevel."
}

$entry = [ordered]@{
    Author             = $m.Author
    Name               = $m.Name
    InternalName       = $m.InternalName
    AssemblyVersion    = $m.AssemblyVersion
    Description        = $m.Description
    Punchline          = $m.Punchline
    ApplicableVersion  = if ($m.ApplicableVersion) { $m.ApplicableVersion } else { 'any' }
    RepoUrl            = $m.RepoUrl
    DalamudApiLevel    = [int]$m.DalamudApiLevel
    Tags               = @($m.Tags)
    CategoryTags       = @($m.CategoryTags)
    IsHide             = $false
    IsTestingExclusive = $false
    LastUpdate         = $LastUpdate
    DownloadLinkInstall = $DownloadUrl
    DownloadLinkUpdate  = $DownloadUrl
    DownloadLinkTesting = $DownloadUrl
}

# -InputObject keeps the one-element array an array. LF line endings, UTF-8 without BOM, trailing newline.
$json = (ConvertTo-Json -InputObject @($entry) -Depth 5) -replace "`r`n", "`n"
$outPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
[System.IO.File]::WriteAllText($outPath,$json + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $OutFile for $($m.InternalName) $($m.AssemblyVersion)"
