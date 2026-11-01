"""Private-desktop real App.Main scheduler smoke test; Sandbox-marked isolated layout only."""
from pathlib import Path
import argparse,ctypes,json,shutil,time
from verify_installer import PrivateProcess

def main():
    parser=argparse.ArgumentParser()
    for item in ('layout','probe','data','work-root'):parser.add_argument('--'+item,type=Path,required=True)
    args=parser.parse_args();root=args.work_root.resolve();repo=Path(__file__).resolve().parents[2]
    assert not ctypes.windll.shell32.IsUserAnAdmin()
    assert not root.exists() and not root.is_relative_to(repo)
    assert 'AppId=Fyltrix.Sandbox.PerUser' in (args.layout/'.fyltrix-install.ini').read_text()
    version=json.loads((args.layout/'current.json').read_text())['current']
    payload=(args.layout/f'app-{version}/Fyltrix.exe').resolve()
    assert payload.is_relative_to(args.layout.resolve()) and payload.is_file()
    root.mkdir();results=[]
    def wait(path,condition=lambda _:True):
        until=time.monotonic()+40
        while time.monotonic()<until:
            if path.exists():
                try:value=json.loads(path.read_text())
                except json.JSONDecodeError:continue
                if condition(value):return value
            time.sleep(.05)
        raise AssertionError(str(path))
    for mode in (1,0):
        case=root/str(mode);case.mkdir();(case/'.fyltrix-installer-test').write_text('explicit test')
        folder=case/'folder';folder.mkdir();(folder/'Smoke.txt').write_text('fixture')
        profile=case/'profile';profile.mkdir()
        (profile/'settings.json').write_text(json.dumps({'StartupFolder':str(folder),'OpenTabs':[str(folder)],'RestoreTabs':True,'ConfirmCloseTabs':False,'ShowHidden':True,'PreviewVisible':True,'UpdateMode':mode}))
        # Own the payload directly so a failed smoke check cannot orphan it behind a launcher.
        app=PrivateProcess(payload,[str(folder)],{'DOTNET_STARTUP_HOOKS':str(args.probe.resolve()),'FYLTRIX_INSTALLER_TEST_ROOT':str(case),'FYLTRIX_NOTIFICATION_TEST_DATA':str(args.data.resolve())})
        try:
            assert wait(case/'ready.json')['previewReady']
            snapshot=wait(case/'notice-probe.json',lambda x:x['banner']=='Visible') if mode else wait(case/'notice-probe.json')
            if mode:
                assert snapshot['checks']==1 and '0.1.6' in snapshot['UpdateBannerText']
                (case/'dismiss-notice').write_text('dismiss');snapshot=wait(case/'notice-probe.json',lambda x:x['banner']=='Collapsed')
            else:
                time.sleep(3);snapshot=wait(case/'notice-probe.json');assert snapshot['checks']==0
            assert snapshot['downloads']==0 and snapshot['popupCount']==0
            (case/'release-app').write_text('normal close');assert app.wait(20)==0
            saved=json.loads((profile/'settings.json').read_text());assert saved['UpdateMode']==mode and saved['ShowHidden']
            results.append({'mode':mode,'passed':True,'checks':snapshot['checks'],'downloads':snapshot['downloads']})
        finally:
            if app.running():(case/'release-app').write_text('cleanup');app.wait(60)
            app.close()
    report={'success':True,'realStartupScheduler':True,'privateDesktop':True,'productionInstallation':False,'results':results}
    (root/'verification.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))

if __name__=='__main__':main()
