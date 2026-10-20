# -*- coding: utf-8 -*-
"""诊断图标：把「图标本身是坏的」和「Windows 图标缓存陈旧」区分开。只读，不改动任何文件。

查三件事：
  1. Assets/TimeGuard.ico 每个条目能不能解出可见像素（尺寸 / 格式 / 有效像素占比）
  2. dist/TimeGuard.exe 里到底有没有图标资源、有几个条目、每条是 PNG 还是 DIB
  3. 桌面 .lnk 的 IconLocation 指向哪里

用法：
    python scripts/inspect_icon.py [exe路径] [lnk路径]
"""

import os
import struct
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO = os.path.join(ROOT, "Assets", "TimeGuard.ico")
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "dist", "TimeGuard.exe")
LNK = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    os.environ.get("USERPROFILE", ""), "Desktop", "TimeGuard.lnk")

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def visible_ratio(img):
    """alpha > 32 的像素占多少。空白图标会是 0。"""
    px = img.convert("RGBA")
    w, h = px.size
    data = px.load()
    n = 0
    for y in range(h):
        for x in range(w):
            if data[x, y][3] > 32:
                n += 1
    return n / float(w * h)


def dominant(img):
    """拿到可见像素里出现最多的颜色，用来判断「是不是我们那个深色圆盘」。"""
    px = img.convert("RGBA")
    w, h = px.size
    data = px.load()
    cnt = {}
    for y in range(h):
        for x in range(w):
            r, g, b, a = data[x, y]
            if a > 32:
                key = (r // 32 * 32, g // 32 * 32, b // 32 * 32)
                cnt[key] = cnt.get(key, 0) + 1
    if not cnt:
        return None
    return max(cnt.items(), key=lambda kv: kv[1])[0]


def check_ico(path):
    print("=" * 72)
    print("1) %s" % path)
    if not os.path.exists(path):
        print("   文件不存在！")
        return False
    size = os.path.getsize(path)
    raw = open(path, "rb").read()
    reserved, kind, count = struct.unpack_from("<HHH", raw, 0)
    print("   %d 字节，条目数 %d，type=%d（1 = 图标）" % (size, count, kind))

    ok = True
    for i in range(count):
        o = 6 + 16 * i
        bw, bh, ncol, res, planes, bpp, nbytes, offset = struct.unpack_from(
            "<BBBBHHII", raw, o)
        w = bw or 256
        h = bh or 256
        blob = raw[offset:offset + nbytes]
        fmt = "PNG" if blob[:8] == PNG_MAGIC else "DIB"
        note = ""
        ratio = None
        color = None
        try:
            if fmt == "PNG":
                import io
                img = Image.open(io.BytesIO(blob)).convert("RGBA")
            else:
                img = Image.open(path)
                img.size = (w, h)
                img = img.convert("RGBA")
            ratio = visible_ratio(img)
            color = dominant(img)
        except Exception as exc:              # noqa: BLE001  —— 诊断脚本要看到失败原因
            note = "  ← 解不开：%s" % exc
            ok = False
        line = "   #%d %3dx%-3d %s  %5d 字节  planes=%d bpp=%d" % (
            i, w, h, fmt, nbytes, planes, bpp)
        if ratio is not None:
            line += "  可见像素 %5.1f%%  主色 %s" % (ratio * 100, color)
            if ratio < 0.10:
                line += "  ← 几乎全透明，会被看成空白！"
                ok = False
        print(line + note)
    return ok


def rva_to_off(secs, rva):
    for vaddr, vsize, rawptr, rawsize in secs:
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None


def check_exe(path):
    print("=" * 72)
    print("2) %s" % path)
    if not os.path.exists(path):
        print("   文件不存在！")
        return
    d = open(path, "rb").read()
    e = struct.unpack_from("<I", d, 0x3C)[0]
    if d[e:e + 4] != b"PE\x00\x00":
        print("   不是 PE 文件")
        return
    coff = e + 4
    nsec = struct.unpack_from("<H", d, coff + 2)[0]
    optsize = struct.unpack_from("<H", d, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", d, opt)[0]
    roff = 112 if magic == 0x20B else 96
    # DataDirectory[2] 才是 Resource；别顺手写 opt+roff，那是 Export 目录（恒为 0）
    res_rva, res_size = struct.unpack_from("<II", d, opt + roff + 2 * 8)
    print("   资源目录 RVA=%#x 大小=%d" % (res_rva, res_size))
    if not res_size:
        print("   ← 没有资源段，说明 ApplicationIcon 根本没生效！")
        return

    secs = []
    base = opt + optsize
    for i in range(nsec):
        o = base + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", d, o + 8)
        secs.append((vaddr, vsize, rawptr, rawsize))

    resbase = rva_to_off(secs, res_rva)
    entries = {}

    def walk(dir_off, level, acc):
        named, ided = struct.unpack_from("<HH", d, dir_off + 12)
        for i in range(named + ided):
            eo = dir_off + 16 + i * 8
            nm, od = struct.unpack_from("<II", d, eo)
            if level < 2:
                if od & 0x80000000:
                    walk(resbase + (od & 0x7FFFFFFF), level + 1,
                         acc + ([nm & 0x7FFFFFFF] if level == 0 else [nm & 0x7FFFFFFF]))
                continue
            if not (od & 0x80000000):
                data_off = resbase + od
                rva, size, cp, rs = struct.unpack_from("<IIII", d, data_off)
                fo = rva_to_off(secs, rva)
                entries.setdefault(acc[0], []).append((acc[1], fo, size))

    walk(resbase, 0, [])

    groups = entries.get(14, [])
    icons = entries.get(3, [])
    print("   RT_GROUP_ICON 条目 %d 个，RT_ICON 条目 %d 个" % (len(groups), len(icons)))

    for gname, goff, gsize in sorted(groups):
        grp = d[goff:goff + gsize]
        cnt = struct.unpack_from("<H", grp, 4)[0]
        print("   图标组 id=%d 含 %d 个成员：" % (gname, cnt))
        for j in range(cnt):
            o = 6 + j * 14
            bw, bh, ncol, res, planes, bpp, nbytes, iid = struct.unpack_from(
                "<BBBBHHIH", grp, o)
            w = bw or 256
            h = bh or 256
            # 找到对应 RT_ICON 的实体，看它头 8 字节
            blob = b""
            for iname, ioff, isize in icons:
                if iname == iid:
                    blob = d[ioff:ioff + min(isize, 16)]
            fmt = "PNG" if blob[:8] == PNG_MAGIC else ("DIB" if blob else "?")
            print("      %3dx%-3d %s  %6d 字节  bpp=%d" % (w, h, fmt, nbytes, bpp))


def check_lnk(path):
    print("=" * 72)
    print("3) %s" % path)
    if not os.path.exists(path):
        print("   快捷方式不存在")
        return
    d = open(path, "rb").read()
    hs, = struct.unpack_from("<I", d, 0)
    flags, = struct.unpack_from("<I", d, 20)
    off = hs
    if flags & 1:
        idsize, = struct.unpack_from("<H", d, off)
        off += 2 + idsize
    if flags & 2:
        li_size, = struct.unpack_from("<I", d, off)
        off += li_size
    for bit, nm in [(4, "NAME"), (8, "RELATIVE_PATH"), (16, "WORKING_DIR"),
                    (32, "ARGS"), (64, "ICON_LOCATION")]:
        if not (flags & bit):
            continue
        cnt, = struct.unpack_from("<H", d, off)
        if flags & 0x80:
            s = d[off + 2:off + 2 + cnt * 2].decode("utf-16-le", "ignore")
            off += 2 + cnt * 2
        else:
            s = d[off + 2:off + 2 + cnt].decode("mbcs", "ignore")
            off += 2 + cnt
        print("   %-14s = %r" % (nm, s))
    print("   长度 %d 字节" % len(d))


if __name__ == "__main__":
    check_ico(ICO)
    check_exe(EXE)
    check_lnk(LNK)
    print("=" * 72)
