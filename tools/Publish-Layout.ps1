#Requires -Version 5.1
<#
.SYNOPSIS
    Builds an isolated win-x64 release-layout proof of concept for future Inno Setup packaging.
.DESCRIPTION
    Does not install, update, launch, sign, upload, or alter publish.ps1's portable output.
    OutputRoot/<version>/layout contains a native root launcher, current.json, and app-<version>.
    Requires a .NET 8-compatible SDK and the Windows C++/SDK tools for NativeAOT.
    Existing release destinations are never overwritten. Use a new OutputRoot to repeat a build.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\distribution')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\Fyltrix\Fyltrix.csproj'
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Select-Object -First 1 }
$pattern = '\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-([0-9A-Za-z-]+\.)*[0-9A-Za-z-]+)?\z'
if ($Version.Length -gt 64 -or $Version -cnotmatch $pattern) { throw 'Version must be a path-safe SemVer core with an optional prerelease (no build metadata).' }
if ($Version.Contains('-')) {
    foreach ($identifier in $Version.Substring($Version.IndexOf('-') + 1).Split('.')) {
        if ($identifier -cmatch '\A0[0-9]+\z') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
    }
}
$coreVersion = $Version.Split('-')[0]
foreach ($component in $coreVersion.Split('.')) {
    if ([int]$component -gt 65534) { throw 'Version components must fit Windows assembly/file metadata (0 through 65534).' }
}
$binaryVersion = "$coreVersion.0"

function Is-Within([string]$candidate, [string]$parent) {
    $candidate = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
    $parent = [IO.Path]::GetFullPath($parent).TrimEnd('\')
    return $candidate.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or $candidate.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$allowedWorkspaceOutput = Join-Path $root 'artifacts\distribution'
if ((Is-Within $OutputRoot $root) -and -not (Is-Within $OutputRoot $allowedWorkspaceOutput)) {
    throw 'Inside the checkout, output must stay under artifacts\distribution; the development copy is protected.'
}
foreach ($profile in @(
    (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'FileLens'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileLens')
)) {
    if ((Is-Within $OutputRoot $profile) -or (Is-Within $profile $OutputRoot)) { throw 'Output must not overlap existing application data.' }
}
# Do not follow junctions into protected folders, or put experiments inside an existing release/installation.
$ancestor = New-Object IO.DirectoryInfo($OutputRoot)
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Output paths must not traverse a junction or symbolic link.' }
    if ((Test-Path -LiteralPath (Join-Path $ancestor.FullName 'current.json')) -or (Test-Path -LiteralPath (Join-Path $ancestor.FullName 'Fyltrix.exe'))) {
        throw 'Output must not be inside an existing Fyltrix release or installed copy.'
    }
    $ancestor = $ancestor.Parent
}
$release = Join-Path $OutputRoot $Version
if (Test-Path -LiteralPath $release) { throw "The release destination already exists and will not be overwritten: $release" }
$layout = Join-Path $release 'layout'
$payload = Join-Path $layout "app-$Version"
$build = Join-Path $release '.build'
$launcherOut = Join-Path $build 'native-launcher'
[IO.Directory]::CreateDirectory($release) | Out-Null

# NativeAOT locates the C++ toolchain through vswhere. This affects only this script process.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$originalPath = $env:PATH
try {
    if (Test-Path -LiteralPath $vsInstaller) { $env:PATH = "$env:PATH;$vsInstaller" }
    Write-Host "Publishing isolated Fyltrix payload $Version..."
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 "-p:Version=$Version" "-p:AssemblyVersion=$binaryVersion" "-p:FileVersion=$binaryVersion" --artifacts-path $build -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application payload publish failed. The incomplete output is retained for diagnosis.' }

    Write-Host 'Publishing the native layout launcher...'
    & dotnet publish (Join-Path $root 'src\Fyltrix.Launcher\Fyltrix.Launcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:DebugType=none "-p:Version=$Version" "-p:AssemblyVersion=$binaryVersion" "-p:FileVersion=$binaryVersion" --artifacts-path $build -o $launcherOut --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher publish failed. No installer or existing application was changed.' }
}
finally { $env:PATH = $originalPath }

Copy-Item -LiteralPath (Join-Path $launcherOut 'Fyltrix.Launcher.exe') -Destination (Join-Path $layout 'Fyltrix.exe')
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $layout 'UPDATE_PROTOCOL'), "Fyltrix.Update/1`nFileLens.Profile/1`n", $utf8)
[IO.File]::WriteAllBytes((Join-Path $layout 'startup.lock'), [byte[]]@())
[IO.File]::WriteAllText((Join-Path $layout 'current.json'), (([ordered]@{ current = $Version; previous = $null } | ConvertTo-Json) + [Environment]::NewLine), $utf8)
$notes = @"
Fyltrix versioned release layout ($Version, win-x64)

Launch the root Fyltrix.exe. It forwards folder arguments to app-$Version\Fyltrix.exe.
Keep this whole layout together. It can be moved or extracted to another writable or read-only directory.
Settings remain in %AppData%\FileLens; caches remain in %LocalAppData%\FileLens.
WebView2 Runtime and optional preview codecs are still required separately.

This layout supports protocol-1 update handover, isolated health checks, and rollback/recovery.
Settings > Updates offers Manual only, Notify only, automatic download with manual installation,
and optional automatic download/install on safe exit. Notify only is the default; checks run
shortly after startup and every six hours while open. No service or startup registration is added.
Exit installation requires an explicit opt-in and a ready verified package, respects close/work
safeguards, and leaves the application closed. Manual Install and restart still asks for confirmation.
Managed updates require owner-pinned signing trust. Use the separate installer for first installation/bootstrap.
An interrupted transaction blocks startup until explicit authenticated recovery.
The ZIP checksum detects file changes; it is not a publisher signature or a trusted update manifest.
"@
[IO.File]::WriteAllText((Join-Path $layout 'LAYOUT.txt'), $notes, $utf8)
$zip = Join-Path $release "Fyltrix-$Version-win-x64.zip"
Compress-Archive -LiteralPath @((Join-Path $layout 'Fyltrix.exe'), (Join-Path $layout 'current.json'), (Join-Path $layout 'LAYOUT.txt'), (Join-Path $layout 'UPDATE_PROTOCOL'), (Join-Path $layout 'startup.lock'), $payload) -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), "$hash  $([IO.Path]::GetFileName($zip))`r`n", $utf8)
$metadata = [ordered]@{
    product = 'Fyltrix'; version = $Version; runtime = 'win-x64'; kind = 'versioned-layout-protocol1'
    protocolVersion = 1; profileContract = 1
    launcher = 'layout\Fyltrix.exe'; payload = "layout\app-$Version\Fyltrix.exe"
    sha256 = $hash; installerBuilt = $false; automaticInstallationEnabled = $false
}
[IO.File]::WriteAllText((Join-Path $release 'build-info.json'), ($metadata | ConvertTo-Json), $utf8)
Write-Host "Layout: $layout"
Write-Host "ZIP: $zip"
Write-Host "SHA-256: $hash"
