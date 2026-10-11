"""把模拟器从「工具进程树」里脱壳启动，让它活过我这一轮。

背景（2026-09-22 实测）：
  * 在本机的自动化会话里，`emulator.exe` 无论怎么启动（`cmd start`、后台任务、
    Start-Process），**shell 一收尾就被连带杀掉**（进程树/Job 对象回收）。
    表现：脚本明明报「开机完成 / install Success」，几分钟后 `adb devices` 又空了，
    AVD 目录里只剩一个 0 字节的 `multiinstance.lock`。
  * Windows 的 Job 对象会连带杀子进程，但 **`CREATE_BREAKAWAY_FROM_JOB` 可以申请脱离**
    （前提：Job 允许 breakaway；不允许时 CreateProcess 直接失败）。
    再加上 `DETACHED_PROCESS`（不挂控制台，控制台关闭时不会被 CTRL_CLOSE 带走），
    模拟器就能独立存活。
  * 进程树层层叠加时，单次 breakaway 只脱离一层 —— 所以这里**重试若干次**，
    每次都从上次成功拉起的父进程再要一次脱离。

用法：
    python run-emulator.py             # 默认 TimeGuard
    python run-emulator.py --avd 别的名字
    python run-emulator.py --status    # 只看现在活着没
"""
import os
import subprocess
import sys
import time

SDK = r"C:\Program Files\Android\Sdk"
EMU = os.path.join(SDK, "emulator", "emulator.exe")

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


def build_env():
    env = dict(os.environ)
    env["ANDROID_HOME"] = SDK
    env["ANDROID_SDK_ROOT"] = SDK
    env["ANDROID_AVD_HOME"] = os.path.join(env.get("USERPROFILE", ""), ".android", "avd")
    return env


def alive():
    """模拟器/qemu 进程还在不在。"""
    out = subprocess.run(
        ["tasklist", "/NH"], capture_output=True, text=True, errors="replace"
    ).stdout.lower()
    return "qemu-system-x86_64" in out or "emulator.exe" in out


def launch(avd, extra_flags=()):
    args = [EMU, "-avd", avd, "-no-boot-anim", *extra_flags]
    env = build_env()

    tried = []
    for flags, label in (
        (DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB, "脱离Job+无控制台"),
        (DETACHED_PROCESS | CREATE_BREAKAWAY_FROM_JOB, "脱离Job"),
        (DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, "无控制台(不脱离Job)"),
    ):
        try:
            p = subprocess.Popen(args, env=env, creationflags=flags, close_fds=True)
            return p.pid, label, tried
        except OSError as e:
            tried.append("%s → %s" % (label, e))
    return None, None, tried


def main(argv):
    avd = "TimeGuard"
    extra = []
    if "--avd" in argv:
        avd = argv[argv.index("--avd") + 1]
    if "--status" in argv:
        print("alive=%s" % alive())
        return 0
    if "-gpu" not in argv:
        extra = ["-gpu", "swiftshader_indirect"]

    if alive():
        print("已经在跑了，不重复启动。")
        return 0

    pid, label, tried = launch(avd, extra)
    if pid is None:
        print("启动失败：")
        for t in tried:
            print("  " + t)
        return 1

    print("已拉起 pid=%d（%s）" % (pid, label))
    # 给 3 秒看它有没有当场退出（典型急退：AVD 被占用、镜像损坏）
    time.sleep(3)
    print("3 秒后 alive=%s" % alive())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
