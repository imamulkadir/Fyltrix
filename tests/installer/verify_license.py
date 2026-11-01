"""Inspect the native agreement wizard on a private desktop; cancel before installation."""
from pathlib import Path
import argparse,ctypes,hashlib,json,os,time,winreg
from ctypes import wintypes as W
from verify_installer import PrivateProcess,registration

U=ctypes.WinDLL('user32',use_last_error=True)
CALLBACK=ctypes.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
U.EnumDesktopWindows.argtypes=[W.HANDLE,CALLBACK,W.LPARAM]
U.EnumChildWindows.argtypes=[W.HWND,CALLBACK,W.LPARAM]
U.GetClassNameW.argtypes=[W.HWND,W.LPWSTR,ctypes.c_int]
U.IsWindowVisible.argtypes=[W.HWND]; U.IsWindowEnabled.argtypes=[W.HWND]
U.SendMessageTimeoutW.argtypes=[W.HWND,W.UINT,W.WPARAM,W.LPARAM,W.UINT,W.UINT,ctypes.POINTER(ctypes.c_size_t)]
U.SendMessageTimeoutW.restype=ctypes.c_size_t
U.PostMessageW.argtypes=[W.HWND,W.UINT,W.WPARAM,W.LPARAM]

def windows(desktop=None,parent=None):
    result=[]
    @CALLBACK
    def add(hwnd,_):result.append(hwnd);return True
    if parent is not None:U.EnumChildWindows(parent,add,0)
    else:U.EnumDesktopWindows(desktop,add,0)
    return result

def classname(hwnd):
    value=ctypes.create_unicode_buffer(256);U.GetClassNameW(hwnd,value,256);return value.value

def text(hwnd):
    value=ctypes.create_unicode_buffer(16384); result=ctypes.c_size_t()
    U.SendMessageTimeoutW(hwnd,0x000D,len(value),ctypes.cast(value,ctypes.c_void_p).value,2,1000,ctypes.byref(result))
    return value.value

def controls(hwnd):return [(child,text(child)) for child in windows(parent=hwnd) if U.IsWindowVisible(child)]
def until(condition,message,seconds=30):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        result=condition()
        if result:return result
        time.sleep(.05)
    raise AssertionError(message)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--installer',type=Path,required=True);parser.add_argument('--work-root',type=Path,required=True);args=parser.parse_args()
    assert os.name=='nt' and not ctypes.windll.shell32.IsUserAnAdmin()
    info=json.loads((args.installer.parent/'installer-build.json').read_text(encoding='utf-8-sig'))
    assert info['mode']=='Sandbox' and info['appId']=='Fyltrix.Sandbox.PerUser'
    assert hashlib.sha256(args.installer.read_bytes()).hexdigest()==info['sha256']
    assert registration() is None and registration(winreg.HKEY_LOCAL_MACHINE) is None
    root=args.work_root.resolve();repo=Path(__file__).resolve().parents[2]
    assert not root.exists() and not root.is_relative_to(repo)
    root.mkdir();target=root/'never-installed'
    setup=PrivateProcess(args.installer.resolve(),['/DIR='+str(target),'/LOG='+str(root/'setup.log')])
    wizard=None
    try:
        wizard=until(lambda:next((w for w in windows(desktop=setup.desktop) if U.IsWindowVisible(w) and 'wizardform' in classname(w).lower()),None),'wizard did not open')
        rows=until(lambda:controls(wizard),'no controls')
        until(lambda:any('license agreement' in value.lower() for _,value in controls(wizard)),'agreement must be the first wizard page')
        rows=controls(wizard)
        next_button=next(w for w,value in rows if value.replace('&','').strip().startswith('Next'))
        accept=next(w for w,value in rows if value.replace('&','').strip().lower()=='i accept the agreement')
        assert not U.IsWindowEnabled(next_button),'Next must require acceptance'
        assert any('MIT License' in value and 'Fyltrix contributors' in value for _,value in rows),'actual MIT text missing'
        assert not target.exists() and registration() is None
        U.PostMessageW(accept,0x00F5,0,0) # BM_CLICK, only on this test's private desktop
        until(lambda:U.IsWindowEnabled(next_button),'acceptance did not enable Next')
        U.PostMessageW(next_button,0x00F5,0,0)
        until(lambda:any('select destination location' in value.lower() for _,value in controls(wizard)),'directory page must follow agreement')
        assert not target.exists() and registration() is None
        U.PostMessageW(wizard,0x0010,0,0) # close without proceeding to installation
        def confirm_exit():
            for top in windows(desktop=setup.desktop):
                if not U.IsWindowVisible(top):continue
                for button,value in controls(top):
                    if value.replace('&','').strip().lower()=='yes':U.PostMessageW(button,0x00F5,0,0);return True
            return not setup.running()
        until(confirm_exit,'cancel confirmation missing')
        setup.wait(20)
        assert not target.exists() and registration() is None
        report={'success':True,'agreementFirst':True,'mitTextShown':True,'nextDisabledUntilAccept':True,'directoryAfterAgreement':True,'cancelCreatedNoInstallation':True,'privateDesktop':True,'productionInstallerExecuted':False}
        (root/'verification.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
    finally:
        if setup.running() and wizard is not None:
            U.PostMessageW(wizard,0x0010,0,0)
            for top in windows(desktop=setup.desktop):
                for button,value in controls(top):
                    if value.replace('&','').strip().lower()=='yes':U.PostMessageW(button,0x00F5,0,0)
            setup.wait(20)
        setup.close()

if __name__=='__main__':main()
