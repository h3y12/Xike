# 息刻（TimeGuard）

> 一个常驻桌面的 Windows 小挂件：盯你早睡的「健康分」+ 一个「到点 / 倒计时」计时器。无边框、半透明、置顶、可鼠标穿透（默认关），带托盘图标，两个面板可分别收起。

给三类人看：

- **想接手/参与项目的人**：先看 `docs/ARCHITECTURE.md` 的代码地图和 `docs/DEVELOPING.md` 的「改 X 该动哪里」对照表。
- **想读懂代码的人**：从 `docs/ARCHITECTURE.md` 的「数据流」一节顺着读，纯逻辑（`ScoreEngine` / `Countdown` / `SleepRules` / `ScoreVisual`）和界面（`MainWindow`）是解耦的，可单独读懂。
- **想自己改一处功能的人（比如作者本人）**：直接翻 `docs/DEVELOPING.md` 的对照表，`docs/PITFALLS.md` 里记了所有踩过的坑，改之前先扫一遍，别把修过的 bug 又引回来。

---

## 目录结构

```text
TimeGuard/
├── src/                 C# 源码、WPF 界面和对应后台代码
├── docs/                架构、开发指南、功能与修复说明
│   └── images/          说明文档中的配图
├── Assets/              程序图标等资源
├── scripts/             Python 辅助与验证脚本
├── dist/                正式发布的程序
├── _verify/             本地验证报告与临时发布产物
├── bin/、obj/           自动生成的构建文件
├── TimeGuard.csproj     项目配置
├── app.manifest         Windows 应用清单
└── publish.cmd          发布入口
```

文档中提到的桌面版 `.cs` / `.xaml` 文件均位于 `src/`。构建、发布命令仍在项目根目录执行。

说明文档：[架构](docs/ARCHITECTURE.md) · [开发指南](docs/DEVELOPING.md) · [踩坑记录](docs/PITFALLS.md) · [闹钟功能](docs/闹钟功能说明.md) · [高 DPI 修复](docs/高DPI缩放修复说明.md) · [审查整改](docs/代码审查整改说明.md)。

---

## 怎么跑起来

从 GitHub Releases 下载 Windows 版 EXE 后直接运行。自行构建发布时，产物路径为：

```
dist\TimeGuard.exe
```

首次运行会在托盘区出现图标，20:30 之后窗口自己浮现；白天（06:30–20:30）默认隐藏，只留托盘图标。

> 产物 `dist/TimeGuard.exe` 是 **framework-dependent 单文件发布**（含 `hostfxr` 与内嵌 `runtimeconfig`，约 459 KB）。目标机需装 **.NET 8 桌面运行时**（Win10 19043 实测）。若双击没反应，先确认 .NET 8 Runtime 已装。

---

## 界面说明

窗口竖排两块，中间一条细分割线：

| 区域 | 位置 | 显示什么 | 能否单独收起 |
|------|------|----------|--------------|
| **早睡健康分**块 | 上面 | 大数字分数（如 `100`）、当前钟点、状态文案 | 能（托盘/右键菜单「收起『早睡健康分』块」，或 `ConfigScoreHidden`） |
| 细线 | 中间 | 1px 分隔 | 跟两块一起显隐 |
| **倒计时**块 | 下面 | `HH:MM:SS` 大字、说明小字、输入框 + 开始/重置按钮 | 能（「收起『倒计时』块」）；只在运行或暂停且有剩余时间时显示；从托盘/热键主动设置时临时展开输入区 |

两块都收起时，窗口整体隐藏，但后台计时照常跑（倒计时不受影响）。

**鼠标交互**：拖动窗口空白处可移动；鼠标悬停窗口右上角露出 `–`（最小化）和 `✕`（退出）两个按钮 —— 这两个按钮是**窗口级浮层**，不管收起「早睡健康分」还是「倒计时」块都还在，不会跟着某个块消失；右键弹菜单；托盘图标**单击 = 唤出窗口，双击 = 收起**（窗口已经是最小化状态时，单击把它放回来；想收起就双击）；托盘右键菜单功能最全。

---

## 快捷键表

全局热键在 `MainWindow.xaml.cs` 的 `OnSourceInitialized` 里用 `RegisterHotKey` 注册。按下标的是 **Ctrl+Alt**，第三键如下（注册失败会自动回退，见备注）：

| 快捷键 | 作用 | 备注 |
|--------|------|------|
| **Ctrl+Alt+L** | 切换「鼠标穿透」开/关 | 始终注册，无回退。`WM_MOUSEACTIVATE` 兜底保证键盘焦点能进窗口 |
| **Ctrl+Alt+M** | 最小化到托盘 / 从托盘恢复 | 被别的程序占用时回退到 **Ctrl+Alt+H**，托盘菜单里显示实际生效键 |
| **Ctrl+Alt+T** | 开始 / 暂停 / 继续 / 再来 倒计时 | 被占用时回退到 **Ctrl+Alt+K** |
| 输入框内 **Enter** | 起倒计时（空闲）/ 暂停（时长模式在跑）/ 按框内钟点重设（到点模式在跑） | 见 `CdInput_KeyDown` |
| 输入框内 **Esc** | 清除键盘焦点 | |
| 设置窗口 **Enter / Esc** | 保存 / 取消 | 见 `SettingsWindow.Win_PreviewKeyDown` |
| 鼠标穿透开着时 **按住 Ctrl** | 临时恢复可交互（拖动、点按钮） | `ApplyClickThrough` + `BtnRow` 逻辑 |

> 哪些键被占用、实际生效哪套，跑 `--uicheck` 或看托盘菜单底部那几行灰色提示（它们直接显示 `_minHotkeyDesc` / `_cdHotkeyDesc`）。

---

## 配置文件

路径（默认，随用户漫游）：

```
%APPDATA%\TimeGuard\config.json
```

旧版本叫 `SleepGuard`，首次运行会自动从 `%APPDATA%\SleepGuard\config.json` 搬过来（`AppConfig.Load`）。**所有会建窗口或落盘的自检模式不写这个文件**——它们改用旁边的 `selftest-config.json`（见下文「自检命令」）。

逐字段含义与默认值（`AppConfig.cs`，落盘后 `Sanitize` 会再把越界值拉回默认）：

| 字段 | 类型 | 默认值 | 含义 |
|------|------|--------|------|
| `ConfigVersion` | int | `2` | 配置格式版本。当前 `2`；`<2` 的旧配置会被强制把 `ClickThrough` 掰回 `false`（见 `PITFALLS.md`「输入框打不进字」）。**不要手改这个字段** |
| `Left` / `Top` | double? | `null` | 窗口左上角坐标（设备无关单位）。`null` 表示未记录，首次运行落在右上角（工作区右 - 28、上 + 28） |
| `Opacity` | double | `0.88` | 窗口不透明度。越界（<0.2 或 >1）会被拉回 `0.88`；托盘菜单「透明度」可切 50%–100% |
| `ClickThrough` | bool | `false` | 鼠标穿透。**默认必须是 false**，否则输入框收不到键盘。想用得从托盘/右键菜单自己开 |
| `ScoreHidden` | bool | `false` | 手动收起「早睡健康分」块 |
| `CountdownHidden` | bool | `false` | 手动收起「倒计时」块 |
| `CountdownPinned` | bool | `false` | 旧版兼容字段，已不参与显示判断；没有剩余时间的倒计时自动隐藏 |
| `ShowAtText` | string | `"20:30"` | 窗口出现、开始记分的时刻（HH:mm） |
| `DeductAtText` | string | `"23:30"` | 开始扣分的时刻 |
| `HideAtText` | string | `"06:30"` | 窗口隐藏的时刻 |
| `MinutesPerPoint` | int | `3` | 每多少分钟扣一次分 |
| `PointsPerStep` | int | `1` | 每次扣几分 |
| `StatusBeforeText` | string | `"夜深了，注意休息~"` | 已出现、还没开始扣分时，分数下面显示的那行字 |
| `StatusHighText` | string | `"快去睡吧。"` | 高分档（≥80）的状态文字 |
| `StatusMidText` | string | `"头发要紧，睡觉！"` | 中分档（60–79）的状态文字 |
| `StatusLowText` | string | `"不睡等死吧你！！！"` | 低分档（<60）的状态文字 |
| `CdInput` | string | `""` | 倒计时输入框里上次填的文字（重启后回显） |
| `CdMode` | string | `""` | 倒计时模式：`ClockTime`（到钟点）/ `Duration`（N 分钟后），空 = 没设 |
| `CdClockTicks` | long | `0` | 钟点模式的「目标时刻」时间戳（ticks） |
| `CdDurationTicks` | long | `0` | 时长模式的时长（ticks） |
| `CdDeadline` | string | `""` | 绝对截止时刻（ISO8601，如 `2026-09-21T23:30:00`）；程序没开的时段也照常计入 |
| `CdPhase` | string | `""` | 倒计时阶段：`Idle`/`Running`/`Paused`/`Finished` |
| `CdPausedTicks` | long | `0` | 暂停时冻结的剩余量（ticks） |

> 规则与状态文案（`ShowAtText` 到 `StatusLowText` 共九个）都由设置窗口改；倒计时那一组是落盘字段，关掉程序再开倒计时接着走。改坏了 `Sanitize` 会兜底还原，`CdMode`/`CdPhase` 写错直接清空倒计时。
>
> **状态文案留空是合法的**：表示那个状态下不显示文字（那一行连同它的间距一起收掉）。旧配置里没有这四个字段时会用默认文案，不会变空。
>
> 分档界限定在 `ScoreVisual.HighLine`（当前 80）与 `ScoreVisual.MidLine`（当前 60），设置窗口的档位标签按它自动生成。

---

## 自检命令

`App.xaml.cs` 按第一个命令行参数分发模式，输出文件都是「第二个参数」，省略则落在 exe 同目录：

| 命令 | 作用 | 输出/副作用 |
|------|------|-------------|
| `TimeGuard.exe --selftest [文件]` | 把整夜各时刻的分数导出成表，逐分钟模拟与引擎对拍 + 边界断言 + 文案配色 + 设置校验 + 倒计时校验 | 文本报告（如 `selftest.txt`）。**只读 config.json，不建窗口、不落盘** |
| `TimeGuard.exe --snapshot [png]` | 离屏渲染整夜各阶段界面 + 设置窗口，拼一张对照图；**并实测设置窗口每一行有没有溢出**（哪个控件右边界超出可视区） | `snapshot.png` + 同名的 `snapshot.layout.txt`（逐行 `PASS/FAIL` 与余量像素）。**会 `new MainWindow()`，因此强制改用隔离配置 `selftest-config.json`**，不碰真实配置 |
| `TimeGuard.exe --icon [png]` | 把托盘实际用的图标导出来，确认没退回系统默认 | `trayicon.png` + `trayicon.txt`（含来源判定 PASS/FAIL） |
| `TimeGuard.exe --uicheck [文件]` | 真开窗口，验证「最小化后不会被自己弹回来」、热键注册、设置链路、倒计时链路、两块分别收起 | `uicheck.txt`。**用隔离配置**，跑完还原面板开关 |
| `TimeGuard.exe --uitest [文件] [clickthrough]` | 开真窗口，由外部脚本（`scripts/uitest.py`）注入真实鼠标点击 + 键盘敲字，验证「点得进、打得进」 | `uitest.log`。加第三参 `clickthrough` 会强制开着穿透，用来证明这个自检真能甄别出「点不进去」。**用隔离配置** |

> 这些模式**绝不会读写你的 `config.json`**：它们统一调 `AppConfig.UseOverrideFile(...)`，配置落在输出文件旁的 `selftest-config.json`。历史教训见 `docs/PITFALLS.md`「快照洗掉真实配置」。

常规双击启动（无参数）走单实例互斥：第二个实例只发信号把已有窗口叫出来，不会多开。

---

## 构建与发布命令

**目标框架锁死 `net8.0-windows`**——不要升到 net9：.NET 9 的 apphost 在这台 Win10（19043）上会崩，报 `CLR: Assert failure: !AreShadowStacksEnabled()`。

本机装的是 .NET SDK **9.0.100**，但它能交叉构建 net8 目标（需 net8 桌面运行时/引用包已装）。项目用 `win-x64` RID（见 `obj/Release/net8.0-windows/win-x64`）。

> 下面这条发布命令是**本机实际跑通、产出 `dist/TimeGuard.exe`（约 459 KB）的那一条**。

```bash
# 还原依赖
dotnet restore

# Release 构建
dotnet build -c Release

# 单文件发布到 dist/（framework-dependent 单文件；目标机需装 .NET 8 桌面运行时）
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=none -o dist
```

> ⚠️ 发布前先确认 `dist\TimeGuard.exe` **没有正在运行**，否则报 `MSB4018 ... being used by another process`——先关掉它再发布，别反复重试。

**不想记命令就双击根目录的 `publish.cmd`**：它先查有没有在跑的 `TimeGuard.exe`（有就打印一句提示直接退出，不动任何文件），没有就执行上面这条 publish，最后把 `dist\TimeGuard.exe` 的时间戳打出来。

> 🔴 **桌面快捷方式指向的是 `dist\TimeGuard.exe`，不是 `bin\Release`。**
> `bin\Release` 里编译通过、`--selftest` 全绿，都**不代表**用户点图标能看到新版——只要漏了 publish，点进去还是旧程序。改完代码务必跑一次发布；发布后要验证，跑的是 `dist\` 里那份 exe（见 `docs/DEVELOPING.md`）。
> 另一个容易误判的点：程序有单实例锁，**旧实例还在跑的时候点桌面图标，新进程会立刻退出并把旧窗口唤到前面**，屏幕上看到的仍然是旧版界面。要先右键窗口/托盘图标 → 退出，再点图标。

- 图标与清单由 csproj 指定：`<ApplicationIcon>Assets\TimeGuard.ico</ApplicationIcon>`、`<ApplicationManifest>app.manifest</ApplicationManifest>`，且 `Assets\TimeGuard.ico` 同时作为 `<Resource>` 嵌进程序集（托盘图标从内嵌资源读，保证和 exe 图标一致）。
- 重新生成图标：`python scripts/make_icon.py`（见 `docs/DEVELOPING.md`）。
- 重建桌面快捷方式：`python scripts/make_shortcut.py dist\TimeGuard.exe`（见 `docs/DEVELOPING.md`）。

---

## 源码文件一览（详见 `docs/ARCHITECTURE.md`）

`App.xaml.cs`（启动/模式分发/单实例）· `AppConfig.cs`（配置+迁移）· `SleepRules.cs`（规则）· `ScoreEngine.cs`（计分纯函数）· `ScoreVisual.cs`（分数→文案/颜色）· `Countdown.cs`（倒计时状态机+解析）· `MainWindow.xaml(.cs)`（主界面）· `SettingsWindow.xaml(.cs)`（设置）· `SelfTest.cs` / `Snapshot.cs`（自检）· `app.manifest` · `TimeGuard.csproj` · `scripts/*.py`（图标/快捷方式/验证脚本）。

闹钟修复（2.0.1）：非模态设置窗口保存不再设置 DialogResult，避免保存后崩溃；一次性闹钟在触发时立即删除并保存，选择保留时关闭；有闹钟正在响时，新到期的闹钟仍会进入队列，前一个自动停止后继续响下一个。隐藏挂件不影响程序内的闹钟；退出程序后不再响铃。
