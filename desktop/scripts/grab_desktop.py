# -*- coding: utf-8 -*-
"""把桌面（图标那一层）抓成图片，**不改变用户屏幕上正在显示的东西**。

被别的窗口挡住的桌面照样能抓到：走 PrintWindow(PW_RENDERFULLCONTENT)，
是让桌面窗口把内容画到我们自己的内存 DC 上，不是截屏。

桌面图标其实画在  Progman|WorkerW  →  SHELLDLL_DefView  →  SysListView32
这一串窗口里的最后一层。

用法：
    python scripts/grab_desktop.py [输出png]
"""

import ctypes
import ctypes.wintypes as wt
import os
import sys

from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else "_verify/desktop-icons.png"

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

user32.FindWindowW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
user32.FindWindowW.restype = ctypes.c_void_p
user32.FindWindowExW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.c_wchar_p, ctypes.c_wchar_p]
user32.FindWindowExW.restype = ctypes.c_void_p
user32.GetDC.argtypes = [ctypes.c_void_p]
user32.GetDC.restype = ctypes.c_void_p
user32.PrintWindow.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.UINT]
user32.PrintWindow.restype = wt.BOOL
gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
gdi32.CreateDIBSection.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.UINT,
                                   ctypes.POINTER(ctypes.c_void_p),
                                   ctypes.c_void_p, wt.DWORD]
gdi32.CreateDIBSection.restype = ctypes.c_void_p
gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.SelectObject.restype = ctypes.c_void_p


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def find_desktop_view():
    """返回 (窗口句柄, 说明)。优先最里层的 SysListView32。"""
    progman = user32.FindWindowW("Progman", None)
    cands = []
    if progman:
        dv = user32.FindWindowExW(progman, None, "SHELLDLL_DefView", None)
        if dv:
            cands.append(dv)
    h = None
    while True:
        h = user32.FindWindowExW(None, h, "WorkerW", None)
        if not h:
            break
        dv = user32.FindWindowExW(h, None, "SHELLDLL_DefView", None)
        if dv:
            cands.append(dv)
    # 先从后往前（后出现的 WorkerW 往往才是顶层），再看能否钻到 listview
    for dv in reversed(cands):
        lv = user32.FindWindowExW(dv, None, "SysListView32", None)
        if lv:
            return lv, "SysListView32"
    if cands:
        return cands[-1], "SHELLDLL_DefView"
    return None, "没找到桌面视图窗口"


def grab(hwnd):
    rc = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rc))
    w = rc.right - rc.left
    h = rc.bottom - rc.top
    if w <= 0 or h <= 0:
        return None, (0, 0)

    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0
    bits = ctypes.c_void_p()
    hbm = gdi32.CreateDIBSection(hdc_mem, ctypes.byref(bi), 0,
                                 ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(hdc_mem, hbm)
    ok = user32.PrintWindow(hwnd, hdc_mem, 2)     # PW_RENDERFULLCONTENT
    gdi32.GdiFlush()
    raw = ctypes.string_at(bits.value, w * h * 4)
    img = Image.frombytes("RGBA", (w, h), raw, "raw", "BGRA")
    gdi32.SelectObject(hdc_mem, old)
    gdi32.DeleteObject(hbm)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)
    return img, (ok, (w, h))


def main():
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass
    hwnd, label = find_desktop_view()
    print("桌面视图窗口：%s  hwnd=%s" % (label, hwnd))
    if not hwnd:
        return
    img, (ok, size) = grab(hwnd)
    if img is None:
        print("窗口尺寸异常")
        return
    print("PrintWindow 返回 %s，抓到 %dx%d" % (ok, size[0], size[1]))
    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    img.convert("RGB").save(OUT)
    print("已保存 -> %s" % OUT)


if __name__ == "__main__":
    main()
