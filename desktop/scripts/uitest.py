"""真实输入链路验证：用系统级鼠标点击 + 键盘敲击，验证倒计时输入框能不能用。

为什么要这么测：
  之前只测过「直接构造参数调用链路」（SettingsRoundTripCheck）和「最小化/恢复」，
  这两样都绕过了鼠标和键盘本身。结果交付了一个鼠标穿透开着的窗口——点不到、打不进字。
  只有真的把光标移过去点一下、真的敲键，才能测出这种问题。

用法：
  python uitest.py <uitest.log> <期望的输入框内容>
流程：
  1. 读日志拿到 DPI 缩放和输入框在窗口内的位置
  2. 窗口物理矩形 + 相对位置 → 算出输入框中心的物理坐标
  3. SetCursorPos + mouse_event 真实点击
  4. keybd_event 真实敲键
  5. 再读日志，判定「窗口激活 / 输入框有键盘焦点 / 内容等于期望值」
"""
import ctypes
import ctypes.wintypes as wt
import re
import sys
import time

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # 必须在任何坐标 API 之前
except Exception:
    pass

u = ctypes.windll.user32
u.SetProcessDPIAware()

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
KEYEVENTF_KEYUP = 0x0002

# 只用到无 Shift 的键：数字 0-9、小写 m/h
VK = {
    "0": 0x30, "1": 0x31, "2": 0x32, "3": 0x33, "4": 0x34,
    "5": 0x35, "6": 0x36, "7": 0x37, "8": 0x38, "9": 0x39,
    "m": 0x4D, "h": 0x48, "s": 0x53,
}


def find_window(title="TimeGuard"):
    """找可见的 TimeGuard 主窗口；跳过 GDI+ 那种 1x1 隐藏辅助窗口。"""
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        n = u.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 2)
            u.GetWindowTextW(hwnd, buf, n + 2)
            if buf.value.strip() == title and u.IsWindowVisible(hwnd):
                r = wt.RECT()
                u.GetWindowRect(hwnd, ctypes.byref(r))
                if r.right - r.left > 50:      # 排除辅助小窗
                    found.append((hwnd, r))
        return True

    u.EnumWindows(cb, 0)
    return found


def read_log(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def last_state(text):
    """取日志里最后一条状态行。"""
    m = None
    for m in re.finditer(
        r'active=(\d) kbdFocus=(\d) wsExTransparent=(\d) text="([^"]*)" inWindow=([\d.]+),([\d.]+),([\d.]+),([\d.]+)',
        text):
        pass
    if not m:
        return None
    return {
        "active": m.group(1) == "1",
        "kbdFocus": m.group(2) == "1",
        "transparent": m.group(3) == "1",
        "text": m.group(4),
        "inWindow": tuple(float(m.group(i)) for i in range(5, 9)),
    }


def main():
    log_path = sys.argv[1]
    expect = sys.argv[2] if len(sys.argv) > 2 else "45m"
    keys = sys.argv[3] if len(sys.argv) > 3 else "45m"

    out = []

    def say(s):
        out.append(s)
        print(s, flush=True)

    # ---- 1. 等窗口和第一条日志 ----
    win = None
    for _ in range(40):
        w = find_window()
        if w:
            win = w[0]
            break
        time.sleep(0.25)
    if win is None:
        say("FAIL  找不到可见的 TimeGuard 窗口")
        return 1
    hwnd, rect = win

    st = None
    for _ in range(40):
        st = last_state(read_log(log_path))
        if st:
            break
        time.sleep(0.25)
    if st is None:
        say("FAIL  日志里没有状态行（--uitest 没起来？）")
        return 1

    dpi = 1.0
    m = re.search(r"^dpi=([\d.]+)", read_log(log_path), re.M)
    if m:
        dpi = float(m.group(1))

    say("窗口       hwnd=%s rect=%d,%d %dx%d" % (hex(hwnd), rect.left, rect.top,
                                              rect.right - rect.left, rect.bottom - rect.top))
    say("DPI        %.3f" % dpi)
    say("点击前     active=%d kbdFocus=%d wsExTransparent=%d text=\"%s\""
        % (st["active"], st["kbdFocus"], st["transparent"], st["text"]))

    # ---- 2. 点击输入框 ----
    x = rect.left + int((st["inWindow"][0] + st["inWindow"][2] / 2) * dpi)
    y = rect.top + int((st["inWindow"][1] + st["inWindow"][3] / 2) * dpi)
    say("点击坐标   (%d, %d)" % (x, y))

    # Windows 只把前台权交给"最近产生过输入"的进程。先敲一下 ALT 再 SetForegroundWindow
    # 是绕过前台锁的常规做法；否则从后台起的窗口可能压根没激活，测量起点就不一致。
    u.keybd_event(0x12, 0, 0, 0)
    time.sleep(0.05)
    u.keybd_event(0x12, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.15)
    u.SetForegroundWindow(hwnd)
    time.sleep(0.5)
    u.SetCursorPos(x, y)
    time.sleep(0.2)
    u.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    u.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.7)

    after_click = last_state(read_log(log_path))
    say("点击后     active=%d kbdFocus=%d" % (after_click["active"], after_click["kbdFocus"]))

    # ---- 3. 真实敲键 ----
    for ch in keys:
        vk = VK.get(ch.lower())
        if vk is None:
            continue
        u.keybd_event(vk, 0, 0, 0)
        time.sleep(0.04)
        u.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
        time.sleep(0.09)
    time.sleep(0.8)

    after_type = last_state(read_log(log_path))
    say("敲键后     active=%d kbdFocus=%d text=\"%s\""
        % (after_type["active"], after_type["kbdFocus"], after_type["text"]))

    # ---- 4. 判定 ----
    ok_click = after_click["active"] and after_click["kbdFocus"]
    ok_type = after_type["text"] == expect

    say("--")
    say("PASS  点击后窗口激活且输入框拿到键盘焦点" if ok_click
        else "FAIL  点击后没拿到焦点（active=%d kbdFocus=%d）—— 鼠标点不进去"
             % (after_click["active"], after_click["kbdFocus"]))
    say("PASS  真实敲键能把字打进输入框（\"%s\"）" % expect if ok_type
        else "FAIL  敲键后输入框里是 \"%s\"，期望 \"%s\"" % (after_type["text"], expect))

    with open(log_path, "a", encoding="utf-8") as f:
        f.write("\n" + "\n".join(out) + "\n")

    return 0 if (ok_click and ok_type) else 1


if __name__ == "__main__":
    sys.exit(main())
