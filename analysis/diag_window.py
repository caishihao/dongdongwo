import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
k = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

ctypes.windll.user32.SetProcessDPIAware()

print("primary (physical):", u.GetSystemMetrics(0), "x", u.GetSystemMetrics(1))
print("virtual desktop L,T,R,B:", u.GetSystemMetrics(76), u.GetSystemMetrics(77),
      u.GetSystemMetrics(78), u.GetSystemMetrics(79))
print("monitors:", u.GetSystemMetrics(80))

# 找出所有 BongoCatRemix 进程的顶层窗口
target_pids = set()
n = 4096
ids = (wintypes.DWORD * n)()
cb = wintypes.DWORD()
if psapi.EnumProcesses(ids, ctypes.sizeof(ids), ctypes.byref(cb)):
    import os
    count = cb.value // ctypes.sizeof(wintypes.DWORD)
    for i in range(count):
        pid = ids[i]
        h = k.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if h:
            buf2 = ctypes.create_unicode_buffer(260)
            size = wintypes.DWORD(260)
            if k.QueryFullProcessImageNameW(h, 0, buf2, ctypes.byref(size)):
                if "BongoCatRemix" in buf2.value:
                    target_pids.add(pid)
            k.CloseHandle(h)
print("BongoCatRemix pids:", target_pids)

buf = ctypes.create_unicode_buffer(256)
clsbuf = ctypes.create_unicode_buffer(256)

def get_wtext(h):
    u.GetWindowTextW(h, buf, 256)
    return buf.value

def get_cls(h):
    u.GetClassNameW(h, clsbuf, 256)
    return clsbuf.value

rows = []
@ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
def cb(hwnd, lparam):
    pid = wintypes.DWORD()
    u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value in target_pids:
        r = wintypes.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        rows.append({
            "hwnd": hex(hwnd), "pid": pid.value,
            "title": get_wtext(hwnd), "class": get_cls(hwnd),
            "rect": (r.left, r.top, r.right, r.bottom),
            "visible": bool(u.IsWindowVisible(hwnd)),
        })
    return True

u.EnumWindows(cb, 0)
for row in rows:
    print(row)

# 另外枚举显示器（EnumDisplayMonitors）验证布局
MONITORINFO = ctypes.Structure
class RECTw(wintypes.RECT):
    pass
