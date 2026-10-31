#Requires -Version 5.1
<#
.SYNOPSIS
    Owner-only completion of an existing tag-built draft. Never creates tags or commits.
.DESCRIPTION
    Checks exact tagged source, package checksums and GitHub provenance, signs locally
    with the existing CLI and a pinned Fyltrix key, then uploads the manifest.
    Publication requires the explicit -Publish switch. Never executes Setup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$KeyPath,
    [Parameter(Mandatory=$true)][string]$PublicKeyPath,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = & (Join-Path $PSScriptRoot 'Test-Release.ps1') -Tag $Tag
$version = $release.Version
$repo = 'imamulkadir/Fyltrix'
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { $git = Join-Path $env:ProgramFiles 'Git\cmd\git.exe' }
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path -LiteralPath $git) -or -not (Test-Path -LiteralPath $gh)) { throw 'Git and GitHub CLI are required. Authenticate gh first.' }
$KeyPath = [IO.Path]::GetFullPath($KeyPath); $PublicKeyPath = [IO.Path]::GetFullPath($PublicKeyPath)
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) { throw 'Provide existing Fyltrix signing key files. Never reuse the DynamicEye key.' }
function Invoke-ReleaseGit {
    $output = & $git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw 'Git release-source validation failed.' }
    $output
}
function Invoke-ReleaseGh {
    $output = & $gh @args
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release operation failed; retained workspace contains diagnostic evidence.' }
    $output
}
if ((Invoke-ReleaseGit branch --show-current) -cne 'master') { throw 'Run from master.' }
if (Invoke-ReleaseGit status --porcelain --untracked-files=all) { throw 'Commit/review local changes before signing a release. The script never commits.' }
$commit = Invoke-ReleaseGit rev-parse --verify "$Tag^{commit}"
if ($commit -cnotmatch '\A[0-9a-f]{40}\z' -or (Invoke-ReleaseGit rev-parse HEAD) -cne $commit) { throw 'HEAD must equal the existing release tag. Use the matching committed source.' }
$remote = @(Invoke-ReleaseGit ls-remote origin "refs/tags/$Tag" "refs/tags/$Tag^{}")
$resolved = @($remote | Where-Object { $_ -match '\^\{\}$' })
if ($resolved.Count -eq 0) { $resolved = $remote }
if ($resolved.Count -ne 1 -or ($resolved[0] -split '\s+')[0] -cne $commit) { throw 'Remote tag must resolve to the exact local commit.' }
$draft = Invoke-ReleaseGh release view $Tag --repo $repo --json 'isDraft,isPrerelease,assets' | ConvertFrom-Json
if (-not $draft.isDraft -or $draft.isPrerelease) { throw 'Only an existing stable draft can be completed. Published releases are never replaced.' }
$zipName = "Fyltrix-$version-win-x64.zip"
$names = @($zipName, 'Fyltrix-Setup-win-x64.exe')
$allowed = @($names + @($names | ForEach-Object { "$_.sha256" }))
$manifestNames = @('Fyltrix-release.json','Fyltrix-release.json.sig')
foreach ($asset in $draft.assets) { if ($asset.name -cnotin $allowed) { throw 'Draft has existing manifest or unexpected assets; review them before signing. Nothing is overwritten.' } }
foreach ($name in $allowed) { if (@($draft.assets | Where-Object { $_.name -ceq $name }).Count -ne 1) { throw 'Draft is missing a required package/checksum.' } }
$work = Join-Path ([IO.Path]::GetTempPath()) ('Fyltrix-release-' + $version + '-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
Write-Host "Release verification workspace (retained): $work"
foreach ($name in $names) {
    Invoke-ReleaseGh release download $Tag --repo $repo --dir $work --pattern $name --pattern "$name.sha256" | Out-Null
    $file = Join-Path $work $name
    $sidecar = [IO.File]::ReadAllText($file + '.sha256').Trim()
    if ($sidecar -cnotmatch ('\A([0-9a-f]{64})  ' + [regex]::Escape($name) + '\z')) { throw 'Invalid package checksum file.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches[1]) { throw 'Downloaded package checksum mismatch.' }
    Invoke-ReleaseGh attestation verify $file --repo $repo --signer-workflow "$repo/.github/workflows/release.yml" --source-ref "refs/tags/$Tag" --source-digest $commit --deny-self-hosted-runners | Out-Null
}
# Build only the existing standalone tool. Private signing keys are never supplied to CI.
& dotnet build (Join-Path $root 'tools\Fyltrix.UpdateTool\Fyltrix.UpdateTool.csproj') -c Release --artifacts-path (Join-Path $work 'build') -o (Join-Path $work 'tool') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Signing tool build failed.' }
& dotnet (Join-Path $work 'tool\Fyltrix.UpdateTool.dll') sign-release $version $KeyPath $PublicKeyPath (Join-Path $work 'signed') (Join-Path $work $zipName) (Join-Path $work 'Fyltrix-Setup-win-x64.exe')
if ($LASTEXITCODE -ne 0) { throw 'Signing refused. Configure a dedicated pinned Fyltrix public key through a reviewed bootstrap release first.' }
# Recheck draft state and hashes just before any remote mutation.
$again = Invoke-ReleaseGh release view $Tag --repo $repo --json 'isDraft,isPrerelease,assets' | ConvertFrom-Json
if (-not $again.isDraft -or $again.isPrerelease) { throw 'Release state changed; refusing upload.' }
if (($draft.assets | ConvertTo-Json -Depth 6 -Compress) -cne ($again.assets | ConvertTo-Json -Depth 6 -Compress)) { throw 'Draft assets changed during verification.' }
foreach ($name in $manifestNames) {
    $local = Join-Path $work "signed\$name"
    Invoke-ReleaseGh release upload $Tag $local --repo $repo | Out-Null
}
if ($Publish) {
    Invoke-ReleaseGh release edit $Tag --repo $repo --draft=false --latest | Out-Null
    Write-Host "Published $Tag."
} else { Write-Host "Signed assets uploaded. $Tag remains a draft for your review." }
