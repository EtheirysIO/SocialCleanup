<#
.SYNOPSIS
  Resolves the version and release channel of a build and checks a release tag against the csproj <Version>.

.DESCRIPTION
  Writes two lines to stdout, in the format of $GITHUB_OUTPUT:

    version=X.Y.Z.W
    channel=latest|testing|none

  - Tag vX.Y.Z.W            -> channel latest  (stable release)
  - Tag vX.Y.Z.W-<suffix>   -> channel testing (pre-release), for example v1.0.1.0-test1
  - No tag (branch, PR)     -> channel none, version read from the csproj

  For a tag, the numeric part must equal the csproj <Version> exactly, so a release can only be cut after the version
  has been bumped and committed. Malformed tags and versions fail. Numbers are written without leading zeros, so the
  tag, the csproj and the CHANGELOG.md heading (## X.Y.Z.W) always agree as text.
#>
param(
    [string] $Tag = '',
    [Parameter(Mandatory = $true)][string] $Csproj
)

$ErrorActionPreference = 'Stop'

$number = '(?:0|[1-9][0-9]{0,4})'
$versionPattern = "^$number\.$number\.$number\.$number$"

[xml] $proj = Get-Content -LiteralPath $Csproj -Raw
$nodes = @($proj.SelectNodes('/Project/PropertyGroup/Version'))
if ($nodes.Count -ne 1) {
    throw "Expected exactly one <Version> in $Csproj, found $($nodes.Count)."
}
$projVersion = $nodes[0].InnerText.Trim()
if ($projVersion -notmatch $versionPattern) {
    throw "csproj <Version> '$projVersion' must have four numeric parts, like 1.0.0.0."
}

if ($Tag -eq '') {
    Write-Output "version=$projVersion"
    Write-Output 'channel=none'
    return
}

# v + four-part version, optionally followed by -suffix (letters, digits and dots), e.g. v1.0.1.0-test1.
if ($Tag -cnotmatch "^v($number\.$number\.$number\.$number)(-[0-9A-Za-z][0-9A-Za-z.]*)?$") {
    throw "Tag '$Tag' must be vX.Y.Z.W (stable) or vX.Y.Z.W-suffix (testing), for example v1.0.0.0 or v1.0.0.0-test1."
}
$tagVersion = $Matches[1]
$channel = if ($Matches[2]) { 'testing' } else { 'latest' }

if ($tagVersion -ne $projVersion) {
    throw "Tag $Tag ($tagVersion) does not match <Version> $projVersion in $Csproj. Bump the csproj version, commit, then tag that commit."
}

Write-Output "version=$tagVersion"
Write-Output "channel=$channel"
