<#
.SYNOPSIS
  Turns a release tag (vX.Y.Z or vX.Y.Z.W) into a four-part version and checks it against the csproj <Version>.

.DESCRIPTION
  Writes the four-part version to stdout. Fails when the tag is malformed or does not match the csproj, so a release
  can only be cut after the version has been bumped and committed.
#>
param(
    [Parameter(Mandatory = $true)][string] $Tag,
    [Parameter(Mandatory = $true)][string] $Csproj
)

$ErrorActionPreference = 'Stop'

function ConvertTo-FourPartVersion([string] $value, [string] $what) {
    if ($value -notmatch '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
        throw "$what '$value' is not a version of the form X.Y.Z or X.Y.Z.W."
    }
    $rev = if ($Matches[4]) { $Matches[4] } else { '0' }
    return '{0}.{1}.{2}.{3}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$rev
}

if ($Tag -notmatch '^v(.+)$') {
    throw "Tag '$Tag' must start with 'v' (for example v1.0.0 or v1.0.0.0)."
}
$tagVersion = ConvertTo-FourPartVersion $Matches[1] "Version part of tag $Tag,"

[xml] $proj = Get-Content -LiteralPath $Csproj -Raw
$nodes = @($proj.SelectNodes('/Project/PropertyGroup/Version'))
if ($nodes.Count -ne 1) {
    throw "Expected exactly one <Version> in $Csproj, found $($nodes.Count)."
}
$projVersion = ConvertTo-FourPartVersion $nodes[0].InnerText.Trim() "csproj <Version>"

if ($tagVersion -ne $projVersion) {
    throw "Tag $Tag ($tagVersion) does not match <Version> $projVersion in $Csproj. Bump the csproj version, commit, then tag that commit."
}

Write-Output $tagVersion
