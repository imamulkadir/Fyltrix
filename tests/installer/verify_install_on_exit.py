"""Offline private-desktop update lifecycle. Never installs/registers a production application."""
from pathlib import Path
import argparse,ctypes,hashlib,json,shutil,time
from verify_installer import PrivateProcess,K

K.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong]
K.OpenProcess.restype=ctypes.c_void_p

def main():
    parser=argparse.ArgumentParser()
    for key in ('layout','probe','data','work-root'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args();work=args.work_root.resolve();repo=Path(__file__).resolve().parents[2]
    assert not ctypes.windll.shell32.IsUserAnAdmin() and not work.exists() and not work.is_relative_to(repo)
    assert 'AppId=Fyltrix.Sandbox.PerUser' in (args.layout/'.fyltrix-install.ini').read_text()
    work.mkdir();results=[]
    def wait(path,condition=lambda _:True,seconds=60):
        end=time.monotonic()+seconds
        while time.monotonic()<end:
            error=path.parent/'probe-error.txt'
            if error.exists():raise AssertionError(error.read_text())
            if path.exists():
                try:value=json.loads(path.read_text())
                except (json.JSONDecodeError,PermissionError):time.sleep(.05);continue
                if condition(value):return value
            time.sleep(.05)
        raise AssertionError(str(path))
    def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
    for mode in ('manual','commit','health-failure','cancel-close','worker-start-failure'):
        case=work/mode;case.mkdir();layout=case/'application & বাংলা';shutil.copytree(args.layout,layout)
        test=case/'isolated';test.mkdir();(test/'.fyltrix-installer-test').write_text('explicit test')
        folder=test/'folder';folder.mkdir();(folder/'Smoke.txt').write_text('fixture')
        profile=test/'profile';profile.mkdir();(profile/'settings.json').write_text(json.dumps(dict(StartupFolder=str(folder),OpenTabs=[str(folder)],RestoreTabs=True,ConfirmCloseTabs=False,ShowHidden=True,PreviewVisible=True,UpdateMode=2 if mode=='manual' else 3)))
        pointer=(layout/'current.json').read_bytes()
        app=PrivateProcess(layout/'app-0.1.5/Fyltrix.exe',[str(folder)],{'DOTNET_STARTUP_HOOKS':str(args.probe.resolve()),'FYLTRIX_INSTALLER_TEST_ROOT':str(test),'FYLTRIX_EXIT_UPDATE_DATA':str(args.data.resolve()),'FYLTRIX_EXIT_UPDATE_CASE':mode})
        restart_handle=None
        try:
            assert wait(test/'ready.json')['previewReady']
            status=wait(test/'exit-status.json',lambda x:x['ready']);stage=Path(status['stage'])
            assert (layout/'current.json').read_bytes()==pointer and not (stage/'handover.json').exists()
            # A completed download must not itself start installation.
            time.sleep(.3);assert not (test/'worker-options.json').exists()
            if mode=='manual':(test/'ready.json').unlink()
            (test/'request-exit').write_text('explicit normal exit or manual install')
            options=wait(test/'worker-options.json')['args']
            assert ('--no-restart' in options)==(mode!='manual')
            assert ('--notify' in options)==(mode=='manual')
            if mode in ('cancel-close','worker-start-failure'):
                wait(test/'exit-status.json',lambda x:not x['pending'] and 'postponed' in x['Message'])
                assert app.running() and (layout/'current.json').read_bytes()==pointer
                assert not (stage/'close-approved').exists()
                (test/'release-app').write_text('ordinary exit without retry');assert app.wait(20)==0
            else:assert app.wait(30)==0
            saved=digest(profile/'settings.json')
            if mode!='worker-start-failure':
                result=wait(stage/'update-result.json',seconds=70)
                assert result['Status']==(0 if mode in ('manual','commit') else 1 if mode=='health-failure' else 2),result
            assert digest(profile/'settings.json')==saved
            if mode in ('manual','commit'):
                assert json.loads((layout/'current.json').read_text())['current']=='0.1.6'
            else:assert (layout/'current.json').read_bytes()==pointer
            if mode=='manual':
                pid=wait(test/'restart-pid.json')['pid'];restart_handle=K.OpenProcess(0x00100000|0x1000,False,pid);assert restart_handle
                ready=wait(test/'ready.json');assert ready['previewReady'] and ready['ShowHidden'] and ready['version']=='0.1.6.0'
                (test/'release-app').write_text('close restarted copy');assert K.WaitForSingleObject(restart_handle,20000)==0
            else:assert not (test/'restart-request.txt').exists() and not (test/'restart-pid.json').exists()
            if mode in ('commit','health-failure'):
                version='0.1.6' if mode=='commit' else '0.1.5'
                for signal in ('ready.json','exit-status.json','request-exit','release-app'):(test/signal).unlink(missing_ok=True)
                original_options=(test/'worker-options.json').read_bytes()
                reopened=PrivateProcess(layout/f'app-{version}/Fyltrix.exe',[str(folder)],{'DOTNET_STARTUP_HOOKS':str(args.probe.resolve()),'FYLTRIX_INSTALLER_TEST_ROOT':str(test),'FYLTRIX_EXIT_UPDATE_DATA':str(args.data.resolve()),'FYLTRIX_EXIT_UPDATE_CASE':mode})
                try:
                    assert wait(test/'ready.json')['previewReady']
                    next_status=wait(test/'exit-status.json',lambda x:('already installed' if mode=='commit' else 'needs review') in x['Message'])
                    assert next_status['ready']==(mode=='health-failure')
                    (test/'release-app').write_text('normal close without repeating prior attempt');assert reopened.wait(20)==0
                    assert (test/'worker-options.json').read_bytes()==original_options
                finally:
                    if reopened.running():(test/'release-app').write_text('cleanup');reopened.wait(60)
                    reopened.close()
            preferences=json.loads((profile/'settings.json').read_text());assert preferences['ShowHidden'] and preferences['UpdateMode']==(2 if mode=='manual' else 3)
            results.append(dict(case=mode,passed=True,settingsPreserved=True,noRestart=mode!='manual'))
        finally:
            (test/'release-app').write_text('cleanup')
            if app.running():app.wait(60)
            app.close()
            if restart_handle:K.WaitForSingleObject(restart_handle,60000);K.CloseHandle(restart_handle)
    report=dict(success=True,results=results,privateDesktop=True,explicitDisposableTestTrust=True,productionInstallation=False,gitOrReleaseWrites=False)
    (work/'verification.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))

if __name__=='__main__':main()
