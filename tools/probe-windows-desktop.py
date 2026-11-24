import ctypes as c
import json
import os
import shutil
import subprocess
import tempfile
import time
import _winapi
from ctypes import wintypes as w
from hashlib import sha256
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
USER = c.WinDLL('user32', use_last_error=True)
CALLBACK = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)


def bind(name, result, *args):
    fn = getattr(USER, name)
    fn.restype = result
    fn.argtypes = list(args)
    return fn


create_desktop = bind('CreateDesktopW', w.HANDLE, w.LPCWSTR, w.LPCWSTR, c.c_void_p,
                      w.DWORD, w.DWORD, c.c_void_p)
close_desktop = bind('CloseDesktop', w.BOOL, w.HANDLE)
enum_windows = bind('EnumDesktopWindows', w.BOOL, w.HANDLE, CALLBACK, w.LPARAM)
enum_children = bind('EnumChildWindows', w.BOOL, w.HWND, CALLBACK, w.LPARAM)
get_pid = bind('GetWindowThreadProcessId', w.DWORD, w.HWND, c.POINTER(w.DWORD))
get_class = bind('GetClassNameW', c.c_int, w.HWND, w.LPWSTR, c.c_int)
get_title = bind('GetWindowTextW', c.c_int, w.HWND, w.LPWSTR, c.c_int)
visible = bind('IsWindowVisible', w.BOOL, w.HWND)
post = bind('PostMessageW', w.BOOL, w.HWND, w.UINT, w.WPARAM, w.LPARAM)
get_menu = bind('GetMenu', w.HMENU, w.HWND)
get_control_id = bind('GetDlgCtrlID', c.c_int, w.HWND)
submenu = bind('GetSubMenu', w.HMENU, w.HMENU, c.c_int)
menu_count = bind('GetMenuItemCount', c.c_int, w.HMENU)
menu_text = bind('GetMenuStringW', c.c_int, w.HMENU, w.UINT, w.LPWSTR, c.c_int, w.UINT)
send_timeout = bind('SendMessageTimeoutW', w.LPARAM, w.HWND, w.UINT, w.WPARAM,
                    w.LPARAM, w.UINT, w.UINT, c.POINTER(c.c_size_t))
kernel = c.WinDLL('kernel32', use_last_error=True)


class Startup(c.Structure):
    _fields_ = [('cb', w.DWORD), ('reserved', w.LPWSTR), ('desktop', w.LPWSTR),
                ('title', w.LPWSTR), ('x', w.DWORD), ('y', w.DWORD), ('x_size', w.DWORD),
                ('y_size', w.DWORD), ('x_chars', w.DWORD), ('y_chars', w.DWORD),
                ('fill', w.DWORD), ('flags', w.DWORD), ('show', w.WORD), ('reserved_size', w.WORD),
                ('reserved_bytes', c.c_void_p), ('stdin', w.HANDLE), ('stdout', w.HANDLE), ('stderr', w.HANDLE)]


class ProcessInfo(c.Structure):
    _fields_ = [('process', w.HANDLE), ('thread', w.HANDLE), ('pid', w.DWORD), ('tid', w.DWORD)]


create_process = kernel.CreateProcessW
create_process.argtypes = [w.LPCWSTR, w.LPWSTR, c.c_void_p, c.c_void_p, w.BOOL, w.DWORD,
                          c.c_void_p, w.LPCWSTR, c.POINTER(Startup), c.POINTER(ProcessInfo)]
create_process.restype = w.BOOL


class DesktopProcess:
    def __init__(self, args, directory, desktop_name):
        info = Startup(cb=c.sizeof(Startup), desktop=desktop_name)
        proc = ProcessInfo()
        command = c.create_unicode_buffer(subprocess.list2cmdline(args))
        if not create_process(args[0], command, None, None, False, 0, None, str(directory), c.byref(info), c.byref(proc)):
            raise c.WinError(c.get_last_error())
        _winapi.CloseHandle(proc.thread)
        self.pid = proc.pid
        self.handle = proc.process

    def poll(self):
        if _winapi.WaitForSingleObject(self.handle, 0) == _winapi.WAIT_TIMEOUT:
            return None
        return _winapi.GetExitCodeProcess(self.handle)

    def close(self):
        if _winapi.WaitForSingleObject(self.handle, 3000) == _winapi.WAIT_TIMEOUT:
            _winapi.TerminateProcess(self.handle, 1)
            _winapi.WaitForSingleObject(self.handle, 3000)
        _winapi.CloseHandle(self.handle)


def props(fn, hwnd):
    buffer = c.create_unicode_buffer(768)
    fn(hwnd, buffer, len(buffer))
    return buffer.value


def menu_items(menu, depth=0):
    rows = []
    if not menu or depth > 4:
        return rows
    for index in range(menu_count(menu)):
        text = c.create_unicode_buffer(300)
        menu_text(menu, index, text, len(text), 0x400)
        children = menu_items(submenu(menu, index), depth + 1)
        if text.value or children:
            rows.append({'title': text.value, 'children': children})
    return rows


def get_windows(desktop, pid):
    windows = []

    @CALLBACK
    def collect(hwnd, _):
        owner = w.DWORD()
        get_pid(hwnd, c.byref(owner))
        if owner.value != pid or not visible(hwnd):
            return True
        children = []

        @CALLBACK
        def child(child_hwnd, _):
            if visible(child_hwnd) and len(children) < 100:
                cls = props(get_class, child_hwnd)
                item = {'class': cls, 'title': props(get_title, child_hwnd),
                        'handle': child_hwnd, 'control_id': get_control_id(child_hwnd)}
                if cls == 'SysListView32':
                    count = c.c_size_t()
                    if send_timeout(child_hwnd, 0x1004, 0, 0, 2, 500, c.byref(count)):
                        item['item_count'] = count.value
                children.append(item)
            return True

        enum_children(hwnd, child, 0)
        windows.append({'handle': hwnd, 'class': props(get_class, hwnd),
                        'title': props(get_title, hwnd), 'children': children,
                        'menu': menu_items(get_menu(hwnd))})
        return True

    enum_windows(desktop, collect, 0)
    return windows


def run(desktop, desktop_name, programs):
    started = []
    results = []
    try:
        for label, executable, source, extra_args in programs:
            args = [str(executable), *extra_args]
            if source:
                value = str(source)
                args.append(f'-MAIL_FILE={value}' if label == 'Windows MBox Viewer' else value)
            try:
                proc = DesktopProcess(args, executable.parent, desktop_name)
            except OSError as error:
                results.append({'program': label, 'executable': executable.name, 'pid': None,
                                'argument_fixture': source.name if source else None,
                                'launch_error': str(error), 'exit_code_at_observation': None, 'windows': []})
                continue
            started.append((label, proc, executable, source))
        deadline = time.monotonic() + 15
        time.sleep(6)
        while time.monotonic() < deadline:
            if all(proc.poll() is not None or get_windows(desktop, proc.pid) for _, proc, _, _ in started):
                break
            time.sleep(0.5)
        for _ in range(5):
            for label, proc, _, _ in started:
                if label == 'Windows MBox Viewer':
                    for window in get_windows(desktop, proc.pid):
                        if window['title'] in {'About mboxview', 'Did you know?', 'Data Folder Configuration'}:
                            button = next((child for child in window['children']
                                           if child['title'] in {'Close', 'OK'}), None)
                            if button:
                                post(window['handle'], 0x0111, button['control_id'], button['handle'])
            time.sleep(1)
        for label, proc, executable, source in started:
            windows = get_windows(desktop, proc.pid)
            results.append({'program': label, 'executable': executable.name, 'pid': proc.pid,
                            'argument_fixture': source.name if source else None,
                            'exit_code_at_observation': proc.poll(), 'windows': windows})
            for window in windows:
                post(window['handle'], 0x0010, 0, 0)
    finally:
        for _, proc, _, _ in started:
            proc.close()
    return results


def stage_systools(target):
    shutil.copytree(ROOT / 'extracted' / 'systools' / '{app}', target)
    for filename in ['SysTools Outlook PST Viewer.exe', 'ChilkatDotNet2.dll', 'msvcr100.dll', 'IEShims.dll']:
        output = target / filename
        source = target / (output.stem + ',2' + output.suffix)
        if source.is_file():
            shutil.copy2(source, output)
    return target / 'SysTools Outlook PST Viewer.exe'


def main():
    name = 'ForensicMailViewerResearch' + str(os.getpid())
    access = 0x01FF
    desktop = create_desktop(name, None, None, 0, access, None)
    if not desktop:
        raise c.WinError(c.get_last_error())
    data = ROOT / 'fixtures'
    files = [data / x for x in ('01-original.eml', 'synthetic.mbox', 'tika-test-outlook.msg', 'tika-testPST.pst')]
    before = {file.name: sha256(file.read_bytes()).hexdigest() for file in files}
    extracted = ROOT / 'extracted'
    try:
        with tempfile.TemporaryDirectory(prefix='ForensicMailViewerRuntime') as temp:
            test_files = []
            for file in files:
                copy = Path(temp) / file.name
                shutil.copy2(file, copy)
                test_files.append(copy)
            eml, mbox, msg, pst = test_files
            sys_tools = stage_systools(Path(temp) / 'SysTools')
            mbox_app = Path(temp) / 'MBox'
            shutil.copytree(extracted / 'windows-mbox', mbox_app)
            config = mbox_app / 'Config' / 'MBoxViewer.config.sample'
            if config.is_file():
                shutil.copy2(config, config.with_name('MBoxViewer.config'))
            programs = [
                ('MiTeC Mail Viewer', extracted / 'mitec' / 'MailView.exe', eml, []),
                ('Windows MBox Viewer', mbox_app / 'mboxview64.exe', mbox, []),
                ('CoolUtils Mail Viewer', extracted / 'coolutils' / '{app}' / 'MailViewer.exe', msg, []),
                ('SysTools PST Viewer', sys_tools, pst, []),
                ('Aid4Mail', extracted / 'aid4mail' / '{app}' / 'Aid4Mail.exe', None, []),
            ]
            results = run(desktop, name, programs)
            readback = {file.name: sha256(file.read_bytes()).hexdigest() for file in test_files if file.is_file()}
        report = {'desktop': name, 'desktop_switched': False, 'observation': 'startup and submitted command-line arguments',
                  'fixture_sha_before': before, 'fixture_copy_sha_after': readback, 'programs': results}
        (ROOT / 'evidence' / 'native-startup.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf8')
        for program in results:
            print(program['program'], program['exit_code_at_observation'], [(x['class'], x['title']) for x in program['windows']])
    finally:
        close_desktop(desktop)


if __name__ == '__main__':
    main()
