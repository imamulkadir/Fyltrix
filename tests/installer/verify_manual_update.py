"""Offline, non-elevated, private-desktop real update-window integration. Sandbox installers only."""
from pathlib import Path
import argparse,ctypes,json,os,re,shutil,time,winreg
from verify_installer import PrivateProcess,registration,snapshot,K

K.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong]
K.OpenProcess.restype=ctypes.c_void_p

def main():
    p=argparse.ArgumentParser()
    for name in ('installer','probe','data-directory','work-root'):p.add_argument('--'+name,type=Path,required=True)
    args=p.parse_args(); repo=Path(__file__).resolve().parents[2]; work=args.work_root.resolve()
    assert os.name=='nt' and not ctypes.windll.shell32.IsUserAnAdmin()
    assert not work.exists() and not work.is_relative_to(repo) and registration() is None and registration(winreg.HKEY_LOCAL_MACHINE) is None
    info=json.loads((args.installer.parent/'installer-build.json').read_text(encoding='utf-8-sig'))
    assert info['mode']=='Sandbox'
    old=info['version'];new=json.loads((args.data_directory/'release.json').read_text(encoding='utf-8'))['tag_name'].removeprefix('v')
    assert re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)',old)
    assert re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)',new)
    assert tuple(map(int,new.split('.')))>tuple(map(int,old.split('.')))
    import hashlib
    assert hashlib.sha256(args.installer.read_bytes()).hexdigest()==info['sha256']
    for env in ('APPDATA','LOCALAPPDATA'):
        protected=(Path(os.environ[env])/'FileLens').resolve()
        assert not work.is_relative_to(protected) and not protected.is_relative_to(work)
    work.mkdir(); before=snapshot(Path(os.environ['APPDATA'])/'FileLens'); results=[]
    def native(exe,argv):
        process=PrivateProcess(exe,argv)
        try:return process.wait(60)
        finally:process.close()
    def wait(path,test,timeout=60):
        deadline=time.monotonic()+timeout
        while not path.exists() and time.monotonic()<deadline:
            error=test/'manual-probe-error.txt'
            if error.exists():raise AssertionError(error.read_text())
            time.sleep(.05)
        assert path.exists(),str(path)
        return json.loads(path.read_text())
    installed=None
    try:
        for case in ('commit','cancel-close','worker-start-failure','health-failure'):
            case_root=work/case; case_root.mkdir(); layout=case_root/'application & বাংলা'; test=case_root/'isolated'; test.mkdir()
            (test/'.fyltrix-installer-test').write_text('explicit test')
            shutil.copy2(args.data_directory/'public-test.txt',test/'public-test.txt')
            code=native(args.installer,['/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG='+str(case_root/'setup.log'),'/DIR='+str(layout)])
            assert code==0,f'Sandbox Setup exit {code}; see {case_root / "setup.log"}'
            installed=layout; folder=test/'folder';folder.mkdir();(folder/'Smoke.txt').write_text('fixture')
            profile=test/'profile';profile.mkdir();(profile/'settings.json').write_text(json.dumps({'StartupFolder':str(folder),'OpenTabs':[str(folder)],'RestoreTabs':True,'ConfirmCloseTabs':False,'ShowHidden':True,'PreviewVisible':True,'ShowSecondaryToolbarLabels':False}))
            original=(layout/'current.json').read_bytes()
            environment={'DOTNET_STARTUP_HOOKS':str(args.probe.resolve()),'FYLTRIX_INSTALLER_TEST_ROOT':str(test),
                'FYLTRIX_MANUAL_UPDATE_DATA':str(args.data_directory.resolve()),'FYLTRIX_MANUAL_UPDATE_CASE':case}
            app=PrivateProcess(layout/'Fyltrix.exe',[str(folder)],environment)
            try:
                assert wait(test/'ready.json',test)['previewReady']
                (test/'open-updates').write_text('open'); assert wait(test/'ui-opened.json',test)['noAutomaticCheck']
                (test/'check-update').write_text('explicit check'); assert wait(test/'ui-available.json',test)['authenticated']
                (test/'download-update').write_text('explicit download'); downloaded=wait(test/'ui-downloaded.json',test)
                stage=Path(downloaded['stage']); assert downloaded['noAutomaticInstallation']
                assert app.running() and (layout/'current.json').read_bytes()==original and not (stage/'close-approved').exists()
                if case=='commit':(test/'ready.json').unlink()
                (test/'install-update').write_text('explicit install and restart')
                if case in ('commit','health-failure'):
                    assert app.wait(60)==0
                    result=wait(stage/'update-result.json',test)
                    assert result['Status']==(0 if case=='commit' else 1),result
                    assert json.loads((layout/'current.json').read_text())['current']==(new if case=='commit' else old)
                    assert (stage/'close-approved').exists()
                    assert (test/'restart-request.txt').exists()==(case=='commit')
                else:
                    refused=wait(test/'ui-refused.json',test); assert refused['mainStillOpen'] and app.running()
                    assert (layout/'current.json').read_bytes()==original and not (stage/'close-approved').exists()
                    (test/'release-app').write_text('normal close'); assert app.wait(20)==0
                    if case=='cancel-close': assert wait(stage/'update-result.json',test)['Status']!=0
            finally:
                if app.running():(test/'release-app').write_text('cleanup');app.wait(100)
                app.close()
            # The committed update actually launches the new app; failed/refused updates use a manual restart.
            if case=='commit':
                pid=wait(test/'restart-pid.json',test)['pid']
                handle=K.OpenProcess(0x100000|0x1000,False,pid);assert handle
                try:
                    ready=wait(test/'ready.json',test);assert ready['previewReady'] and ready['ShowHidden'] and ready['version']==new+'.0'
                    (test/'release-app').write_text('close restarted application')
                    assert K.WaitForSingleObject(handle,20000)==0
                    code=ctypes.c_ulong();assert K.GetExitCodeProcess(handle,ctypes.byref(code)) and code.value==0
                finally:
                    (test/'release-app').write_text('cleanup restarted application');K.WaitForSingleObject(handle,60000);K.CloseHandle(handle)
            else:
                (test/'ready.json').unlink();(test/'release-app').unlink(missing_ok=True)
                restarted=PrivateProcess(layout/'Fyltrix.exe',[str(folder)],{'DOTNET_STARTUP_HOOKS':str(args.probe.resolve()),'FYLTRIX_INSTALLER_TEST_ROOT':str(test)})
                try:
                    ready=wait(test/'ready.json',test);assert ready['previewReady'] and ready['ShowHidden'] and ready['version']==old+'.0'
                    (test/'release-app').write_text('close');assert restarted.wait(20)==0
                finally:
                    if restarted.running():(test/'release-app').write_text('cleanup');restarted.wait(60)
                    restarted.close()
            assert native(layout/'unins000.exe',['/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'])==0
            deadline=time.monotonic()+30
            while time.monotonic()<deadline and ((layout/'Fyltrix.exe').exists() or (layout/f'app-{new}/Fyltrix.exe').exists()):time.sleep(.05)
            assert registration() is None and not (layout/'Fyltrix.exe').exists() and not (layout/f'app-{new}/Fyltrix.exe').exists()
            installed=None; results.append({'case':case,'passed':True,'realUpdateWindow':True,'productionDefaultTrust':True,'freshStartupAndPreferencesVerified':True,'automaticRestartVerified':case=='commit'})
        assert snapshot(Path(os.environ['APPDATA'])/'FileLens')==before
        report={'success':True,'results':results,'privateDesktop':True,'realProfileUnchanged':True,'productionInstallerExecuted':False,'published':False}
        (work/'verification.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
    finally:
        if installed is not None and registration() is not None and (installed/'unins000.exe').exists():
            native(installed/'unins000.exe',['/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'])

if __name__=='__main__':main()
