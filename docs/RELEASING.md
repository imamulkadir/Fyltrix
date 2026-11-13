# Releasing Fyltrix

Fyltrix follows DyanamicEye's release model: **edit the changelog, prepare the version, push a tag, then sign locally**. GitHub builds/tests/packages and creates a draft. Your private signing key stays on your PC. A release is published only by your explicit action. Ordinary pushes to `master` do not create releases.

## One-time setup

- Commit and push the release tooling/workflow to `master` yourself. Enable GitHub Actions if repository policy currently disables it. The workflow needs permission to write release contents, OIDC identity and attestations. Package attestations must be available for the repository; public repositories support them. Do not bypass failed provenance verification.
- A dedicated Fyltrix ECDSA P-256 key has been created outside every checkout, with a verified encrypted local backup, and its Base64 SPKI public key is pinned in `ReleasePolicy.PinnedPublicKeys` (`src/Fyltrix.Updates/ReleaseMetadata.cs`). Complete the independent offline backup/password-storage step in [SIGNING_SETUP.md](SIGNING_SETUP.md). Never regenerate the existing key, reuse DynamicEye's key or pin a test key.
- The first production installation must be an explicitly tested, manually installed bootstrap build carrying that public key and the supported update protocol. The release automation does not bypass the remaining application-update integration/rollout gates.
- Local signing needs Windows PowerShell 5.1+, .NET 8 SDK, Git and a current GitHub CLI authenticated with `gh auth login`. CI uses `windows-2025` with .NET 8, C++/NativeAOT tools and Inno Setup 6. Authenticode/SmartScreen signing is separate from updater manifest signing.

## Prepare each release

**Current published release: 0.1.10**, confirmed as latest by read-only GitHub inspection on 2026-11-13. Historical draft, signature and public-feed verification records are retained in [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).

**Next candidate: 0.1.12, prepared locally on 2026-11-13.** Tag `v0.1.11` was pushed before its release-preparation commit (it points at `b4dd020`, which still declares 0.1.10); tag run 43405083520 failed at the first validation step (no `0.1.11` changelog section), so nothing was built, drafted or published. Per the no-moving-tags rule that tag is left as is and the release continues as 0.1.12, exactly as the failed `v0.1.7` was followed by 0.1.8. 0.1.12 contains the preview-engine recovery fix, the `fyltrix.example` preview origin and the FileLens → Fyltrix data-folder copy. Its `UPDATE_PROTOCOL` contract is unchanged (`Fyltrix.Update/1`, `FileLens.Profile/1`), so installed 0.1.10 updaters accept it. Owner steps: commit the release preparation, push, run the manual workflow on `master`, then tag `v0.1.12`. See [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).

**Previous candidate: 0.1.10, uploaded assets verified on 2026-11-05 (since published).** The stable draft has all six expected assets. Downloaded manifest/signature bytes match the locally signed files and GitHub sizes/digests exactly; the original four package/checksum identities and digests are unchanged. The verifier shipped in the ZIP accepts uploaded metadata and both packages with production pinning, and rejects tampering. The owner may now publish the existing stable draft as latest, then request live public-feed verification. Nothing has been published by the agent. See [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).

Before creating another tag, validate the pushed source on GitHub: **Actions → Release → Run workflow → master**. This manual run checks versions/notes, runs the tests, builds the ZIP and compiles Inno Setup, and uploads the build/test results as a workflow artifact. It does not create a tag, attest a tagged release, create a draft or publish anything. The manual option appears after the workflow change is committed and pushed to the default branch. Wait for it to pass before preparing/pushing the next release tag; an existing tag continues to identify its original source commit.

Collect user-facing changes under `CHANGELOG.md` → `Unreleased` during development. When ready, move the intended notes into a new stable version section, for example:

```markdown
## [Unreleased]

## [0.1.1] - 2026-11-01

### Fixed

- Describe the actual improvements included in this version.
```

Choose a new version for each release. Keep release notes below 16 KiB; missing, duplicate or empty sections are refused. Stable tags only (`vX.Y.Z`) are supported by this first Fyltrix pipeline; beta tags are refused rather than published accidentally.

From the checkout root, prepare the three application version fields together:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Set-ReleaseVersion.ps1 -Version 0.1.1
powershell -ExecutionPolicy Bypass -File .\tools\Test-Release.ps1 -Tag v0.1.1
```

Preparation requires the matching changelog section, refuses a duplicate/downgrade and changes only `Version`, `AssemblyVersion` and `FileVersion` in the application project. It never commits or tags. Packaging stamps the launcher/payload versions from the tag; shared component assembly identities remain stable.

Review the changes and perform your own Git operations. Example commands for the owner, **not commands executed by the coding agent**:

```powershell
git add CHANGELOG.md src/Fyltrix/Fyltrix.csproj
git commit -m "chore(release): prepare v0.1.1"
git push origin master
git tag -a v0.1.1 -m "Fyltrix 0.1.1"
git push origin v0.1.1
```

Commit/push all intended code and release-tooling changes before tagging. Keep the working directory clean. Do not move a published tag or replace an old release's packages; fix forward with a new version. The workflow rejects a tagged commit outside `master` history.

## What GitHub automates

The tag triggers `.github/workflows/release.yml`:

1. Verify tag, `master` ancestry, all application version fields and the matching changelog section.
2. Build/run application, updater and packaging tests, installer guards and release-tooling tests.
3. Build the self-contained versioned ZIP and native launcher, then compile the Production Inno installer. **Never execute Production Setup.** `publish.ps1 -Run` and its output are untouched.
4. Attest the ZIP/installer provenance with GitHub's keyless build identity.
5. Create a **draft**, using the changelog section as its notes, containing:
   - `Fyltrix-<version>-win-x64.zip` and its `.sha256`.
   - `Fyltrix-Setup-win-x64.exe` and its `.sha256`.

The workflow is restricted to this repository, uses actions pinned to full commit SHAs, has no private signing secret, and never publishes a release. Failed tests/builds/attestation prevent draft creation. Manual runs on `master` validate/build only; tag pushes alone run provenance and draft creation. Build files/test reports are retained for seven days as workflow artifacts, including available reports after a failed step. It does not install or update an application on your PC. Re-running against an existing draft refuses to overwrite it; inspect the failure instead of deleting tags or clobbering files.

## Sign and optionally publish

After the workflow succeeds, run the owner-only helper from the clean `master` checkout at the exact tagged commit:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Complete-Release.ps1 `
    -Tag v0.1.1 -KeyPath 'D:\Private keys\fyltrix.pem' `
    -PublicKeyPath 'D:\Private keys\fyltrix-public.txt' -Publish
```

This is the second command stage you may remember from DyanamicEye. The helper:

- Checks clean tagged source, matching remote tag and an existing stable draft with the exact expected assets.
- Downloads packages/checksums into a new retained temporary workspace. Verifies hashes plus the expected GitHub repository, release workflow, tag and source commit in the build attestations.
- Builds the existing standalone tool from that source and invokes `sign-release`. It refuses an unpinned public key or mismatching/private key inside a checkout. Offline `sign` remains available for explicit test trust, but is not used by release completion.
- Uploads `Fyltrix-release.json` and `Fyltrix-release.json.sig` without overwriting existing assets.
- Publishes as latest **only** when you supplied `-Publish`.

Omit `-Publish` to keep the signed release as a draft for review. After reviewing, publish it through GitHub's release page. Do not rerun completion to replace an existing manifest: ECDSA signatures can differ on each signing. If an upload is interrupted, retain the workspace and review the partial draft; nothing automatically deletes or overwrites its assets.

Keep the temporary verification workspace private; clean it manually when no longer needed. Private key files are not copied into it or uploaded. Production completion refuses unpinned keys; do not use the offline test-key override to work around that gate.

## Verification status and remaining gates

The owner-run `v0.1.5` tag workflow succeeded. Its actual draft ZIP and installer passed size/checksum/asset-digest and exact repository/workflow/tag/commit provenance checks. A manifest was prepared locally with the existing pinned Fyltrix key; the update library shipped inside that ZIP verified it and both packages using its default production trust. Modified manifest/package bytes were rejected. This verification used an explicit offline preview of published metadata while the release was still a draft; subsequent owner publication and live checks are recorded below. No Production Setup was run. Evidence and owner-only upload/publication/manual-installation steps are recorded in [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md). The full local/hosted test and earlier isolated installer lifecycle results are recorded there separately; these do not certify an actual production installation or in-app update.

This automates release production. Stage 7B connects the [apply/recovery backend](APPLY_AND_RECOVERY.md) to the [manual in-app workflow](MANUAL_UPDATES.md) in later source. Notification-plan Stages 1–3 add Settings > Updates, checks/notices, optional verified downloads, saved-package revalidation and opt-in installation on safe normal exit without restart. Notify only remains the default; Manual only disables scheduling. Manual installation asks for confirmation; saving Install on exit supplies permission only for the guarded exit workflow. Local regression and isolated Sandbox lifecycle tests pass; owner review/CI, a new bootstrap release and actual production rollout remain separate gates. Already published `v0.1.5` does not gain these source changes remotely. Do not create a release tag merely because a stage's validation is green; review and approve release preparation separately.

The owner subsequently uploaded the manifest/signature and published `v0.1.5`. Read-only public-feed checks report an authenticated update for installed version `0.1.4` and `UpToDate` for `0.1.5`. Manual production-installer/application review remains an owner step; Stage 7B is later source work; published `v0.1.5` does not contain its new controls.

GitHub references: [tag-triggered workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#push), [draft creation with existing tags](https://cli.github.com/manual/gh_release_create), [provenance verification](https://cli.github.com/manual/gh_attestation_verify), and [Windows runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md).

## Agent-assisted release runbook (who does what)

Written after 0.1.10–0.1.12 so a coding agent can resume a release quickly. **Owner:** every Git mutation (commit, push, tag), GitHub Actions runs, uploading the two signed files, and publishing. **Agent:** preparation, local verification, read-only GitHub checks, and local signing with the owner's key. The agent never uploads, publishes, tags, commits or pushes.

### 1. Prepare (agent)

1. Confirm the published latest release: `gh release list -R imamulkadir/Fyltrix -L 3`. The next version is the next unused `X.Y.Z`; check the remote tags too (`git ls-remote --tags origin 'v0.1.*'`), because a failed tag is never reused.
2. Move the `CHANGELOG.md` → `Unreleased` notes into `## [X.Y.Z] - YYYY-MM-DD`.
3. Set the version and validate the notes:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\tools\Set-ReleaseVersion.ps1 -Version X.Y.Z
   powershell -ExecutionPolicy Bypass -File .\tools\Test-Release.ps1 -Tag vX.Y.Z
   ```
4. Optional local CI replica (about 10–15 minutes; the same steps as `.github/workflows/release.yml`):
   - `dotnet test -c Release` for `tests/Fyltrix.Tests`, `tests/Fyltrix.Updates.Tests` and `tests/Fyltrix.Packaging.Tests`
   - `python -m unittest discover -s tests/installer -p test_builder.py`
   - `python -m unittest discover -s tests/release -p 'test_*.py'`
   - `.\tools\Publish-Layout.ps1 -Version X.Y.Z`
   - `.\tools\Build-Installer.ps1 -ReleaseDirectory artifacts\distribution\X.Y.Z -Mode Production`, which **compiles only and never runs Setup**

   Output goes to `artifacts/distribution/` (ignored by Git). Never use `publish.ps1 -Run`.
5. Update the "Current published release / Next candidate" lines above and add a dated entry to `PROJECT_PROGRESS.md` and `IMPLEMENTATION_STATUS.md`.

### 2. Commit, validate, tag (owner) — order matters

```powershell
git add CHANGELOG.md src/Fyltrix/Fyltrix.csproj docs/RELEASING.md docs/PROJECT_PROGRESS.md docs/IMPLEMENTATION_STATUS.md
git commit -m "chore(release): prepare vX.Y.Z"
git push origin master
# Actions → Release → Run workflow → master; wait for green on THIS commit
git tag -a vX.Y.Z -m "Fyltrix X.Y.Z"
git push origin vX.Y.Z
```

**Pitfall (happened with v0.1.11):** tagging before the preparation commit was pushed. The tag then points at a commit whose `Fyltrix.csproj`/`CHANGELOG.md` still declare the previous version. The tag run fails at "Validate tag, master ancestry, versions and changelog" (`Get-ReleaseNotes.ps1`), and nothing is built or drafted. Recovery: leave the tag, prepare the **next** version and tag again. Do not delete or move tags (the same was done after `v0.1.7`).

Before tagging, the agent can confirm that the green manual run's `headSha` equals the preparation commit:

```powershell
gh run list -R imamulkadir/Fyltrix -w Release -L 4 --json databaseId,event,headBranch,headSha,status,conclusion
```

### 3. Verify the draft (agent, read-only)

After the tag run passes:

- **Run steps:** `gh run view <run id> -R imamulkadir/Fyltrix --json jobs` shows all steps succeeded.
- **Assets:** `gh release view vX.Y.Z -R imamulkadir/Fyltrix --json isDraft,assets` shows a stable draft with exactly `Fyltrix-X.Y.Z-win-x64.zip`, `Fyltrix-Setup-win-x64.exe` and their two `.sha256` files.
- **Checksums:** download into a scratch folder (`gh release download vX.Y.Z -R imamulkadir/Fyltrix -D <folder>`). The `sha256sum` of each package must equal its `.sha256` file and GitHub's asset digest.
- **Provenance:** for each package, run
  ```powershell
  gh attestation verify <file> -R imamulkadir/Fyltrix --signer-workflow imamulkadir/Fyltrix/.github/workflows/release.yml --source-ref refs/tags/vX.Y.Z --source-digest <tag commit> --format json
  ```
  The exit code is 0. Without `--format json`, the success text may not match a simple grep.
- **ZIP contents:**
  - `UPDATE_PROTOCOL` is exactly `Fyltrix.Update/1\nFileLens.Profile/1\n` (36 bytes). Changing it would make every installed updater refuse the package.
  - `current.json` names the new version.
  - `app-X.Y.Z/Fyltrix.dll` ProductVersion is `X.Y.Z+<tag commit>`.
- **Notes:** the draft body equals the changelog section (`gh release view vX.Y.Z --json body`).

### 4. Sign locally, without uploading (agent, with the owner's key)

The pinned key lives in `%LocalAppData%\Fyltrix-Signing` (`fyltrix-release-private.pem`, `fyltrix-release-public.txt`); see [SIGNING_SETUP.md](SIGNING_SETUP.md). Never copy, print or upload the private key.

The owner prefers to upload and publish manually. The agent therefore runs the sign-only adaptation of `Complete-Release.ps1` below from the scratch folder, and hands over:

- the **signed folder path**, `%TEMP%\Fyltrix-release-X.Y.Z-<guid>\signed`, containing `Fyltrix-release.json` and `Fyltrix-release.json.sig`
- the **draft link**, from `gh release view vX.Y.Z -R imamulkadir/Fyltrix --json url`; draft URLs look like `.../releases/tag/untagged-<id>`

Notes:

- Sign **once**. ECDSA signatures differ on every run, so never re-sign to replace uploaded metadata.
- The checkout may hold uncommitted verification notes. The script allows only the two progress docs, and builds the signing tool from a clean `git archive` of the tag.
- In Windows PowerShell 5.1 `git` may not be on PATH (use the `Program Files` fallbacks), and `H`/`G`-style short function names can collide with built-in aliases (`H` = `Get-History`).

```powershell
#Requires -Version 5.1
# Local-only adaptation of tools/Complete-Release.ps1 for one release: same source/tag/draft/checksum/provenance/signing
# gates; builds the signing tool from a clean export of the tag (the checkout has uncommitted docs only); NEVER uploads
# or publishes. The owner uploads the two signed files.
param([Parameter(Mandatory=$true)][string]$Tag, [string]$Root = 'D:\Tools\Fyltrix')
$ErrorActionPreference = 'Stop'
$repo = 'imamulkadir/Fyltrix'
$version = $Tag.TrimStart('v')
$signing = Join-Path $env:LOCALAPPDATA 'Fyltrix-Signing'
$KeyPath = Join-Path $signing 'fyltrix-release-private.pem'
$PublicKeyPath = Join-Path $signing 'fyltrix-release-public.txt'
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) { throw 'Signing key files missing.' }
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { $git = Join-Path $env:ProgramFiles 'Git\cmd\git.exe' }
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path -LiteralPath $git) -or -not (Test-Path -LiteralPath $gh)) { throw "git/gh not found: $git / $gh" }
function Invoke-RGit { $o = & $git -C $Root @args; if ($LASTEXITCODE -ne 0) { throw "git failed: $args" }; $o }
function Invoke-RGh { $o = & $gh @args; if ($LASTEXITCODE -ne 0) { throw "gh failed: $args" }; $o }

if ((Invoke-RGit branch --show-current) -cne 'master') { throw 'Run from master.' }
$commit = Invoke-RGit rev-parse --verify "$Tag^{commit}"
if ((Invoke-RGit rev-parse HEAD) -cne $commit) { throw 'HEAD must equal the release tag commit.' }
$dirty = @(Invoke-RGit status --porcelain --untracked-files=all)
$allowedDirty = @(' M docs/IMPLEMENTATION_STATUS.md', ' M docs/PROJECT_PROGRESS.md')
foreach ($line in $dirty) { if ($line -cnotin $allowedDirty) { throw "Unexpected local change: $line" } }
$remote = @(Invoke-RGit ls-remote origin "refs/tags/$Tag^{}")
if ($remote.Count -ne 1 -or ($remote[0] -split '\s+')[0] -cne $commit) { throw 'Remote tag must resolve to the local tag commit.' }
$release = & (Join-Path $Root 'tools\Test-Release.ps1') -Tag $Tag
if ($release.Version -cne $version) { throw 'Version mismatch.' }

$draft = Invoke-RGh release view $Tag --repo $repo --json 'isDraft,isPrerelease,assets' | ConvertFrom-Json
if (-not $draft.isDraft -or $draft.isPrerelease) { throw 'Only an existing stable draft can be signed.' }
$zipName = "Fyltrix-$version-win-x64.zip"
$names = @($zipName, 'Fyltrix-Setup-win-x64.exe')
$allowed = @($names + @($names | ForEach-Object { "$_.sha256" }))
foreach ($asset in $draft.assets) { if ($asset.name -cnotin $allowed) { throw 'Draft already has manifest or unexpected assets; nothing is overwritten.' } }
foreach ($name in $allowed) { if (@($draft.assets | Where-Object { $_.name -ceq $name }).Count -ne 1) { throw 'Draft missing a package/checksum.' } }

$work = Join-Path ([IO.Path]::GetTempPath()) ('Fyltrix-release-' + $version + '-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
Write-Host "WORKSPACE $work"
$draft | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $work 'draft-before.json')
foreach ($name in $names) {
    Invoke-RGh release download $Tag --repo $repo --dir $work --pattern $name --pattern "$name.sha256" | Out-Null
    $file = Join-Path $work $name
    $sidecar = [IO.File]::ReadAllText($file + '.sha256').Trim()
    if ($sidecar -cnotmatch ('\A([0-9a-f]{64})  ' + [regex]::Escape($name) + '\z')) { throw 'Invalid checksum file.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches[1]) { throw 'Checksum mismatch.' }
    Invoke-RGh attestation verify $file --repo $repo --signer-workflow "$repo/.github/workflows/release.yml" --source-ref "refs/tags/$Tag" --source-digest $commit --deny-self-hosted-runners | Out-Null
    Write-Host "VERIFIED $name"
}

# Clean tagged source for the signing tool (identical to a clean checkout at the tag).
$source = Join-Path $work 'source'
[IO.Directory]::CreateDirectory($source) | Out-Null
$archive = Join-Path $work 'source.zip'
Invoke-RGit archive --format=zip -o $archive $Tag | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $source
& dotnet build (Join-Path $source 'tools\Fyltrix.UpdateTool\Fyltrix.UpdateTool.csproj') -c Release --artifacts-path (Join-Path $work 'build') -o (Join-Path $work 'tool') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Signing tool build failed.' }
& dotnet (Join-Path $work 'tool\Fyltrix.UpdateTool.dll') sign-release $version $KeyPath $PublicKeyPath (Join-Path $work 'signed') (Join-Path $work $zipName) (Join-Path $work 'Fyltrix-Setup-win-x64.exe')
if ($LASTEXITCODE -ne 0) { throw 'Signing refused.' }

$again = Invoke-RGh release view $Tag --repo $repo --json 'isDraft,isPrerelease,assets' | ConvertFrom-Json
if (($draft.assets | ConvertTo-Json -Depth 6 -Compress) -cne ($again.assets | ConvertTo-Json -Depth 6 -Compress)) { throw 'Draft assets changed during signing.' }
Write-Host "SIGNED $(Join-Path $work 'signed')"
Get-ChildItem (Join-Path $work 'signed') | ForEach-Object { "{0}  {1} bytes  sha256 {2}" -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
Get-Content (Join-Path $work 'signed\Fyltrix-release.json')
```

Run it as:

```powershell
powershell -ExecutionPolicy Bypass -File <scratch>\Sign-ReleaseLocal.ps1 -Tag vX.Y.Z
```

### 5. Upload and publish (owner)

Open the draft link, then **Edit**. Attach the two signed files without touching the four existing ones, check that 6 assets are listed, keep "Set as the latest release" ticked, and **Publish**. Or upload from the command line, then publish on the page:

```powershell
gh release upload vX.Y.Z <signed>\Fyltrix-release.json <signed>\Fyltrix-release.json.sig -R imamulkadir/Fyltrix
```

### 6. After publication (agent, read-only)

- Confirm the uploaded metadata bytes equal the local signed files.
- Confirm the release is marked latest.
- Confirm installed versions see the update, for example with the signing tool's `check` command against the public feed for the previous version.

Owner acceptance: update the previous installed version in-app, then check that settings are preserved and that previews and Compare work.
