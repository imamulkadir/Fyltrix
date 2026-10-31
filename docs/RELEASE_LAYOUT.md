# Isolated release layout — Stage 2

This is a packaging/launcher proof of concept for the future Inno Setup installer. It does not install or update Fyltrix, register shortcuts, change startup behavior, migrate data, or publish anything. The normal application project and `publish.ps1 -Run` workflow remain unchanged.

## Build

On Windows x64, use a .NET 8-compatible SDK plus the Visual Studio C++ build tools and Windows SDK for NativeAOT. The launcher uses only the .NET libraries; there is no third-party update framework.

From the checkout root, in Windows PowerShell 5.1 or newer:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Publish-Layout.ps1

# Optional version and separate experimental output location:
powershell -ExecutionPolicy Bypass -File .\tools\Publish-Layout.ps1 `
    -Version 0.1.1-preview.1 -OutputRoot 'D:\Fyltrix packaging experiments'
```

Without `-Version`, the script reads the existing application project's Version property. Supported layout versions use a three-part numeric core and an optional SemVer prerelease suffix, at most 64 characters. Numeric core components must fit Windows assembly/file versions (0–65534). Build metadata (`+...`) is not supported in directory names. The package build stamps the requested version into the isolated binaries; it does not edit the source project's development version.

Default output:

```text
artifacts/distribution/<version>/
  layout/
    Fyltrix.exe                  NativeAOT launcher; stable future shortcut path
    current.json                 { "current": "<version>", "previous": null }
    LAYOUT.txt                   Package instructions and limitations
    app-<version>/
      Fyltrix.exe                Existing WPF application payload
      Fyltrix.dll
      ...                        Self-contained win-x64 dependencies and resources
  Fyltrix-<version>-win-x64.zip
  Fyltrix-<version>-win-x64.zip.sha256
  build-info.json
  .build/                        Isolated build intermediates; excluded from ZIP
```

The ZIP contains the layout's contents. Extract the entire archive and run its root `Fyltrix.exe`, optionally with a folder argument. WebView2 and optional codecs/preview handlers are still separate requirements.

The script never overwrites an existing version destination, even an incomplete one. Choose another OutputRoot for a repeat build. Within the checkout, outputs are restricted to `artifacts/distribution`. External output locations are supported, but application-data overlaps, existing Fyltrix layouts/installations, and junction/symlink traversal are rejected. Spaces, Unicode, and `&` were exercised in build/launch tests. All application build/publish intermediates go to the new release's `.build`, leaving the normal development output separate.

The ordinary portable output at `artifacts/Fyltrix` is not replaced. There is no `-Run` or installer option in this new script; use the existing `publish.ps1 -Run` for normal development.

## Launcher behavior

- Resolve the layout relative to the launcher, so relocating/extracting the complete package works independently of its original build path.
- Read at most 4,097 pointer bytes and accept a valid pointer only up to 4,096 bytes. Reject malformed JSON, duplicate/unknown fields, invalid versions, traversal, and payload junction/symlink redirects.
- Start `app-<current>/Fyltrix.exe`. If that executable is missing and a valid previous payload exists, start the previous one.
- Forward argument boundaries through `ProcessStartInfo.ArgumentList`, without a shell; preserve the caller's working directory. Relative folder arguments retain their existing meaning.
- Wait for the payload and return its exit code. The launcher has no polling loop, background service, automatic restart, or crash supervisor.
- Do not write to the layout or application-data stores. A missing/invalid layout produces a recovery message rather than recursively launching the root executable.

The previous-pointer behavior is a **missing-payload fallback**, not health-based rollback. A payload that exists but crashes, lacks dependencies, or is blocked by security policy is not automatically replaced by the previous version in Stage 2. A corrupt pointer fails closed rather than guessing a version. Stable root and payload processes both use the Fyltrix executable name; a future shutdown/handover stage must account for both processes.

## Data preservation

Settings and persisted application data stay under `%AppData%\FileLens`; caches stay under `%LocalAppData%\FileLens`. The launcher never reads or migrates preferences. The archive contains the application payload, not the user's data, history, Favourites, or window state.

The existing application, its source/configuration defaults, and its normal shutdown behavior are unchanged. Installer IDs, registry entries, and update trust/signing keys are not introduced in this stage.

## Verification

```powershell
dotnet test .\tests\Fyltrix.Packaging.Tests\Fyltrix.Packaging.Tests.csproj
dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj
```

Stage 2 evidence is recorded in [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md). Unit tests cover pointer parsing, version/path rejection, fallback, junction rejection, and argument forwarding. Integration probes launch the actual native root executable on a private desktop with isolated settings, WebView2, and clipboard providers; tests do not interact with the user's desktop.

The checksum file detects archive changes. It is **not** a publisher signature, signed update manifest, or authorization to install an update. This proof of concept is unsigned. Installer behavior, installation/upgrade/uninstall, authenticated releases, safe shutdown, health acknowledgement, and update/rollback recovery remain later stages and require separate approval.
