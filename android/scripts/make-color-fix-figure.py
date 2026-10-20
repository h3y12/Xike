"""把模拟器实拍的截图拼成一张"修复前 / 修复后"对照图。

用的都是真机（模拟器）截图，不做任何示意图：
  s2.png     修复前：alpha 已补回，但白底上仍是近白色字 —— 就是用户看到的样子
  after.png  修复后：App 白底预览改用深色字
  overlay.png 修复后：悬浮窗深色卡上的浅色字
"""
from PIL import Image, ImageDraw, ImageFont

SRC = r"E:\tmp\shots"
OUT = r"E:\codes\TimeGuardAndroid\_verify\倒计时数字-修复前后.png"

FONT = r"C:\Windows\Fonts\msyh.ttc"
f_title = ImageFont.truetype(FONT, 24)
f_note = ImageFont.truetype(FONT, 19)

PAD = 20
GAP = 14
LABEL_H = 34

panels = [
    ("修复前 —— 就是你截图里的样子：白底 + 近白色字，淡得快看不见",
     Image.open(rf"{SRC}\s2.png").crop((0, 470, 720, 600))),
    ("修复后 · App 主页预览（白底）：换成深色字，清清楚楚",
     Image.open(rf"{SRC}\after.png").crop((0, 470, 720, 600))),
    ("修复后 · 悬浮窗（深色卡片）：浅色字，也清楚",
     Image.open(rf"{SRC}\overlay.png").crop((300, 190, 640, 370))),
]

W = PAD * 2 + 720
H = PAD * 2 + sum(LABEL_H + p[1].height + GAP for p in panels)
canvas = Image.new("RGB", (W, H), (245, 246, 248))
d = ImageDraw.Draw(canvas)

y = PAD
for text, im in panels:
    d.text((PAD, y + 4), text, font=f_title, fill=(27, 32, 40))
    y += LABEL_H
    x = PAD + (720 - im.width) // 2
    canvas.paste(im.convert("RGB"), (x, y))
    d.rectangle([x - 1, y - 1, x + im.width, y + im.height], outline=(200, 205, 212))
    y += im.height + GAP

canvas.save(OUT)
print("已保存", OUT, canvas.size)
