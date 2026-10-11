"""Generate the shared Xike mark as a multi-resolution Windows ICO."""
import io
import os
import struct
from PIL import Image, ImageDraw
from brand_icon import render, export
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO_PATH = os.path.join(ROOT, 'Assets', 'TimeGuard.ico')
PREVIEW_PATH = os.path.join(ROOT, '_verify', 'icon-preview.png')
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

def dib_bytes(img):
    """32 位 BGRA 的 DIB（BITMAPINFOHEADER + 自下而上的像素 + 1 位 AND 掩码）。"""
    w, h = img.size
    px = img.load()

    header = struct.pack("<IiiHHIIiiII",
                         40,        # biSize
                         w,         # biWidth
                         h * 2,     # biHeight：XOR 图 + AND 掩码，写两倍
                         1,         # biPlanes
                         32,        # biBitCount
                         0,         # biCompression = BI_RGB
                         w * h * 4, # biSizeImage
                         0, 0, 0, 0)

    rows = []
    for y in range(h - 1, -1, -1):          # DIB 是自下而上
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))      # BGRA
        rows.append(bytes(row))
    xor = b"".join(rows)

    # AND 掩码：1 位，1 = 透明；每行按 4 字节对齐
    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        line = bytearray(stride)
        for x in range(w):
            if px[x, y][3] < 128:
                line[x // 8] |= 0x80 >> (x % 8)
        mask += line

    return header + xor + bytes(mask)


def png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, frames):
    """frames: [(size, img)]，小的走 BMP、大的走 PNG。"""
    blobs = []
    for size, img in frames:
        data = png_bytes(img) if size >= 128 else dib_bytes(img)
        blobs.append((size, data))

    offset = 6 + 16 * len(blobs)
    out = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))
    for size, data in blobs:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in blobs:
        out += data

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(bytes(out))


def make_preview(frames, path):
    """把各尺寸按同一放大倍数排成一条，方便肉眼核对。"""
    zoom = 2
    pad = 8
    big = max(s for s, _ in frames) * zoom
    cell = big + pad * 2
    sheet = Image.new("RGBA", (cell * len(frames), cell + 22), (0xF2, 0xF3, 0xF5, 255))
    d = ImageDraw.Draw(sheet)

    for i, (size, img) in enumerate(frames):
        # 每个尺寸都放大到同一个方块里：小的用 NEAREST 看像素，大的原样
        shown = img.resize((big, big), Image.NEAREST)
        x = i * cell + pad
        sheet.alpha_composite(shown, (x, pad))
        d.text((x, cell + 4), "%dpx" % size, fill=(0x33, 0x33, 0x33, 255))

    os.makedirs(os.path.dirname(path), exist_ok=True)
    sheet.save(path)


def main():
    export()
    frames = [(s, render(s)) for s in SIZES]
    write_ico(ICO_PATH, frames)
    make_preview(frames, PREVIEW_PATH)

    print("已生成 %s（%d 字节，%d 个尺寸）" % (ICO_PATH, os.path.getsize(ICO_PATH), len(frames)))
    print("预览 %s" % PREVIEW_PATH)
    for s, _ in frames:
        print("   %3dpx  %s" % (s, "PNG" if s >= 128 else "BMP"))


if __name__ == "__main__":
    main()
