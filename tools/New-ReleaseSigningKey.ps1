#Requires -Version 5.1
<#
.SYNOPSIS
    Create a dedicated Fyltrix signing key and encrypted backup in NEW external directories.
.DESCRIPTION
    No Git writes, pin changes, uploads or releases. The .NET 8 owner tool restricts
    both directories to this account/SYSTEM before writing secrets and verifies restore.
    Copy the encrypted backup off-device and store its recovery password separately.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PrimaryDirectory,
    [Parameter(Mandatory=$true)][string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Fyltrix.UpdateTool\Fyltrix.UpdateTool.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Signing tool build failed.' }
& dotnet (Join-Path $PSScriptRoot 'Fyltrix.UpdateTool\bin\Release\net8.0\Fyltrix.UpdateTool.dll') create-signing-key $PrimaryDirectory $BackupDirectory
if ($LASTEXITCODE -ne 0) { throw 'Signing setup refused or incomplete. Keep any generated files for review; never overwrite an existing key.' }
