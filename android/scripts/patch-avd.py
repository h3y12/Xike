import io, os, re

p = r"C:\Users\Administrator\.android\avd\TimeGuard.avd\config.ini"
src = io.open(p, encoding="utf-8").read()

# 这台机器是 i3-12100 无独显、且当前是 RDP 会话：屏幕调小能明显减轻渲染压力，
# 开硬件键盘是为了能在模拟器里直接用电脑键盘敲倒计时输入框。
want = {
    "hw.lcd.width": "720",
    "hw.lcd.height": "1600",
    "hw.lcd.density": "320",
    "hw.keyboard": "yes",
    "hw.gpu.enabled": "yes",
    "hw.gpu.mode": "auto",
    "hw.ramSize": "2048",
    "vm.heapSize": "256",
    "disk.dataPartition.size": "2048M",
    "showDeviceFrame": "no",
    "hw.camera.back": "none",
    "hw.audioInput": "no",
}

out = []
seen = set()
for line in src.splitlines():
    if "=" in line:
        k = line.split("=", 1)[0].strip()
        if k in want:
            out.append(k + "=" + want[k])
            seen.add(k)
            continue
    out.append(line)

for k, v in want.items():
    if k not in seen:
        out.append(k + "=" + v)

io.open(p, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
print("patched:", sorted(seen))
print("added  :", sorted(set(want) - seen))
