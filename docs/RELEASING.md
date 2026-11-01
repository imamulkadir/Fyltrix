# Releasing Fyltrix

Fyltrix follows DyanamicEye's release model: **edit the changelog, prepare the version, push a tag, then sign locally**. GitHub builds/tests/packages and creates a draft. Your private signing key stays on your PC. A release is published only by your explicit action. Ordinary pushes to `master` do not create releases.

## One-time setup

- Commit and push the release tooling/workflow to `master` yourself. Enable GitHub Actions if repository policy currently disables it. The workflow needs permission to write release contents, OIDC identity and attestations. Package attestations must be available for the repository; public repositories support them. Do not bypass failed provenance verification.
- A dedicated Fyltrix ECDSA P-256 key has been created outside every checkout, with a verified encrypted local backup, and its Base64 SPKI public key is pinned in `ReleasePolicy.PinnedPublicKeys` (`src/Fyltrix.Updates/ReleaseMetadata.cs`). Complete the independent offline backup/password-storage step in [SIGNING_SETUP.md](SIGNING_SETUP.md). Never regenerate the existing key, reuse DynamicEye's key or pin a test key.
- The first production installation must be an explicitly tested, manually installed bootstrap build carrying that public key and the supported update protocol. The release automation does not bypass the remaining application-update integration/rollout gates.
- Local signing needs Windows PowerShell 5.1+, .NET 8 SDK, Git and a current GitHub CLI authenticated with `gh auth login`. CI uses `windows-2025` with .NET 8, C++/NativeAOT tools and Inno Setup 6. Authenticode/SmartScreen signing is separate from updater manifest signing.

## Prepare each release

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

This automates release production. Stage 7B connects the [apply/recovery backend](APPLY_AND_RECOVERY.md) to the [manual in-app workflow](MANUAL_UPDATES.md) in later source. Notification-plan Stages 1 and 2 add Settings > Updates, preference-controlled checks/notices, optional verified automatic download and saved-package revalidation after restart. Notify only remains the default; Manual only disables scheduling. Installation always requires user confirmation; install-on-exit remains unavailable. Local regression and isolated native startup tests pass; owner review/CI, a new bootstrap release and actual production rollout remain separate gates. Already published `v0.1.5` does not gain these source changes remotely. Stage 3 requires separate approval before implementation; do not create a release tag merely because a stage's validation is green.

The owner subsequently uploaded the manifest/signature and published `v0.1.5`. Read-only public-feed checks report an authenticated update for installed version `0.1.4` and `UpToDate` for `0.1.5`. Manual production-installer/application review remains an owner step; Stage 7B is later source work; published `v0.1.5` does not contain its new controls.

GitHub references: [tag-triggered workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#push), [draft creation with existing tags](https://cli.github.com/manual/gh_release_create), [provenance verification](https://cli.github.com/manual/gh_attestation_verify), and [Windows runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md).
