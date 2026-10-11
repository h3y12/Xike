# -*- coding: utf-8 -*-
"""从编译好的 exe 里把图标抽出来存成 PNG，用来确认图标真的写进去了。

为什么要单独验一下：csproj 里 <ApplicationIcon> 写错路径、或者 .ico 的条目格式
系统不认时，编译照样成功，只是图标悄悄没进 exe —— 只看「编译通过」是看不出来的。

坑：Win32 的 GDI 句柄是 64 位的，ctypes 不声明 argtypes/restype 会当成 32 位 int 传，
直接 OverflowError（"int too long to convert"）。凡是句柄参数都要显式声明。

用法：
    python scripts/verify_exe_icon.py <exe路径> <输出目录>
"""

import ctypes
import ctypes.wintypes as wt
import os
import sys

from PIL import Image

SIZES = [16, 32, 48, 256]

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
shell32 = ctypes.windll.shell32

# ---- 句柄一律按 void* 传，否则 64 位句柄会被当成 int 截断 ----
shell32.ExtractIconExW.argtypes = [wt.LPCWSTR, ctypes.c_int,
                                   ctypes.POINTER(wt.HICON), ctypes.POINTER(wt.HICON), wt.UINT]
shell32.ExtractIconExW.restype = wt.UINT

user32.PrivateExtractIconsW.argtypes = [wt.LPCWSTR, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                        ctypes.POINTER(wt.HICON), ctypes.POINTER(wt.UINT),
                                        wt.UINT, wt.UINT]
user32.PrivateExtractIconsW.restype = wt.UINT

user32.GetIconInfo.argtypes = [wt.HICON, ctypes.c_void_p]
user32.GetIconInfo.restype = wt.BOOL

gdi32.GetObjectW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
gdi32.GetObjectW.restype = ctypes.c_int

gdi32.GetDIBits.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.UINT, wt.UINT,
                            ctypes.c_void_p, ctypes.c_void_p, wt.UINT]
gdi32.GetDIBits.restype = ctypes.c_int

gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
user32.DestroyIcon.argtypes = [wt.HICON]
user32.GetDC.restype = ctypes.c_void_p
user32.GetDC.argtypes = [wt.HWND]
user32.ReleaseDC.argtypes = [wt.HWND, ctypes.c_void_p]


class ICONINFO(ctypes.Structure):
    _fields_ = [("fIcon", wt.BOOL), ("xHotspot", wt.DWORD), ("yHotspot", wt.DWORD),
                ("hbmMask", ctypes.c_void_p), ("hbmColor", ctypes.c_void_p)]


class BITMAP(ctypes.Structure):
    _fields_ = [("bmType", wt.LONG), ("bmWidth", wt.LONG), ("bmHeight", wt.LONG),
                ("bmWidthBytes", wt.LONG), ("bmPlanes", wt.WORD), ("bmBitsPixel", wt.WORD),
                ("bmBits", ctypes.c_void_p)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def pick_icon(exe, index, size):
    """先要在指定尺寸上取；取不到就退回 ExtractIconEx 给的那张。"""
    hicon = wt.HICON()
    got = user32.PrivateExtractIconsW(exe, index, size, size, ctypes.byref(hicon), None, 1, 0)
    if got > 0 and hicon:
        return hicon

    large, small = wt.HICON(), wt.HICON()
    if shell32.ExtractIconExW(exe, index, ctypes.byref(large), ctypes.byref(small), 1) <= 0:
        return None
    return large if size >= 32 else small


def hicon_to_image(h):
    """HICON → PIL Image：把 ICONINFO 里的彩色位图按 32 位 BGRA 读出来。"""
    info = ICONINFO()
    if not user32.GetIconInfo(h, ctypes.byref(info)):
        return None

    bm = BITMAP()
    gdi32.GetObjectW(info.hbmColor, ctypes.sizeof(bm), ctypes.byref(bm))
    w, hh = bm.bmWidth, bm.bmHeight
    if w <= 0 or hh <= 0:
        return None

    hdc = user32.GetDC(None)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -hh          # 负数 = 自上而下
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0

    buf = ctypes.create_string_buffer(w * hh * 4)
    gdi32.GetDIBits(hdc, info.hbmColor, 0, hh, buf, ctypes.byref(bi), 0)
    user32.ReleaseDC(None, hdc)

    img = Image.frombuffer("RGBA", (w, hh), buf, "raw", "BGRA", 0, 1).copy()

    user32.DestroyIcon(h)
    if info.hbmColor:
        gdi32.DeleteObject(info.hbmColor)
    if info.hbmMask:
        gdi32.DeleteObject(info.hbmMask)
    return img


def main():
    exe = os.path.abspath(sys.argv[1])
    out_dir = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else "_verify")
    os.makedirs(out_dir, exist_ok=True)

    if not os.path.exists(exe):
        print("找不到 " + exe)
        return 1

    print("exe = %s（%d 字节）" % (exe, os.path.getsize(exe)))

    groups = shell32.ExtractIconExW(exe, -1, None, None, 0)
    print("exe 里的图标组数 = %d" % groups)
    if groups <= 0:
        print("FAIL  exe 里没有图标")
        return 1

    made = []
    for size in SIZES:
        h = pick_icon(exe, 0, size)
        if not h:
            print("FAIL  取不到 %dpx" % size)
            continue
        img = hicon_to_image(h)
        if img is None:
            print("FAIL  %dpx 转位图失败" % size)
            continue
        path = os.path.join(out_dir, "exe-icon-%d.png" % size)
        img.save(path)
        made.append((size, img, path))
        print("PASS  %dpx → %s（实际 %dx%d）" % (size, path, img.size[0], img.size[1]))

    if not made:
        return 1

    # 拼一张对照图：小尺寸放大看像素，大尺寸原样
    cell = 256
    sheet = Image.new("RGBA", (cell * len(made) + 16, cell + 8), (0xF2, 0xF3, 0xF5, 255))
    for i, (_, img, _) in enumerate(made):
        sheet.alpha_composite(img.resize((cell, cell), Image.NEAREST), (8 + i * cell, 4))
    strip = os.path.join(out_dir, "exe-icon-strip.png")
    sheet.save(strip)
    print("对照图 → " + strip)

    return 0


if __name__ == "__main__":
    sys.exit(main())
