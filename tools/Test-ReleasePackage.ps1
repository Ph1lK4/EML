#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [string] $Version = '1.0.0-rc.1',
    [string] $DisplayVersion = '1.0-RC1'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
if (-not (Test-Path -LiteralPath (Join-Path $Path 'release-manifest.json') -PathType Leaf)) {
    throw 'Release manifest is required when verifying a distributed package.'
}
Test-EmlReleaseLayout -Path $Path -Version $Version -DisplayVersion $DisplayVersion
