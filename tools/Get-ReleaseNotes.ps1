#Requires -Version 5.1
<# Extract one nonempty changelog section. No Git or network writes. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Path
)
$ErrorActionPreference = 'Stop'
if (-not $Path) { $Path = Join-Path $PSScriptRoot '..\CHANGELOG.md' }
if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z') { throw 'A stable X.Y.Z version is required.' }
foreach ($part in $Version.Split('.')) { if ([int]$part -gt 65534) { throw 'Version exceeds Windows metadata limits.' } }
if ((Get-Item -LiteralPath $Path).Length -gt 1024 * 1024) { throw 'Changelog exceeds 1 MiB.' }
$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
$heading = '^## \[' + [regex]::Escape($Version) + '\](?: - \d{4}-\d{2}-\d{2})?\s*$'
$starts = @(); $fence = ''
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s{0,3}(`{3,}|~{3,})') {
        $mark = $Matches[1].Substring(0,1)
        if ($fence -eq '') { $fence = $mark } elseif ($fence -eq $mark) { $fence = '' }
        continue
    }
    if ($fence -eq '' -and $lines[$i] -cmatch $heading) { $starts += $i }
}
if ($starts.Count -ne 1) { throw "Changelog must contain exactly one section for $Version." }
$body = New-Object 'Collections.Generic.List[string]'; $fence = ''
for ($i = $starts[0] + 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($fence -eq '' -and $line -match '^##\s') { break }
    if ($line -match '^\s{0,3}(`{3,}|~{3,})') {
        $mark = $Matches[1].Substring(0,1)
        if ($fence -eq '') { $fence = $mark } elseif ($fence -eq $mark) { $fence = '' }
    }
    $body.Add($line)
}
$notes = ($body -join "`n").Trim()
$content = ($notes -replace '(?s)<!--.*?-->', '' -replace '(?m)^\s*#{1,6}\s.*$', '').Trim()
if (-not $content -or $fence -ne '') { throw 'Release notes must contain content and closed code fences.' }
if ([Text.Encoding]::UTF8.GetByteCount($notes) -gt 16 * 1024) { throw 'Release notes exceed the application limit of 16 KiB.' }
$notes
