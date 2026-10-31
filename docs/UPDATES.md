# Release discovery and verification — Stage 4

Stage 4 introduced the independent .NET 8 release-verification library and command-line tool documented here. Stage 5 adds [manual staging and safe handover](MANUAL_UPDATES.md); Stage 6 adds the [apply/health/rollback/recovery backend](APPLY_AND_RECOVERY.md). The WPF host now references the library for explicit worker/trial modes, but there is no end-user update UI, startup check, new settings key, background service or automatic installation. Portable publishing retains its behavior; existing profile identities and data are preserved.

## Discovery

`GitHubReleaseClient.CheckAsync` makes one unauthenticated GET to `https://api.github.com/repos/imamulkadir/Fyltrix/releases/latest`. It sends a descriptive User-Agent, GitHub's JSON Accept header and API-version header. An optional ETag supports conditional requests; a 304 returns `NotModified`, without inventing a release or an up-to-date result. No response or ETag is persisted to the user's settings.

The first implementation supports **stable releases only**, with optional `v` in GitHub tags. Versions use the existing layout grammar and Windows numeric bounds; build metadata is not supported in layout names. Installed prereleases compare according to SemVer precedence, including numeric prerelease identifiers too large for an integer. Equal/older releases are not offered as updates.

Metadata is limited to 256 KiB, 50 assets, and JSON depth 16. Drafts, prereleases, duplicate JSON properties, duplicate asset names, unsafe names, unfinished assets, invalid sizes, and URLs outside this repository/tag/asset are rejected. Notes remain untrusted plain text, limited to 16 KiB, and are never interpreted as HTML or commands.

Checks return distinct results for availability, equal/older versions, 404/no release, rate limiting, invalid metadata, network failure and timeout. A 404 can also mean that the repository is unavailable to this unauthenticated request. Rate-limit responses expose a retry time; there is no automatic retry. The future caller must honor retry times and decide when another manual check is appropriate. Cancellation requested by the caller propagates separately from the component's 15-second timeout.

## Signed manifest contract

Each release that can be authenticated needs these assets:

- `Fyltrix-release.json`
- `Fyltrix-release.json.sig`
- One or both supported packages: `Fyltrix-Setup-win-x64.exe` and `Fyltrix-<version>-win-x64.zip`.

Example manifest:

```json
{
  "schemaVersion": 1,
  "product": "Fyltrix",
  "version": "0.2.0",
  "files": [
    { "name": "Fyltrix-Setup-win-x64.exe", "size": 123456, "sha256": "<64 lowercase hexadecimal characters>" }
  ]
}
```

The signature file contains Base64 of an **ECDSA NIST P-256 / SHA-256 signature in IEEE P1363 format** (64 raw bytes). It signs the exact manifest bytes, not a reparsed/normalized JSON representation. Limits are 64 KiB for the manifest, 1 KiB for the signature file, and 1 GiB per package. Manifest/signature byte lengths must agree with release metadata. Unknown/duplicate manifest fields, other products or schema versions, version mismatches, unsigned package names, Sandbox installers, and asset-size mismatches are rejected.

Public keys are Base64 SubjectPublicKeyInfo values pinned in `ReleasePolicy.PinnedPublicKeys`. The curve OID must identify NIST P-256; key size alone is insufficient. Multiple pins permit planned key rotation. Keys supplied by a feed or manifest are never trusted.

**A dedicated Fyltrix production public key is now pinned**, with SPKI SHA-256 fingerprint `3665b71c7619dc19d986f94b7e46bd3b39618fb9c47bfa129254ae76fb955c0d`. The private key stays in a protected owner-local directory outside Git; its AES-256-encrypted PKCS#8 backup was restored and verified locally. No DynamicEye/test key is pinned. Independent off-device backup/password storage remains an owner step; see [SIGNING_SETUP.md](SIGNING_SETUP.md). Earlier builds with no pins still return `NoTrustedKey` before fetching manifest/signature assets and need an explicitly tested manual bootstrap installation. Do not send or commit private keys. Losing the signing key requires a manual trust/bootstrap recovery; rotation must ship the new public pin before relying on the new private key.

`VerifyManifestAsync` fetches only the two small manifest assets. Downloads follow at most four redirects, using HTTPS on `github.com`, `release-assets.githubusercontent.com`, or `objects.githubusercontent.com`. Reads are bounded even without Content-Length. Use `GitHubReleaseClient.CreateHttpClient()` for the production transport; injected clients must also disable automatic redirects. No package is fetched by this stage.

## Local package verification

`PackageVerifier.VerifyAsync` accepts only a package named in a verified manifest. Through one read-only file handle it checks exact length and streamed SHA-256, then examines content. It does not extract, execute, register, or modify a package.

- Setup must have valid DOS/PE header signatures and bounded PE offset. This is a basic content check, not Authenticode, Inno identity verification, or a compatibility test.
- ZIP must contain the expected root launcher, bounded `current.json` with this version and no previous pointer, and `app-<version>/Fyltrix.exe`. Other root files are limited to the existing `LAYOUT.txt`.
- ZIP entry count is limited to 20,000 and declared expanded bytes to 2 GiB. Rooted/traversal paths, alternate streams, Windows device names, trailing dots/spaces, case-insensitive duplicates, file/directory collisions, links, other payload versions and extra root/profile directories are rejected. Both slash conventions are accepted because the existing Windows packaging script can emit backslashes.

A successful result authenticates the supplied bytes to the explicitly trusted signing key. It is not permission to install them later: a future handover must revalidate the actual bytes it will use and perform shutdown, staging, compatibility and rollback checks. A signature alone does not prove release freshness or application health. The installed-version comparison remains a separate required gate.

## Developer/owner tool

Build without changing or publishing the application:

```powershell
dotnet build .\tools\Fyltrix.UpdateTool\Fyltrix.UpdateTool.csproj
$tool = '.\tools\Fyltrix.UpdateTool\bin\Debug\net8.0\Fyltrix.UpdateTool.dll'

# One live, read-only check; no release or installation is created.
dotnet $tool check 0.1.0

# Offline GitHub-shaped metadata fixture; no network access.
dotnet $tool check 0.1.0 'D:\Update tests\release.json'
```

Offline fixtures retain production-shaped GitHub URLs, but the command reads only the supplied local metadata. HTTP unit tests inject an in-memory handler; they do not listen on a network interface or touch the real clipboard/profile.

To sign already-built packages locally, supply an existing owner-managed PKCS#8 PEM private key and its matching Base64 SPKI public-key text file. The private key must be outside Git checkouts. The output directory must be new; existing files are not replaced. The tool hashes files, signs the manifest, and self-verifies its signature. It does not publish anything or establish application trust pins.

```powershell
dotnet $tool sign 0.2.0 'D:\Private signing\fyltrix.pem' `
    'D:\Private signing\public-key.txt' 'D:\Release output\new-signatures' `
    'D:\Release output\Fyltrix-Setup-win-x64.exe' `
    'D:\Release output\Fyltrix-0.2.0-win-x64.zip'

# Explicit test/owner trust override for this offline command only.
dotnet $tool verify 'D:\Update tests\release.json' `
    'D:\Release output\new-signatures\Fyltrix-release.json' `
    'D:\Release output\new-signatures\Fyltrix-release.json.sig' `
    'D:\Release output\Fyltrix-0.2.0-win-x64.zip' 'D:\Private signing\public-key.txt'
```

`verify` never fetches URLs and reports that its public key is an explicit owner-supplied test override. It cannot configure production pins or install a package. Successful checks/verification exit 0; rejected inputs/transport outcomes exit 2; usage errors exit 1. Discovery availability is metadata only: check the separate verification field before treating any release as authenticated.

For production release preparation, `sign-release` accepts the same arguments as `sign` but additionally requires the supplied public key to be pinned in this build. The [tag/changelog release workflow and owner completion helper](RELEASING.md) use this guarded mode. The existing `sign` command remains explicitly offline/test trust. Neither CLI command uploads or publishes; the completion helper performs those actions only when invoked by the owner, and publication requires its `-Publish` switch.

## Verification and next gate

```powershell
dotnet test .\tests\Fyltrix.Updates.Tests\Fyltrix.Updates.Tests.csproj
dotnet test .\tests\Fyltrix.Packaging.Tests\Fyltrix.Packaging.Tests.csproj
dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj
```

Tests generate disposable keys, sign fixtures, exercise the actual command-line tool, reject altered/wrong-key manifests and package bytes, validate unsafe ZIP cases, and cover transport limits, redirect policy, rate limits, ETags, timeouts and cancellation. Test keys never become production pins. Executed results and external evidence are recorded in [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).

Stage 5's manual staging and shutdown/handover boundary is documented in [MANUAL_UPDATES.md](MANUAL_UPDATES.md). Stage 6's backend and installed Sandbox health/rollback/recovery tests are documented in [APPLY_AND_RECOVERY.md](APPLY_AND_RECOVERY.md). Production installer rollout, Authenticode/SmartScreen, authenticated live-release verification with an owner key, end-user update UI and automatic installation remain gated.

Sources: [GitHub Releases API](https://docs.github.com/en/rest/releases/releases?apiVersion=latest), [Microsoft ECDSA verification](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-8.0), and the local reference's `docs/ADR/0034-signed-release-manifest.md` in the DynamicEye checkout. Fyltrix has its own protocol/product identity and trust root.
