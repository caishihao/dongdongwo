"""阿瓜敲击动画连拍诊断工具：向桌宠窗口发送按键并连拍帧序列。"""
import ctypes
import time
from pathlib import Path

import ctypes.wintypes as wt

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

OUT = Path(r"D:\BongoCat\analysis\agua-frames")
OUT.mkdir(parents=True, exist_ok=True)
for old in OUT.glob("frame-*.png"):
    old.unlink()

X, Y, W, H = 2365, 1363, 195, 165  # 主窗物理坐标（日志位置验证）
VK_A = 0x41

def tap():
    user32.keybd_event(VK_A, 0, 0, 0)
    time.sleep(0.03)
    user32.keybd_event(VK_A, 0, 2, 0)  # KEYEVENTF_KEYUP

def grab(idx):
    img = ImageGrab.grab(bbox=(X, Y, X + W, Y + H))
    img.save(OUT / f"frame-{idx:02d}.png")

time.sleep(1.0)
for i in range(16):
    tap()
    time.sleep(0.02 + (i % 4) * 0.045)
    grab(i)
    time.sleep(0.05)
print(f"已捕获 {len(list(OUT.glob('frame-*.png')))} 帧到 {OUT}")
