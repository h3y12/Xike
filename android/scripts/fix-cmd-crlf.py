"""把 .cmd / .bat 的行尾统一成 CRLF，并确保不带头部 BOM。

为什么必须做：
  * cmd.exe 对 LF-only 的批处理解析不可靠 —— goto/标签、for 循环、括号块都可能
    莫名其妙失败（报「找不到标签」或整行被吞），而文件看起来完全正常；
  * 带 BOM 时，第一行会变成 "\ufeff@echo off"，cmd 会报 "'@echo' 不是内部或外部命令"。

**只做行尾/BOM 是不够的**：含中文的批处理还必须是 **GBK(CP936)** 编码，
否则 cmd.exe 按 936 去解 UTF-8 字节会把行切歪、把行尾当命令执行。
转换请用 `fix-cmd-encoding.py`（它顺带做 CRLF 和去 BOM），
本脚本只用于「已经确认是 ASCII 或已是 GBK、只想修行尾」的场合。

用法：python fix-cmd-crlf.py <文件或目录> [...]
"""
import os
import sys

BOM = b"\xef\xbb\xbf"


def fix(path):
    with open(path, "rb") as fh:
        raw = fh.read()

    orig = raw
    if raw.startswith(BOM):
        raw = raw[3:]
    # 先把已有 CRLF 收成 LF，再统一展开成 CRLF（避免出现 \r\r\n）
    raw = raw.replace(b"\r\n", b"\n").replace(b"\r", b"\n").replace(b"\n", b"\r\n")

    if raw != orig:
        with open(path, "wb") as fh:
            fh.write(raw)
        return "fixed"
    return "clean"


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
        print("%-7s %s" % (fix(t), t))
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
