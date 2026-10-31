#Requires -Version 5.1
<# Update only the application version fields after the changelog is ready. No Git operations. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$ProjectPath,
    [string]$ChangelogPath
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectPath) { $ProjectPath = Join-Path $PSScriptRoot '..\src\Fyltrix\Fyltrix.csproj' }
if (-not $ChangelogPath) { $ChangelogPath = Join-Path $PSScriptRoot '..\CHANGELOG.md' }
$null = & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version $Version -Path $ChangelogPath
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
$original = [IO.File]::ReadAllText($ProjectPath)
[xml]$project = $original
$currentNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($currentNodes.Count -ne 1 -or $currentNodes[0].InnerText -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') { throw 'Expected one existing stable application version.' }
$current = $currentNodes[0].InnerText
if ([version]$Version -le [version]$current) { throw 'Choose a new version greater than the current version.' }
$updated = $original
foreach ($property in @('Version','FileVersion','AssemblyVersion')) {
    $nodes = @($project.SelectNodes("/Project/PropertyGroup/$property"))
    $old = if ($property -eq 'Version') { $current } else { "$current.0" }
    $new = if ($property -eq 'Version') { $Version } else { "$Version.0" }
    if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cne $old -or $nodes[0].HasAttribute('Condition') -or $nodes[0].ParentNode.HasAttribute('Condition')) { throw 'Existing version properties are ambiguous or inconsistent.' }
    $find = "<$property>$old</$property>"
    if ([regex]::Matches($updated,[regex]::Escape($find)).Count -ne 1) { throw 'Version property formatting needs manual review.' }
    $updated = $updated.Replace($find,"<$property>$new</$property>")
}
$pending = $ProjectPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($pending,$updated,(New-Object Text.UTF8Encoding($false)))
    $null = & (Join-Path $PSScriptRoot 'Test-Release.ps1') -Tag "v$Version" -ProjectPath $pending -ChangelogPath $ChangelogPath
    if ([IO.File]::ReadAllText($ProjectPath) -cne $original) { throw 'Project changed during preparation; refusing replacement.' }
    [IO.File]::Replace($pending,$ProjectPath,[NullString]::Value)
} finally { if (Test-Path -LiteralPath $pending) { [IO.File]::Delete($pending) } }
Write-Host "Prepared application version $Version. Review the diff, then commit/push/tag yourself."
