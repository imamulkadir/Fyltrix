"""Windows private-desktop Stage 6 sandbox verification. Never installs a Production identity or publishes anything.

Requires current Debug UpdateTool/HandoverProbe, Release PlacementProbe, two protocol-capable release directories,
and a Sandbox installer for the old release. All generated signing keys are disposable test keys, never trust pins.
"""
from pathlib import Path
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import subprocess
import time
import uuid
import winreg
from verify_installer import PrivateProcess, registration, snapshot


def main():
    parser = argparse.ArgumentParser()
    for name in ('old-release', 'new-release', 'installer', 'probe', 'work-root'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--cases', default='commit,invalid-trial-settings,interrupt-switch,interrupt-commit')
    args = parser.parse_args()
    assert os.name == 'nt' and not ctypes.windll.shell32.IsUserAnAdmin(), 'Run non-elevated on Windows'
    repo = Path(__file__).resolve().parents[2]
    work = args.work_root.resolve()
    assert not work.exists() and not work.is_relative_to(repo)
    for location in ('APPDATA', 'LOCALAPPDATA'):
        profile = (Path(os.environ[location]) / 'FileLens').resolve()
        assert not work.is_relative_to(profile) and not profile.is_relative_to(work)
    assert registration() is None and registration(winreg.HKEY_LOCAL_MACHINE) is None
    record = json.loads((args.installer.parent / 'installer-build.json').read_text(encoding='utf-8-sig'))
    assert record['mode'] == 'Sandbox' and record['appId'] == 'Fyltrix.Sandbox.PerUser'
    assert hashlib.sha256(args.installer.read_bytes()).hexdigest() == record['sha256']
    old = json.loads((args.old_release / 'build-info.json').read_text(encoding='utf-8-sig'))['version']
    new = json.loads((args.new_release / 'build-info.json').read_text(encoding='utf-8-sig'))['version']
    assert old == '0.1.0' and new == '0.1.1' and record['version'] == old
    for release, version in ((args.old_release, old), (args.new_release, new)):
        info = json.loads((release / 'build-info.json').read_text(encoding='utf-8-sig'))
        assert hashlib.sha256((release / f'Fyltrix-{version}-win-x64.zip').read_bytes()).hexdigest() == info['sha256']
    work.mkdir()
    baseline = snapshot(Path(os.environ['APPDATA']) / 'FileLens')
    dotnet = str(Path(os.environ['ProgramFiles']) / 'dotnet/dotnet.exe')
    tool = repo / 'tools/Fyltrix.UpdateTool/bin/Debug/net8.0/Fyltrix.UpdateTool.dll'
    key_probe = repo / 'tests/Fyltrix.HandoverProbe/bin/Debug/net8.0/Fyltrix.HandoverProbe.exe'
    keyroot = work / ('Fyltrix-UpdateKey-' + uuid.uuid4().hex)
    subprocess.run([str(key_probe), '--test-key-root', str(keyroot)], check=True, creationflags=subprocess.CREATE_NO_WINDOW)

    def command(*values):
        result = subprocess.run([dotnet, str(tool), *map(str, values)], check=True, capture_output=True, text=True,
                                timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        return result.stdout

    def wait_file(path, seconds=40):
        until = time.monotonic() + seconds
        while not path.exists() and time.monotonic() < until:
            time.sleep(.05)
        assert path.exists(), str(path)
        return json.loads(path.read_text(encoding='utf-8-sig'))

    def native(exe, arguments, environment=None, timeout=120):
        child = PrivateProcess(exe, arguments, environment)
        try:
            return child.wait(timeout)
        finally:
            child.close()

    results = []
    installed = None
    try:
        signed = work / 'signed'
        package = args.new_release / f'Fyltrix-{new}-win-x64.zip'
        command('sign', new, keyroot / 'private-test.pem', keyroot / 'public-test.txt', signed, package)
        metadata = {'tag_name': 'v' + new, 'html_url': f'https://github.com/imamulkadir/Fyltrix/releases/tag/v{new}',
                    'draft': False, 'prerelease': False, 'body': 'Local sandbox fixture only', 'assets': []}
        for path in (package, signed / 'Fyltrix-release.json', signed / 'Fyltrix-release.json.sig'):
            metadata['assets'].append({'name': path.name, 'size': path.stat().st_size, 'state': 'uploaded',
                'browser_download_url': f'https://github.com/imamulkadir/Fyltrix/releases/download/v{new}/{path.name}'})
        feed = work / 'release.json'; feed.write_text(json.dumps(metadata), encoding='utf-8')
        (keyroot / 'private-test.pem').unlink()  # No private material is needed for applying/recovery.

        for case in args.cases.split(','):
            assert case in ('commit', 'invalid-trial-settings', 'interrupt-switch', 'interrupt-commit')
            case_root = work / case; case_root.mkdir()
            layout = case_root / 'installation & বাংলা'; test = case_root / 'isolated'; test.mkdir()
            (test / '.fyltrix-installer-test').write_text('explicit sandbox test')
            shutil.copy2(keyroot / 'public-test.txt', test / 'public-test.txt')
            assert native(args.installer, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR=' + str(layout),
                          '/LOG=' + str(case_root / 'install.log')]) == 0
            installed = layout
            folder = test / 'folder'; folder.mkdir(); (folder / 'Smoke.txt').write_text('fixture')
            profile = test / 'profile'; profile.mkdir()
            prefs = {'ShowHidden': True, 'ShowExtensions': False, 'ShowFileOperationLabels': True,
                'ShowSecondaryToolbarLabels': False, 'DefaultSort': 'Size', 'Favourites': [str(folder)],
                'OpenTabs': [str(folder)], 'RestoreTabs': True, 'ConfirmCloseTabs': False, 'StartupFolder': str(folder)}
            (profile / 'settings.json').write_text(json.dumps(prefs), encoding='utf-8')
            (profile / 'custom.keep').write_bytes(b'preserve private user data')
            staged = json.loads(command('stage-local', old, feed, signed / 'Fyltrix-release.json', signed / 'Fyltrix-release.json.sig',
                            package, keyroot / 'public-test.txt', case_root / 'staging'))
            stage = Path(staged['directory'])
            environment = {'DOTNET_STARTUP_HOOKS': str(args.probe.resolve()), 'FYLTRIX_INSTALLER_TEST_ROOT': str(test),
                'FYLTRIX_HANDOVER_TEST_STAGE': str(stage), 'FYLTRIX_HANDOVER_TEST_KEY': str(test / 'public-test.txt')}
            app = PrivateProcess(layout / 'Fyltrix.exe', [str(folder)], environment)
            worker = None
            try:
                ready = wait_file(test / 'ready.json'); assert ready['previewReady'] and ready['ShowHidden']
                (test / 'prepare-for-update').write_text('prepare')
                prepared = wait_file(test / 'handover-prepared.json')
                request_path = Path(prepared['request']); request = json.loads(request_path.read_text())
                original_pointer = (layout / 'current.json').read_bytes()
                assert native(layout / f'app-{old}/Fyltrix.exe', ['--fyltrix-update-worker', str(request_path), request['Token']], timeout=20) == 2
                assert app.running() and (layout / 'current.json').read_bytes() == original_pointer
                worker_env = {'DOTNET_STARTUP_HOOKS': str(args.probe.resolve()), 'FYLTRIX_INSTALLER_TEST_ROOT': str(test),
                    'FYLTRIX_SANDBOX_UPDATE_WORKER': '1', 'FYLTRIX_SANDBOX_FAULT': case}
                worker = PrivateProcess(layout / f'app-{old}/Fyltrix.exe', ['--fyltrix-update-worker', str(request_path), request['Token']], worker_env)
                (test / 'release-for-update').write_text('explicit close')
                assert app.wait(40) == 0
                profile_before_apply = snapshot(profile)
                code = worker.wait(90); worker.close(); worker = None
                expected = 0 if case == 'commit' else 3 if case == 'invalid-trial-settings' else 97 if case == 'interrupt-switch' else 98
                assert code == expected, (case, code, (stage / 'update-result.json').read_text() if (stage / 'update-result.json').exists() else '')
                if case.startswith('interrupt-'):
                    assert (layout / 'update-transaction.json').exists()
                    # Direct payload startup must refuse without opening or resetting its profile.
                    assert native(layout / f'app-{new}/Fyltrix.exe', [], timeout=20) == 4
                    recovery = PrivateProcess(layout / f'app-{old}/Fyltrix.exe', ['--fyltrix-update-recover', str(request_path), request['Token']],
                                             {**worker_env, 'FYLTRIX_SANDBOX_FAULT': ''})
                    try:
                        assert recovery.wait(60) == (0 if case == 'interrupt-commit' else 3)
                    finally:
                        recovery.close()
                committed = case in ('commit', 'interrupt-commit')
                assert json.loads((layout / 'current.json').read_text())['current'] == (new if committed else old)
                if not committed: assert (layout / 'current.json').read_bytes() == original_pointer
                assert snapshot(profile) == profile_before_apply
                assert not (layout / 'update-transaction.json').exists()
                assert (layout / f'app-{old}/Fyltrix.exe').exists()
                assert registration()['DisplayVersion'] == (new if committed else old)
                # Verify real launch and saved preferences after commit or rollback, with no automatic repeat update.
                for name in ('ready.json', 'update-close.json', 'release-for-update', 'prepare-for-update', 'handover-prepared.json'):
                    (test / name).unlink(missing_ok=True)
                restarted = PrivateProcess(layout / 'Fyltrix.exe', [str(folder)],
                    {'DOTNET_STARTUP_HOOKS': str(args.probe.resolve()), 'FYLTRIX_INSTALLER_TEST_ROOT': str(test)})
                try:
                    status = wait_file(test / 'ready.json'); assert status['previewReady'] and status['ShowHidden'] and not status['ShowExtensions']
                    assert status['version'] == ('0.1.1.0' if committed else '0.1.0.0')
                    (test / 'release-app').write_text('close'); assert restarted.wait(20) == 0
                finally:
                    if restarted.running(): (test / 'release-app').write_text('cleanup'); restarted.wait(100)
                    restarted.close()
                if committed:
                    assert native(args.installer, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR=' + str(layout),
                        '/LOG=' + str(case_root / 'reject-old-installer.log')]) != 0
                keep = layout / 'user.keep'; keep.write_text('unknown user file')
                nested = layout / f'app-{new}/user.keep'; nested.write_text('unknown nested file')
                modified = layout / f'app-{new}/Fyltrix.runtimeconfig.json'; modified.write_text('{"user-edited":"keep"}')
                assert native(layout / 'unins000.exe', ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=' + str(case_root / 'uninstall.log')]) == 0
                # Inno's bootstrap can exit while its temporary uninstaller finishes post-uninstall code.
                until = time.monotonic() + 30
                while time.monotonic() < until and ((layout / 'update-files.ini').exists() or (layout / f'app-{new}/Fyltrix.exe').exists()):
                    time.sleep(.05)
                assert registration() is None
                assert not (layout / 'Fyltrix.exe').exists() and not (layout / f'app-{old}/Fyltrix.exe').exists()
                assert not (layout / f'app-{new}/Fyltrix.exe').exists(), 'Updater-created payload must be uninstalled'
                assert keep.read_text() == 'unknown user file' and nested.read_text() == 'unknown nested file'
                assert modified.read_text() == '{"user-edited":"keep"}'
                assert snapshot(Path(os.environ['APPDATA']) / 'FileLens') == baseline
                installed = None
                results.append({'case': case, 'success': True, 'profilePreservedDuringTrial': True, 'actualRestartVerified': True,
                                'ownedPayloadsUninstalled': True, 'unknownFilesKept': True, 'modifiedOwnedFileKept': True,
                                'productionTrustRefused': True})
            finally:
                if app.running(): (test / 'release-app').write_text('cleanup'); app.wait(100)
                app.close()
                if worker is not None:
                    worker.wait(100); worker.close()
        report = {'success': True, 'results': results, 'realRoamingProfileUnchanged': snapshot(Path(os.environ['APPDATA']) / 'FileLens') == baseline,
                  'productionInstallerExecuted': False, 'productionTrustChanged': False, 'nonElevated': True}
        (work / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps(report, indent=2))
    finally:
        (keyroot / 'private-test.pem').unlink(missing_ok=True)
        if installed is not None and registration() is not None and (installed / 'unins000.exe').exists():
            native(installed / 'unins000.exe', ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'])


if __name__ == '__main__':
    main()
