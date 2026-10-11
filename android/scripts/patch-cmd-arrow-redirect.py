# -*- coding: utf-8 -*-
"""把 .cmd 里 echo 行中会被当成重定向的 `>` 修掉（一次性映射，不做链式替换）。

背景
----
cmd 在执行前会先解析重定向符。`echo A -> B -> C` 里的 `>` 不是"箭头装饰"，
而是重定向：cmd 把这两个 `>` 都当重定向，最后一个是生效的那个 ——
于是那一整句**不会打到屏幕上**，而是被写进一个以 `C` 开头的文件里。
实测（E:\\tmp\\redirtest）：
    rem ... -> ...    → 安全（cmd 对 rem 整行吞掉，不解析重定向）
    echo ... -> ...   → 屏幕无输出 + 目录里多出一个 22 字节的垃圾文件
    echo ... -> ...   → 全角箭头，正常
    echo ... -^> ...  → `^` 转义，正常

修法：把 echo 行里的半角箭头 `->` 换成全角 `→`（GBK 里有此字符，0xA1FA）。

用法：python patch-cmd-arrow-redirect.py
本脚本对每个目标文件用「长串整串定位 + 出现次数断言」，命中不了就报错退出，
不会写坏文件（字符串替换一律一次性 str.replace，绝不在循环里反复替换）。
"""
import sys

TARGETS = [
    (
        r"E:\codes\TimeGuardAndroid\启动模拟器-在电脑上运行.cmd",
        # 旧（半角箭头，会被当重定向）-> 新（全角箭头）
        "echo   自动编译 -> 覆盖安装 -> 重启 App。",
        "echo   自动编译 → 覆盖安装 → 重启 App。",
    ),
]


def main() -> int:
    bad = 0
    for path, old, new in TARGETS:
        raw = open(path, "rb").read()
        try:
            text = raw.decode("gbk")
            enc = "gbk"
        except UnicodeDecodeError:
            print("跳过（不是 GBK，先确认编码）:", path)
            bad += 1
            continue

        n_old = text.count(old)
        n_new = text.count(new)
        if n_old == 0 and n_new == 1:
            print("[已是目标状态] %s" % path)
            continue
        if n_old != 1:
            print("★ 断言失败：旧串出现 %d 次（期望 1 次），未做任何修改：\n    %s\n    文件：%s"
                  % (n_old, old, path))
            bad += 1
            continue

        text = text.replace(old, new, 1)          # 一次性替换，不循环
        assert text.count(old) == 0, "替换后旧串仍在"
        assert text.count(new) == 1, "替换后新串不是 1 个"

        # 回写：保持 GBK + CRLF + 无 BOM
        data = text.replace("\r\n", "\n").replace("\n", "\r\n").encode("gbk")
        assert data[:3] != b"\xef\xbb\xbf", "不允许写回 BOM"
        open(path, "wb").write(data)
        print("[已修] %s（编码 %s，CRLF %d 行）" % (path, enc, data.count(b"\r\n")))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
