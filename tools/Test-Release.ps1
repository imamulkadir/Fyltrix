#Requires -Version 5.1
<# Read-only validation shared by CI and local release preparation. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [string]$ProjectPath,
    [string]$ChangelogPath
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectPath) { $ProjectPath = Join-Path $PSScriptRoot '..\src\Fyltrix\Fyltrix.csproj' }
if (-not $ChangelogPath) { $ChangelogPath = Join-Path $PSScriptRoot '..\CHANGELOG.md' }
if ($Tag -cnotmatch '\Av((0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8}))\z') { throw 'Tag must be a stable vX.Y.Z version.' }
$version = $Matches[1]
$notes = & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version $version -Path $ChangelogPath
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
foreach ($property in @('Version','AssemblyVersion','FileVersion')) {
    $nodes = @($project.SelectNodes("/Project/PropertyGroup/$property"))
    $expected = if ($property -eq 'Version') { $version } else { "$version.0" }
    if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cne $expected -or $nodes[0].ParentNode.HasAttribute('Condition') -or $nodes[0].HasAttribute('Condition')) {
        throw "$property must be one unconditional value matching $expected in the application project."
    }
}
[pscustomobject]@{ Version=$version; Tag=$Tag; Notes=$notes }
