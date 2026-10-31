<#
  Builds Fyltrix into a portable folder: artifacts\Fyltrix\Fyltrix.exe
  No admin rights needed. If the .NET 8 SDK isn't installed, it is downloaded
  per-user into %LOCALAPPDATA%\Microsoft\dotnet (Microsoft's official script).

  Usage (from this folder, in PowerShell):
      powershell -ExecutionPolicy Bypass -File .\publish.ps1
      powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SingleFile   # one Fyltrix.exe instead of a folder
      powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Run          # build, then start it
#>
param(
    [switch]$SingleFile,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Fyltrix\Fyltrix.csproj'
$out = Join-Path $root 'artifacts\Fyltrix'

function Find-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match '^(8|9|10)\.') { return $c }
        }
    }
    return $null
}

$dotnet = Find-Dotnet
if (-not $dotnet) {
    Write-Host 'No .NET 8+ SDK found. Installing .NET 8 SDK for this user only (no admin)...' -ForegroundColor Yellow
    $installDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    & $script -Channel 8.0 -InstallDir $installDir -NoPath
    $dotnet = Join-Path $installDir 'dotnet.exe'
}
Write-Host "Using $dotnet" -ForegroundColor DarkGray

$resolvedRoot = [IO.Path]::GetFullPath($root)
$resolvedOut = [IO.Path]::GetFullPath($out)
if (-not $resolvedOut.StartsWith($resolvedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish output must stay inside the project root.'
}
if (Test-Path -LiteralPath $resolvedOut) { Remove-Item -LiteralPath $resolvedOut -Recurse -Force }

$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out, '-p:Platform=x64')
if ($SingleFile) {
    $publishArgs += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
}
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE). Copy the errors above and send them over." }

Write-Host ''
Write-Host "Done: $out\Fyltrix.exe" -ForegroundColor Green
if ($Run) { Start-Process (Join-Path $out 'Fyltrix.exe') }
