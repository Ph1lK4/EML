#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [string] $Version = '1.0.0-rc.2',
    [string] $DisplayVersion = '1.0-RC2',
    [string] $RuntimeVersion = '10.0.13',
    [ValidateSet('Flat', 'AppDirectory')][string] $Layout = 'AppDirectory'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
if (-not (Test-Path -LiteralPath (Join-Path $Path 'release-manifest.json') -PathType Leaf)) {
    throw 'Release manifest is required when verifying a distributed package.'
}
Test-EmlReleaseLayout -Path $Path -Version $Version -DisplayVersion $DisplayVersion -RuntimeVersion $RuntimeVersion -Layout $Layout
