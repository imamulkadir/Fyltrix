"""Windows-only sandbox integration test. Builds nothing and never installs a production identity.

Requires Python 3, two Sandbox installers built by tools/Build-Installer.ps1, and a Release build
of tests/Fyltrix.PlacementProbe (its test-only startup hook isolates application preferences).
All installer/application windows run on a private desktop. Existing profile contents are hashed,
never copied into reports. The test aborts if an earlier sandbox registration/shortcut exists.
"""
from pathlib import Path
import argparse
import base64
import ctypes
from ctypes import wintypes as W
import hashlib
import json
import os
import subprocess
import time
import uuid
import winreg


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def snapshot(folder):
    return {str(p.relative_to(folder)): digest(p) for p in folder.rglob("*") if p.is_file()} if folder.exists() else {}


class SI(ctypes.Structure):
    _fields_ = [("cb", W.DWORD), ("lpReserved", W.LPWSTR), ("lpDesktop", W.LPWSTR), ("lpTitle", W.LPWSTR),
                ("dwX", W.DWORD), ("dwY", W.DWORD), ("dwXSize", W.DWORD), ("dwYSize", W.DWORD),
                ("dwXCountChars", W.DWORD), ("dwYCountChars", W.DWORD), ("dwFillAttribute", W.DWORD),
                ("dwFlags", W.DWORD), ("wShowWindow", W.WORD), ("cbReserved2", W.WORD),
                ("lpReserved2", ctypes.POINTER(ctypes.c_byte)), ("hStdInput", W.HANDLE),
                ("hStdOutput", W.HANDLE), ("hStdError", W.HANDLE)]


class PI(ctypes.Structure):
    _fields_ = [("hProcess", W.HANDLE), ("hThread", W.HANDLE), ("dwProcessId", W.DWORD), ("dwThreadId", W.DWORD)]


U = ctypes.WinDLL("user32", use_last_error=True)
K = ctypes.WinDLL("kernel32", use_last_error=True)
U.CreateDesktopW.argtypes = [W.LPCWSTR, W.LPCWSTR, ctypes.c_void_p, W.DWORD, W.DWORD, ctypes.c_void_p]
U.CreateDesktopW.restype = W.HANDLE
U.CloseDesktop.argtypes = [W.HANDLE]
K.CreateProcessW.argtypes = [W.LPCWSTR, W.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, W.BOOL, W.DWORD,
                            ctypes.c_void_p, W.LPCWSTR, ctypes.POINTER(SI), ctypes.POINTER(PI)]
K.CreateProcessW.restype = W.BOOL
K.WaitForSingleObject.argtypes = [W.HANDLE, W.DWORD]
K.WaitForSingleObject.restype = W.DWORD
K.GetExitCodeProcess.argtypes = [W.HANDLE, ctypes.POINTER(W.DWORD)]
K.CloseHandle.argtypes = [W.HANDLE]
K.TerminateProcess.argtypes = [W.HANDLE, W.UINT]


class PrivateProcess:
    def __init__(self, exe, args, environment=None):
        self.desktop_name = "FyltrixInstallerTest-" + uuid.uuid4().hex
        self.desktop = U.CreateDesktopW(self.desktop_name, None, None, 0, 0x01FF, None)
        if not self.desktop:
            raise ctypes.WinError(ctypes.get_last_error())
        self.pi = PI()
        si = SI()
        si.cb = ctypes.sizeof(si)
        si.lpDesktop = self.desktop_name
        command = ctypes.create_unicode_buffer(subprocess.list2cmdline([str(exe), *map(str, args)]))
        previous = {key: os.environ.get(key) for key in environment or {}}
        try:
            os.environ.update(environment or {})
            if not K.CreateProcessW(str(exe), command, None, None, False, 0x08000000, None, str(exe.parent), ctypes.byref(si), ctypes.byref(self.pi)):
                raise ctypes.WinError(ctypes.get_last_error())
        except BaseException:
            U.CloseDesktop(self.desktop)
            raise
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

    def running(self):
        return K.WaitForSingleObject(self.pi.hProcess, 0) == 258

    def wait(self, seconds=120):
        if K.WaitForSingleObject(self.pi.hProcess, seconds * 1000) == 258:
            # Only the process created by this test, never a process found by application name.
            K.TerminateProcess(self.pi.hProcess, 99)
            K.WaitForSingleObject(self.pi.hProcess, 5000)
            raise TimeoutError("Owned installer/application process timed out")
        code = W.DWORD()
        K.GetExitCodeProcess(self.pi.hProcess, ctypes.byref(code))
        return code.value

    def close(self):
        K.CloseHandle(self.pi.hThread)
        K.CloseHandle(self.pi.hProcess)
        U.CloseDesktop(self.desktop)


REGISTRY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fyltrix.Sandbox.PerUser_is1"


def registration(hive=winreg.HKEY_CURRENT_USER):
    try:
        with winreg.OpenKey(hive, REGISTRY, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            return {name: winreg.QueryValueEx(key, name)[0] for name in ["DisplayName", "DisplayVersion", "InstallLocation", "UninstallString"]}
    except FileNotFoundError:
        return None


def powershell(script):
    script = "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false); " + script
    encoded = base64.b64encode(script.encode("utf-16le")).decode()
    return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], check=True, capture_output=True, encoding="utf-8").stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--new", type=Path, required=True)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    work = args.work_root.resolve()
    if work.exists() or work.is_relative_to(repo):
        raise RuntimeError("Use a new external test workspace")
    # Verify installer identity and exact executable hashes before running anything.
    records = []
    for setup in [args.old, args.new]:
        record = json.loads((setup.parent / "installer-build.json").read_text(encoding="utf-8-sig"))
        assert record["mode"] == "Sandbox" and record["appId"] == "Fyltrix.Sandbox.PerUser"
        assert digest(setup) == record["sha256"]
        records.append(record)
    assert registration() is None and registration(winreg.HKEY_LOCAL_MACHINE) is None, "An existing sandbox installation must not be replaced"
    folder_buffer = ctypes.create_unicode_buffer(32768)
    ctypes.windll.shell32.SHGetFolderPathW(None, 2, None, 0, folder_buffer)  # CSIDL_PROGRAMS
    shortcut = Path(folder_buffer.value) / "Fyltrix Installer Sandbox" / "Fyltrix Installer Sandbox.lnk"
    assert not shortcut.exists(), "An existing sandbox shortcut must not be replaced"
    profile = work / "profile"
    real_profile = Path(os.environ["APPDATA"]) / "FileLens"
    assert not work.is_relative_to(real_profile.resolve())
    local_store = Path(os.environ["LOCALAPPDATA"]) / "FileLens"
    assert not work.is_relative_to(local_store.resolve())
    work.mkdir(parents=True)
    (work / ".fyltrix-installer-test").write_text("Sandbox integration test only", encoding="utf-8")
    profile.mkdir()
    data = work / "Folder with spaces & caf\u00e9"
    data.mkdir()
    (data / "Smoke.txt").write_text("Installer smoke test", encoding="utf-8")
    favourite = work / "Saved favourite"
    favourite.mkdir()
    settings = {"Theme": 1, "ShowHidden": True, "ShowExtensions": False,
                "ShowFileOperationLabels": True, "ShowSecondaryToolbarLabels": False,
                "DefaultSort": "Size", "DefaultSortAscending": False, "PreviewVisible": True,
                "Favourites": [str(favourite)], "RecentFolders": [str(favourite)],
                "OpenTabs": [str(data)], "ActiveTab": 0, "RestoreTabs": True}
    (profile / "settings.json").write_text(json.dumps(settings, indent=2), encoding="utf-8")
    (profile / "custom-user-data.json").write_text('{"keep":"existing user data"}', encoding="utf-8")
    (profile / "cache-sentinel.bin").write_bytes(b"preserve cached/user files")
    target = work / "Sandbox install & caf\u00e9"
    real_before = snapshot(real_profile)
    results = []
    app = None

    def invoke(exe, arguments, name, succeeds):
        before = snapshot(profile)
        process = PrivateProcess(exe, arguments)
        try:
            code = process.wait()
        finally:
            process.close()
        assert (code == 0) == succeeds, (name, code)
        assert snapshot(profile) == before, (name, "synthetic profile changed")
        assert snapshot(real_profile) == real_before, (name, "real profile changed")
        results.append({"case": name, "exitCode": code, "passed": True, "profileUnchanged": True})
        print(json.dumps(results[-1]), flush=True)

    def install(setup, destination, name, succeeds=True):
        invoke(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TASKS=", "/DIR=" + str(destination), "/LOG=" + str(work / (name + ".log"))], name, succeeds)

    def uninstall(name, succeeds=True):
        invoke(target / "unins000.exe", ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + str(work / (name + ".log"))], name, succeeds)

    def start_app():
        for flag in ["release-app", "ready.json", "probe-error.txt"]:
            p = work / flag
            if p.exists():
                p.unlink()
        process = PrivateProcess(target / "Fyltrix.exe", [data], {
            "DOTNET_STARTUP_HOOKS": str(args.probe.resolve()), "FYLTRIX_INSTALLER_TEST_ROOT": str(work)})
        try:
            deadline = time.monotonic() + 40
            while not (work / "ready.json").exists() and time.monotonic() < deadline and process.running():
                time.sleep(0.1)
            assert (work / "ready.json").exists(), "Installed application did not become ready"
            ready = json.loads((work / "ready.json").read_text(encoding="utf-8"))
            for key in ["ShowHidden", "ShowExtensions", "ShowFileOperationLabels", "ShowSecondaryToolbarLabels", "DefaultSort", "Favourites"]:
                assert ready[key] == settings[key], (key, ready)
            assert ready["previewReady"] and ready["isolatedProfile"] == str(profile)
            results.append({"case": "installed-app-" + ready["version"], "passed": True, "previewReady": True, "preferencesPreserved": True})
            return process
        except BaseException:
            (work / "release-app").write_text("close failed probe normally", encoding="utf-8")
            try:
                process.wait()
            finally:
                process.close()
            raise

    def stop_app(process):
        (work / "release-app").write_text("close normally", encoding="utf-8")
        assert process.wait() == 0
        process.close()
        assert snapshot(real_profile) == real_before

    try:
        install(args.old, repo / "artifacts" / "Fyltrix", "reject-development-copy", False)
        install(args.old, real_profile, "reject-user-data", False)
        foreign = work / "Foreign nonempty directory"
        foreign.mkdir()
        (foreign / "keep.txt").write_text("must stay", encoding="utf-8")
        before = snapshot(foreign)
        install(args.old, foreign, "reject-unowned-directory", False)
        assert snapshot(foreign) == before
        production = work / "Pretend production copy"
        production.mkdir()
        (production / ".fyltrix-install.ini").write_text("[Installation]\nSchema=1\nAppId=Fyltrix.PerUser\nMode=Production\n", encoding="utf-8")
        before = snapshot(production)
        install(args.old, production, "reject-other-installer-identity", False)
        assert snapshot(production) == before

        install(args.old, target, "install-v1")
        assert registration()["DisplayVersion"] == records[0]["version"]
        assert registration(winreg.HKEY_LOCAL_MACHINE) is None and shortcut.exists()
        escaped = str(shortcut).replace("'", "''")
        link = json.loads(powershell("$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + escaped + "'); @{target=$s.TargetPath;workingDirectory=$s.WorkingDirectory} | ConvertTo-Json -Compress"))
        assert Path(link["target"]).resolve() == (target / "Fyltrix.exe").resolve(), link
        assert Path(link["workingDirectory"]).resolve() == target.resolve()
        assert json.loads((target / "current.json").read_text(encoding="utf-8"))["current"] == records[0]["version"]
        marker = target / ".fyltrix-install.ini"
        marker_bytes = marker.read_bytes()
        marker.write_text("[Installation]\nSchema=1\nAppId=Fyltrix.PerUser\nMode=Production\n", encoding="utf-8")
        try:
            before = snapshot(target)
            uninstall("reject-uninstall-wrong-ownership", False)
            assert snapshot(target) == before
        finally:
            marker.write_bytes(marker_bytes)
        marker.write_text("[Installation]\nSchema=99\nAppId=Fyltrix.Sandbox.PerUser\nMode=Sandbox\n", encoding="utf-8")
        try:
            before = snapshot(target)
            uninstall("reject-uninstall-unknown-schema", False)
            assert snapshot(target) == before
        finally:
            marker.write_bytes(marker_bytes)

        redirected = work / "redirected-install"
        escaped_link, escaped_target = str(redirected).replace("'", "''"), str(target).replace("'", "''")
        powershell("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + escaped_link + "' -Target '" + escaped_target + "' | Out-Null")
        try:
            before = snapshot(target)
            install(args.new, redirected, "reject-install-junction", False)
            assert snapshot(target) == before
        finally:
            redirected.rmdir()

        with open(target / "current.json", "rb") as locked:
            # Python permits shared reading, but the installer requires exclusive write access before changes.
            before = snapshot(target)
            install(args.new, target, "reject-locked-pointer", False)
            assert snapshot(target) == before
        app = start_app()
        before = snapshot(target)
        install(args.new, target, "reject-upgrade-running-app", False)
        uninstall("reject-uninstall-running-app", False)
        assert app.running() and snapshot(target) == before
        stop_app(app)
        app = None

        install(args.new, work / "Relocation attempt", "reject-orphaning-relocation", False)
        install(args.new, target, "upgrade-v2")
        assert registration()["DisplayVersion"] == records[1]["version"]
        assert (target / ("app-" + records[0]["version"]) / "Fyltrix.exe").exists()
        assert (target / ("app-" + records[1]["version"]) / "Fyltrix.exe").exists()
        assert json.loads((target / "current.json").read_text(encoding="utf-8"))["current"] == records[1]["version"]
        before = snapshot(target)
        install(args.old, target, "reject-binary-downgrade", False)
        assert snapshot(target) == before
        app = start_app()
        stop_app(app)
        app = None
        root_sentinel = target / "user-added.keep"
        root_sentinel.write_text("do not delete", encoding="utf-8")
        nested_sentinel = target / ("app-" + records[0]["version"]) / "user-added.keep"
        nested_sentinel.write_text("keep nested user data", encoding="utf-8")
        uninstall("uninstall-v2")
        assert registration() is None and not shortcut.exists()
        assert not (target / "Fyltrix.exe").exists()
        assert not (target / ".fyltrix-install.ini").exists()
        assert not (target / ("app-" + records[0]["version"]) / "Fyltrix.exe").exists()
        assert not (target / ("app-" + records[1]["version"]) / "Fyltrix.exe").exists()
        assert root_sentinel.read_text(encoding="utf-8") == "do not delete"
        assert nested_sentinel.read_text(encoding="utf-8") == "keep nested user data"
        assert snapshot(real_profile) == real_before
        report = {"success": True, "results": results, "unknownInstallationFilesRetained": True,
                  "realProfileUnchanged": True, "sandboxRegistrationRemoved": True,
                  "sandboxShortcutRemoved": True, "productionInstallerExecuted": False,
                  "nonElevated": not bool(ctypes.windll.shell32.IsUserAnAdmin())}
        (work / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps({key: value for key, value in report.items() if key != "results"}, indent=2))
    finally:
        if app is not None:
            stop_app(app)
        # Never delete registry entries manually or kill arbitrary Fyltrix processes.
        # If an assertion failed after installation, preserve evidence and use only this sandbox's own uninstaller.
        if registration() is not None and (target / "unins000.exe").exists():
            process = PrivateProcess(target / "unins000.exe", ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"])
            try:
                code = process.wait()
                if code != 0:
                    print("Sandbox cleanup was refused; use its uninstaller after resolving the logged cause.", flush=True)
            finally:
                process.close()


if __name__ == "__main__":
    main()
