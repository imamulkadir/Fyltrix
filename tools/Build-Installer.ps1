#Requires -Version 5.1
<#
.SYNOPSIS
    Compile a separate per-user Inno Setup installer from a validated Stage 2 release directory.
.DESCRIPTION
    Compilation only. Never runs Setup, publishes, or modifies the source layout/development copy.
    Sandbox is the default; Production uses a separate permanent identity and is never installed by this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [ValidateSet('Sandbox','Production')][string]$Mode = 'Sandbox',
    [string]$OutputDirectory,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$layout = Join-Path $ReleaseDirectory 'layout'
$info = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'build-info.json') -Raw | ConvertFrom-Json
$version = [string]$info.version
if ($info.product -ne 'Fyltrix' -or $info.runtime -ne 'win-x64' -or $version.Length -gt 64 -or
    $version -cnotmatch '\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-([0-9A-Za-z-]+\.)*[0-9A-Za-z-]+)?\z') { throw 'Invalid Fyltrix release metadata.' }
if ($version.Contains('-')) {
    foreach ($part in $version.Substring($version.IndexOf('-')+1).Split('.')) {
        if ($part -cmatch '\A0[0-9]+\z') { throw 'Invalid numeric prerelease identifier.' }
    }
}
$core = $version.Split('-')[0].Split('.') | ForEach-Object { [int]$_ }
if (@($core | Where-Object { $_ -gt 65534 }).Count) { throw 'Version components exceed Windows file metadata limits.' }
$pointer = Get-Content -LiteralPath (Join-Path $layout 'current.json') -Raw | ConvertFrom-Json
if ($pointer.current -cne $version -or $null -ne $pointer.previous) { throw 'The input layout must be the exact standalone release being installed.' }
$zip = Join-Path $ReleaseDirectory "Fyltrix-$version-win-x64.zip"
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecar = ((Get-Content -LiteralPath ($zip + '.sha256') -Raw).Trim() -split '\s+')[0]
if ($hash -ne $sidecar -or $hash -ne $info.sha256) { throw 'The release ZIP/checksum/build record do not agree.' }

function Within([string]$Path, [string]$Parent) {
    $Path = [IO.Path]::GetFullPath($Path).TrimEnd('\'); $Parent = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    return $Path.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Parent+'\',[StringComparison]::OrdinalIgnoreCase)
}
# Compare the entire source layout with its ZIP; do not accidentally package test profiles or changed binaries.
# Inspect directories before opening payload files. Never follow a redirected source subtree.
$pending = New-Object 'Collections.Generic.Stack[string]'
$pending.Push($layout)
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    if (((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source layout junctions/symlinks are not allowed.' }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source layout junctions/symlinks are not allowed.' }
        if ($item.PSIsContainer) { $pending.Push($item.FullName) }
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
$files = @{}
try {
    foreach ($entry in $archive.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
        $file = [IO.Path]::GetFullPath((Join-Path $layout ($entry.FullName.Replace('/','\'))))
        if (-not (Within $file $layout) -or $files.ContainsKey($file)) { throw 'The ZIP contains an unsafe or duplicate path.' }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'A source layout file is missing.' }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'A source layout file differs from its release ZIP.' }
        $files[$file] = $true
    }
    $actual = @(Get-ChildItem -LiteralPath $layout -Recurse -File -Force)
    if ($actual.Count -ne $files.Count) { throw 'Unexpected files were added to the source layout.' }
    foreach ($file in $actual) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $files.ContainsKey($file.FullName)) { throw 'Invalid source layout file.' }
    }
} finally { $archive.Dispose() }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ReleaseDirectory "installer-$Mode" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Within $OutputDirectory $root) -and -not (Within $OutputDirectory (Join-Path $root 'artifacts\distribution'))) { throw 'Installer output inside the checkout must stay under artifacts\distribution.' }
if (Within $OutputDirectory $layout) { throw 'Installer output must be separate from the source layout.' }
foreach ($store in @((Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'FileLens'),(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileLens'))) {
    if ((Within $OutputDirectory $store) -or (Within $store $OutputDirectory)) { throw 'Installer output must not overlap user data.' }
}
$ancestor = New-Object IO.DirectoryInfo($OutputDirectory)
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Installer output paths must not traverse junctions.' }
    if ((Test-Path -LiteralPath (Join-Path $ancestor.FullName 'Fyltrix.exe')) -or (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.fyltrix-install.ini'))) { throw 'Installer output must not be inside an application copy.' }
    $ancestor = $ancestor.Parent
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Installer output already exists and will not be overwritten.' }
if (-not $IsccPath) {
    $IsccPath = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Inno Setup 6 ISCC.exe is required. Supply -IsccPath if necessary.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$id = if ($Mode -eq 'Sandbox') { 'Fyltrix.Sandbox.PerUser' } else { 'Fyltrix.PerUser' }
$marker = Join-Path $OutputDirectory 'installer-identity.ini'
[IO.File]::WriteAllText($marker, "[Installation]`r`nSchema=1`r`nAppId=$id`r`nMode=$Mode`r`n", $utf8)
$ms = ([uint32]$core[0] * 65536) + [uint32]$core[1]
$ls = [uint32]$core[2] * 65536
$binaryVersion = ($core -join '.') + '.0'
$arguments = @('/Qp',"/DAppVersion=$version","/DBinaryVersion=$binaryVersion","/DLayoutDir=$layout","/DMarkerFile=$marker","/DOutputDir=$OutputDirectory","/DVersionMS=$ms","/DVersionLS=$ls")
if ($Mode -eq 'Sandbox') { $arguments += '/DSandbox=1' }
$arguments += (Join-Path $root 'packaging\installer\Fyltrix.iss')
& $IsccPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed; partial output is retained for diagnosis.' }
$name = if ($Mode -eq 'Sandbox') { 'Fyltrix-Sandbox-Setup-win-x64.exe' } else { 'Fyltrix-Setup-win-x64.exe' }
$setup = Join-Path $OutputDirectory $name
$checksum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($setup + '.sha256'), "$checksum  $name`r`n", $utf8)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'installer-build.json'), (([ordered]@{product='Fyltrix';version=$version;mode=$Mode;appId=$id;sha256=$checksum;sourceZipSha256=$hash;automaticInstallationEnabled=$false;installedByBuildScript=$false} | ConvertTo-Json)), $utf8)
Write-Host "Installer compiled (not installed): $setup"
