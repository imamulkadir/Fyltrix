# Fyltrix release-signing setup

The owner authorized creation of a dedicated Fyltrix ECDSA P-256 signing key on 2026-11-01. Its public key is pinned in `src/Fyltrix.Updates/ReleaseMetadata.cs`; no private key, recovery password or test key is stored in Git or supplied to GitHub Actions.

Public-key fingerprint (SHA-256 of SPKI):

```text
3665b71c7619dc19d986f94b7e46bd3b39618fb9c47bfa129254ae76fb955c0d
```

## Owner-local files

Primary directory: `%LocalAppData%\Fyltrix-Signing`.

- `fyltrix-release-private.pem`: existing signing tool's PKCS#8 private key. Keep private; never upload or commit it.
- `fyltrix-release-public.txt`: Base64 SPKI public key; safe to share and compare with the source pin.
- `backup-recovery-password.txt`: random 256-bit recovery password. Store it separately in your private password manager/offline recovery storage. Never paste it into chat.

Owner-selected encrypted backup directory: `D:\Fyltrix-Key-Backups`.

- `fyltrix-release-private.encrypted.pem`: password-encrypted PKCS#8, AES-256-CBC with PBKDF2-SHA256, 600,000 iterations.
- `fyltrix-release-public.txt` and `RESTORE.txt`.

Both directories were created with protected Windows permissions for the current account and SYSTEM before secret bytes were written. Files are created exclusively; existing signing/backup destinations are never overwritten. Git checkouts, application copies, legacy profile data, links and overlapping destinations are refused. A failed setup retains any generated files for review rather than deleting an irreplaceable key.

The encrypted backup was decrypted locally and verified by its public key and a random signature challenge. This proves a local restore with the recovery password. **An off-device backup has not been made or verified.** A second directory/drive letter on this PC does not establish protection against loss of the PC.

## Complete your independent backup

1. Copy the encrypted backup directory to your chosen offline external drive or independent backup storage. Keep the plaintext private key on this PC in its protected directory.
2. Save the recovery password separately in your private password manager or offline recovery storage. The local password file alone is not disaster recovery.
3. Verify the copied encrypted file's SHA-256 matches the original. Recovery needs both the encrypted PKCS#8 and its password. On restoration, import with .NET's `ECDsa.ImportFromEncryptedPem`, check the exported SPKI fingerprint above, and restrict the restored private-key file before use. The creation tool exercises that import/sign/verify sequence without exposing the key.

No key/password needs to be sent to the coding agent or GitHub. The [release completion helper](RELEASING.md) uses only local file paths:

```powershell
$signing = Join-Path $env:LOCALAPPDATA 'Fyltrix-Signing'
powershell -ExecutionPolicy Bypass -File .\tools\Complete-Release.ps1 `
    -Tag v0.1.1 -KeyPath (Join-Path $signing 'fyltrix-release-private.pem') `
    -PublicKeyPath (Join-Path $signing 'fyltrix-release-public.txt') -Publish
```

This is a future owner command after integration/testing, source commit, tag push and successful draft build. It was not executed during setup. The current application's version remains unchanged and no production installation was replaced.

## Creation and rotation

`tools/New-ReleaseSigningKey.ps1` wraps the existing standalone .NET 8 owner tool's `create-signing-key <new-primary-directory> <new-backup-directory>` command. It prints only public metadata, paths and verification flags. It never changes source pins, commits, uploads or publishes.

**Do not run it again for the current key.** Future rotation needs a new protected directory, deliberate review and a release that ships the new public pin before signing exclusively with the new key. Losing a trusted private key can require users to install a new bootstrap build manually. Backups are not deleted by builds, updates or uninstall.

Pinning authenticates signed packages; it does not enable automatic installation or add the in-app update UI. Existing portable/installed builds retain their existing bytes and need the separately tested manual bootstrap path to acquire the new trust.

Reference APIs: [.NET PKCS#8 export/import](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa?view=net-8.0) and [directory creation with Windows ACLs](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions?view=net-8.0). Executed checks and remaining gates: [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).
