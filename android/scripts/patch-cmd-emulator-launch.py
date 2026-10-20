"""一次性补丁：让「启动模拟器-在电脑上运行.cmd」通过 scripts\\start-emulator.cmd 起模拟器。

为什么用 Python 打补丁而不是编辑器：
  * 这个 .cmd 现在是 **GBK** 编码（含中文批处理必须如此，见 fix-cmd-encoding.py）。
    普通文本编辑器会按 UTF-8 读进来 —— 看到的是乱码，写回去就把中文毁了。
  * **必须按行定位**：文件行尾是 CRLF，直接拿带 \\n 的多行串去 replace 会 0 命中
    （第一次就是这么被断言拦下的 —— 断言拦下来是对的，不要绕过它）。
  * 替换只做一次，绝不在循环里反复替换（2026-09-18「通」字连环插入事故）。
"""
import re

P = r"E:\codes\TimeGuardAndroid\启动模拟器-在电脑上运行.cmd"

ANCHOR = "  rem -gpu swiftshader_indirect：本机是 RDP 远程会话，拿不到宿主 OpenGL，"
EXPECT = [
    ANCHOR,
    "  rem 交给软件渲染最稳（慢一点，但不会卡在黑屏）。",
    '  start "" "%EMU%" -avd %AVD% -no-boot-anim -gpu swiftshader_indirect',
]
NEW = [
    "  rem 交给 scripts\\start-emulator.cmd 起：模拟器在**它自己的控制台窗口**里跑，",
    "  rem 这样本脚本这个窗口关掉时，模拟器不会被一起带走",
    "  rem （emulator.exe 是 GUI 程序，用 start 起会继承调用者的控制台，",
    "  rem   控制台一关它就收到 CTRL_CLOSE 跟着退）。",
    "  rem gpu / AVD 参数都在那边，本脚本只管「是不是已经在跑」。",
    '  start "TimeGuard 模拟器" /min "%~dp0scripts\\start-emulator.cmd" %AVD%',
]

text = open(P, "rb").read().decode("gbk")
lines = text.split("\r\n")

hits = [i for i, l in enumerate(lines) if l == ANCHOR]
assert len(hits) == 1, "锚点命中 %d 次，停下来人工看" % len(hits)
i = hits[0]
assert lines[i:i + 3] == EXPECT, "上下文不是预期的三行，停下来人工看：\n%r" % (lines[i:i + 3],)

lines[i:i + 3] = NEW
out = "\r\n".join(lines)

# 自查：连续相同汉字 ≥3 次 = 重复插入事故的特征（2026-09-18 教训）
dup = re.findall(r"([\u4e00-\u9fa5])\1{2,}", out)
print("连续相同汉字检查：", "干净" if not dup else "★发现 %r" % set(dup))

open(P, "wb").write(out.encode("gbk"))
print("已写入。改动后的那一段：")
for n, line in enumerate(out.split("\r\n"), 1):
    if 80 <= n <= 92:
        print("%3d|%s|" % (n, line))
