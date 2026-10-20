"""把含中文的 .cmd / .bat 转成 GBK(CP936)，并去掉 `chcp 65001`。

为什么必须做（这是 2026-09-22 真实事故的结论，别再踩）：
  * **cmd.exe 是按「当前控制台代码页」逐字节读批处理文件的**，而中文 Windows 的
    默认代码页是 936(GBK)。文件若是 UTF-8，cmd 会按 GBK 去解那些字节：
    一个汉字 3 字节 → 解成 1.5 个 GBK 汉字，行的字节边界被切歪，
    于是**行尾的那半截被当成独立命令去执行**，控制台刷出：
        '，不用再往手机里导' is not recognized as an internal or external command
        '��拟器加速驱动' is not recognized as an internal or external command
    看起来像"脚本被写坏了"，其实是编码问题。
  * 文件头写 `chcp 65001` **救不了**：一是它只影响之后的**输出**编码，
    二是 cmd 会按字节偏移回读文件（goto/标签/循环都要回读），
    读到半个汉字就再切歪一次 —— 所以 UTF-8 批处理在中文机上必然时好时坏。
  * 正确做法：**含中文的 .cmd 一律存 GBK 无 BOM + CRLF，不要写 chcp**。
    纯 ASCII 的 .cmd 无所谓（ASCII 在两种编码下字节相同）。

用法：python fix-cmd-encoding.py <文件或目录> [...]
退出码 0 = 全部可用；非 0 = 有文件转不过去（比如含 GBK 表示不了的字符）。
"""
import os
import sys

BOM = b"\xef\xbb\xbf"
CHCP_UTF8 = b"chcp 65001"


def fix(path):
    with open(path, "rb") as fh:
        raw = fh.read()

    orig = raw
    if raw.startswith(BOM):
        raw = raw[3:]

    text = raw.decode("utf-8")                     # 现有文件按 UTF-8 写的

    # 1) 去掉会把读取顺序搞乱的 chcp 65001（换成显式的 936，保证确定性）
    lines = []
    chcp_hit = False
    for ln in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        if ln.strip().lower().startswith("chcp") and "65001" in ln:
            chcp_hit = True
            lines.append("chcp 936 >nul")
            continue
        lines.append(ln)
    text = "\r\n".join(lines)

    # 2) 编码成 GBK；GBK 表示不了的字符要报出来，不能吞
    try:
        data = text.encode("gbk")
    except UnicodeEncodeError as e:
        bad = text[e.start:e.end]
        raise SystemExit("FAIL %s：有 GBK 表示不了的字符 %r（位置 %d）" % (path, bad, e.start))

    notes = []
    if orig.startswith(BOM):
        notes.append("去BOM")
    if chcp_hit:
        notes.append("chcp→936")
    if b"\n" in raw.replace(b"\r\n", b""):
        notes.append("行尾→CRLF")
    if not notes and data == orig:
        return "clean", ""

    with open(path, "wb") as fh:
        fh.write(data)
    return "fixed", "+".join(notes)


def main(argv):
    targets = []
    for a in argv[1:]:
        if os.path.isdir(a):
            for root, _dirs, files in os.walk(a):
                for name in files:
                    if name.lower().endswith((".cmd", ".bat")):
                        targets.append(os.path.join(root, name))
        else:
            targets.append(a)

    rc = 0
    for t in targets:
        if not os.path.exists(t):
            print("MISSING " + t)
            rc = 1
            continue
        status, note = fix(t)
        print("%-7s %s%s" % (status, t, ("   (" + note + ")") if note else ""))
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
