# -*- coding: utf-8 -*-
"""给 TimeGuard.exe 建一个桌面快捷方式（直接拼 .lnk 二进制，不使用 COM）。

为什么不走 WScript.Shell：本机策略禁止实例化 COM 对象（"COM object instantiation
can run arbitrary code"），Windows Script Host / PowerShell 那条路走不通。
.lnk 的格式是公开的（MS-SHLLINK），这里直接按规范拼二进制。

**必须有 IDList**（flag 位 0）。实测：把一个能用的快捷方式里的 IDList 段删掉、
只留 LinkInfo，ShellExecuteW 立刻返回 SE_ERR_NOASSOC(31)，双击提示"没有关联的应用"——
壳在解析 .lnk 时会优先走 IDList，光有 LinkInfo 不够。
IDList 不手拼（里面是 BEEF0004 那套短名/长名/时间戳，手拼必错），
改成调 shell 自己的 SHParseDisplayName 拿一份现成的字节。

桌面路径从注册表 HKCU\\...\\Explorer\\Shell Folders 读，这样桌面被 OneDrive 接管
或改成中文名「桌面」时也能找对地方。

用法：
    python scripts/make_shortcut.py <目标exe或文件夹> [快捷方式名]
                                  [--icon <图标文件>] [--desc <说明>]

目标也可以是**文件夹**（如发布产物目录）：IDList 和 LinkInfo 对目录同样适用。
此时用 `--icon` 指一个现成的 exe/dll 拿图标（文件夹自身没有图标可用）。
"""

import ctypes
import ctypes.wintypes as wt
import os
import struct
import sys

try:
    import winreg
except ImportError:                     # 非 Windows 上只做语法检查时用
    winreg = None

# ---- LinkFlags（MS-SHLLINK 2.1.1）----
HAS_LINK_TARGET_ID_LIST = 0x00000001
HAS_LINK_INFO = 0x00000002
HAS_NAME = 0x00000004
HAS_WORKING_DIR = 0x00000010
HAS_ICON_LOCATION = 0x00000040
IS_UNICODE = 0x00000080

SW_SHOWNORMAL = 1
FILE_ATTRIBUTE_ARCHIVE = 0x20
DRIVE_FIXED = 3

# ShellLink 的 CLSID {00021401-0000-0000-C000-000000000046}。
# 坑：GUID 落盘不是按「大端字面量」排的，Data1/2/3 各自小端，Data4 原样 ——
# 所以字节序列是 01 14 02 00 00 00 00 00 C0 ...，写成 00 02 14 01... 系统就不认这个文件，
# ShellExecute 会回 SE_ERR_NOASSOC(31)，双击提示「没有关联的应用」。
LINK_CLSID = bytes.fromhex("01140200" "0000" "0000" "C000000000000046")


def desktop_dir():
    """桌面路径：先问注册表，问不到再退回 ~\\Desktop。"""
    if winreg is not None:
        key = r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as k:
                path, _ = winreg.QueryValueEx(k, "Desktop")
                path = os.path.expandvars(path)
                if os.path.isdir(path):
                    return path
        except OSError:
            pass
    return os.path.join(os.path.expanduser("~"), "Desktop")


def item_id_list(path):
    """用 shell 自己的 SHParseDisplayName 拿目标的 PIDL 字节，直接当 IDList 用。

    PIDL = 一串「大小(2字节) + 数据」的项，末尾跟一个 2 字节的 0。
    拿回来的这块内存由 shell 分配，读完用 CoTaskMemFree 还回去。
    """
    shell32 = ctypes.windll.shell32
    ole32 = ctypes.windll.ole32

    shell32.SHParseDisplayName.argtypes = [wt.LPCWSTR, ctypes.c_void_p,
                                           ctypes.POINTER(ctypes.c_void_p),
                                           wt.DWORD, ctypes.POINTER(wt.DWORD)]
    shell32.SHParseDisplayName.restype = ctypes.c_long

    try:
        ole32.CoInitializeEx(None, 2)       # APARTMENTTHREADED，shell 接口要求
    except Exception:
        pass

    pidl = ctypes.c_void_p()
    attrs = wt.DWORD()
    hr = shell32.SHParseDisplayName(path, None, ctypes.byref(pidl), 0, ctypes.byref(attrs))
    if hr < 0 or not pidl:
        raise OSError("SHParseDisplayName 失败，HRESULT=%#x，路径 %s" % (hr & 0xFFFFFFFF, path))

    raw = ctypes.cast(pidl, ctypes.POINTER(ctypes.c_ubyte))
    n = 0
    while True:
        size = raw[n] | (raw[n + 1] << 8)
        n += size if size else 2       # 末尾的 0 项只占 2 字节
        if size == 0:
            break

    blob = bytes(bytearray(raw[i] for i in range(n)))
    ole32.CoTaskMemFree(pidl)
    return blob


def unicode_string(text):
    """StringData 里的一节：2 字节字符数 + UTF-16LE 本体（不含结尾的 \\0）。"""
    data = text.encode("utf-16-le")
    return struct.pack("<H", len(text)) + data


def ansi_z(text):
    return text.encode("mbcs") + b"\x00"


def build_link_info(target):
    """LinkInfo：给一个 VolumeID + LocalBasePath，Shell 就靠它定位目标。"""
    volume_label = b"\x00"                                   # 无卷标，给个空串占位
    volume_id = struct.pack("<IIII", 16 + len(volume_label), DRIVE_FIXED, 0, 0x10) + volume_label
    local_base_path = ansi_z(target)
    common_suffix = b"\x00"

    # 头部 7 个 DWORD，后面依次是 VolumeID / LocalBasePath / CommonPathSuffix
    header = 7 * 4
    volume_off = header
    base_off = volume_off + len(volume_id)
    suffix_off = base_off + len(local_base_path)
    total = suffix_off + len(common_suffix)

    return (struct.pack("<IIIIIII", total, 0x1C, 0x1,   # size, headerSize, flags=VolumeIDAndLocalBasePath
                        volume_off, base_off, 0, suffix_off)
            + volume_id + local_base_path + common_suffix)


def build_lnk(target, workdir, icon_location, description):
    flags = (HAS_LINK_TARGET_ID_LIST | HAS_LINK_INFO
             | HAS_WORKING_DIR | HAS_ICON_LOCATION | IS_UNICODE)
    if description:
        flags |= HAS_NAME

    header = (struct.pack("<I", 0x4C)          # HeaderSize，固定 0x4C
              + LINK_CLSID                      # LinkCLSID，固定
              + struct.pack("<I", flags)
              + struct.pack("<I", FILE_ATTRIBUTE_ARCHIVE)
              + struct.pack("<Q", 0)            # CreationTime
              + struct.pack("<Q", 0)            # AccessTime
              + struct.pack("<Q", 0)            # WriteTime
              + struct.pack("<I", 0)            # FileSize
              + struct.pack("<i", 0)            # IconIndex
              + struct.pack("<I", SW_SHOWNORMAL)
              + struct.pack("<H", 0)            # HotKey
              + struct.pack("<H", 0)            # Reserved
              + struct.pack("<I", 0)            # Reserved2
              + struct.pack("<I", 0))           # Reserved3

    idlist = item_id_list(target)

    # LinkTargetIDList：2 字节的 IDListSize（= 后面的字节数，**不含自己**）+ IDList 本体
    body = struct.pack("<H", len(idlist)) + idlist
    body += build_link_info(target)

    # StringData 的顺序是规定死的，而且只写 flags 里声明了的那几节
    if description:
        body += unicode_string(description)
    body += unicode_string(workdir)             # WORKING_DIR
    body += unicode_string(icon_location)       # ICON_LOCATION

    body += struct.pack("<I", 0)                # ExtraData 终止块
    return header + body


def parse_args(argv):
    """位置参数（目标、名称）+ 可选 --icon / --desc。"""
    positional, icon, desc = [], None, None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--icon" and i + 1 < len(argv):
            icon = argv[i + 1]; i += 2
        elif a == "--desc" and i + 1 < len(argv):
            desc = argv[i + 1]; i += 2
        else:
            positional.append(a); i += 1
    return positional, icon, desc


def main():
    positional, icon, desc = parse_args(sys.argv[1:])
    if not positional:
        print(__doc__)
        return 2

    target = os.path.abspath(positional[0])
    name = positional[1] if len(positional) > 1 else "TimeGuard"
    if not name.lower().endswith(".lnk"):
        name += ".lnk"

    if not os.path.exists(target):
        print("FAIL  找不到目标 " + target)
        return 1

    is_dir = os.path.isdir(target)
    # 图标：显式给了就用给的；否则用目标自身（仅对文件可行 —— 文件夹没有可取的图标）
    icon_location = (os.path.abspath(icon) + ",0") if icon else (target + ",0")
    description = desc if desc else "TimeGuard 早睡健康分 / 倒计时挂件"

    lnk_dir = desktop_dir()
    lnk_path = os.path.join(lnk_dir, name)
    workdir = target if is_dir else os.path.dirname(target)

    with open(lnk_path, "wb") as f:
        f.write(build_lnk(target, workdir, icon_location, description))

    print("桌面目录  = " + lnk_dir)
    print("快捷方式  = " + lnk_path)
    print("目标      = " + target + ("（文件夹）" if is_dir else "（文件）"))
    print("工作目录  = " + workdir)
    print("图标来源  = " + icon_location)
    print("大小      = %d 字节" % os.path.getsize(lnk_path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
