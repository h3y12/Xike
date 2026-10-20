# -*- coding: utf-8 -*-
"""用 Windows 自己的接口把图标**真画出来**，看系统实际会显示成什么样。

两个来源，差别正是「图标本身坏」和「shell 图标缓存陈旧」的分水岭：

    ExtractIconExW   直接读文件的图标资源，不经过 shell 缓存
    SHGetFileInfoW   走 shell 这条路（含图标缓存）—— 桌面和资源管理器用的就是它

如果 A 有图、B 是空白，就是缓存陈旧；如果 A 也空白，是图标资源本身的问题。

用法：
    python scripts/probe_shell_icon.py [exe路径] [lnk路径]
"""

import ctypes
import ctypes.wintypes as wt
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "dist", "TimeGuard.exe")
LNK = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    os.environ.get("USERPROFILE", ""), "Desktop", "TimeGuard.lnk")
OUT = os.path.join(ROOT, "_verify", "icon-shell-probe.png")

user32 = ctypes.windll.user32
shell32 = ctypes.windll.shell32
gdi32 = ctypes.windll.gdi32

user32.GetDC.argtypes = [ctypes.c_void_p]
user32.GetDC.restype = ctypes.c_void_p
user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
user32.DrawIconEx.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                              ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                              wt.UINT, ctypes.c_void_p, wt.UINT]
user32.DrawIconEx.restype = wt.BOOL
user32.DestroyIcon.argtypes = [ctypes.c_void_p]

gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
gdi32.DeleteDC.argtypes = [ctypes.c_void_p]
gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.SelectObject.restype = ctypes.c_void_p
gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
gdi32.CreateDIBSection.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.UINT,
                                   ctypes.POINTER(ctypes.c_void_p),
                                   ctypes.c_void_p, wt.DWORD]
gdi32.CreateDIBSection.restype = ctypes.c_void_p

shell32.ExtractIconExW.argtypes = [ctypes.c_wchar_p, ctypes.c_int,
                                   ctypes.POINTER(ctypes.c_void_p),
                                   ctypes.POINTER(ctypes.c_void_p), wt.UINT]
shell32.ExtractIconExW.restype = wt.UINT
shell32.SHGetFileInfoW.argtypes = [ctypes.c_wchar_p, wt.DWORD, ctypes.c_void_p,
                                   wt.UINT, wt.UINT]
shell32.SHGetFileInfoW.restype = ctypes.c_void_p

SHGFI_ICON = 0x00000100
SHGFI_LARGEICON = 0x00000000
SHGFI_SMALLICON = 0x00000001
DI_NORMAL = 0x0003

MAGENTA = (255, 0, 255)


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


class SHFILEINFOW(ctypes.Structure):
    _fields_ = [("hIcon", ctypes.c_void_p), ("iIcon", ctypes.c_int),
                ("dwAttributes", wt.DWORD),
                ("szDisplayName", ctypes.c_wchar * 260),
                ("szTypeName", ctypes.c_wchar * 80)]


def hicon_to_image(hIcon, size):
    """把 HICON 画到一块 32 位 DIB 上；底色刷成品红，没画到的像素就会留成品红。"""
    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = size
    bi.bmiHeader.biHeight = -size            # 负数 = 自上而下，省得翻转
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0           # BI_RGB

    bits = ctypes.c_void_p()
    hbm = gdi32.CreateDIBSection(hdc_mem, ctypes.byref(bi), 0,
                                 ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(hdc_mem, hbm)

    n = size * size
    arr = (ctypes.c_ubyte * (n * 4)).from_address(bits.value)
    for i in range(n):
        arr[i * 4 + 0] = MAGENTA[2]
        arr[i * 4 + 1] = MAGENTA[1]
        arr[i * 4 + 2] = MAGENTA[0]
        arr[i * 4 + 3] = 255

    ok = bool(hIcon) and bool(user32.DrawIconEx(hdc_mem, 0, 0, hIcon, size, size,
                                                0, None, DI_NORMAL))
    gdi32.GdiFlush()
    raw = bytes(bytearray(arr))
    img = Image.frombytes("RGBA", (size, size), raw, "raw", "BGRA")

    gdi32.SelectObject(hdc_mem, old)
    gdi32.DeleteObject(hbm)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)
    return img, ok


def coverage(img):
    """与品红不同的像素占比 —— 也就是「真被画出来」的面积。"""
    px = img.load()
    w, h = img.size
    n = 0
    for y in range(h):
        for x in range(w):
            r, g, b, _ = px[x, y]
            if abs(r - 255) + abs(g - 0) + abs(b - 255) > 24:
                n += 1
    return n / float(w * h)


def from_extract(path, size):
    large = ctypes.c_void_p()
    small = ctypes.c_void_p()
    n = shell32.ExtractIconExW(path, 0, ctypes.byref(large), ctypes.byref(small), 1)
    if not n:
        return None, "ExtractIconEx 一个图标都没取到"
    h = large.value if size >= 32 else small.value
    img, ok = hicon_to_image(h, size)
    if large:
        user32.DestroyIcon(large)
    if small:
        user32.DestroyIcon(small)
    return img, ("drawiconex=%s" % ok)


def from_shell(path, size, big=True):
    info = SHFILEINFOW()
    flags = SHGFI_ICON | (SHGFI_LARGEICON if big else SHGFI_SMALLICON)
    r = shell32.SHGetFileInfoW(path, 0, ctypes.byref(info),
                               ctypes.sizeof(info), flags)
    if not r or not info.hIcon:
        return None, "SHGetFileInfo 没给出图标"
    img, ok = hicon_to_image(info.hIcon, size)
    user32.DestroyIcon(info.hIcon)
    return img, ("iIcon=%d drawiconex=%s" % (info.iIcon, ok))


def main():
    rows = []
    for label, getter in [
        ("ExtractIconEx(exe)   ← 直接读文件资源", lambda: from_extract(EXE, 48)),
        ("SHGetFileInfo(exe)   ← 走 shell/缓存", lambda: from_shell(EXE, 48, True)),
        ("SHGetFileInfo(lnk)   ← 桌面实际用的", lambda: from_shell(LNK, 48, True)),
        ("SHGetFileInfo(lnk) 小图标 16px", lambda: from_shell(LNK, 16, False)),
    ]:
        img, note = getter()
        if img is None:
            rows.append((label, None, 0.0, note))
            print("%-38s  %s" % (label, note))
            continue
        cov = coverage(img)
        rows.append((label, img, cov, note))
        print("%-38s  可见面积 %5.1f%%   %s" % (label, cov * 100, note))

    cell = 48 * 4
    sheet = Image.new("RGB", (cell + 40, (cell + 46) * len(rows) + 10), (245, 246, 248))
    d = ImageDraw.Draw(sheet)
    for i, (label, img, cov, note) in enumerate(rows):
        y = 10 + i * (cell + 46)
        d.text((12, y), label, fill=(30, 30, 30))
        d.text((12, y + 16), "%s  可见 %.1f%%" % (note, cov * 100), fill=(110, 110, 110))
        if img is not None:
            sheet.paste(img.resize((cell, cell), Image.NEAREST).convert("RGB"),
                        (12, y + 34))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sheet.save(OUT)
    print("对照图 -> %s" % OUT)


if __name__ == "__main__":
    main()
