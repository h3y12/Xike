# TimeGuard 开发指南

文中提到的桌面版 `.cs` / `.xaml` 文件均位于项目根目录下的 `src/`；构建与发布命令仍在项目根目录执行。

给想改代码的人（尤其是作者本人）。目的：你改 X 功能时，能直接知道动哪个文件、哪一行，以及有哪些前置约束。改完对照 `PITFALLS.md` 自查别把旧 bug 引回来。

---

## 1. 开发环境准备

- **.NET SDK**：本机装的是 **9.0.100**。它能构建 `net8.0-windows` 目标（需要 net8 桌面引用包/运行时已装）。装 `dotnet --version` 确认。
- **目标框架：必须 `net8.0-windows`** —— 见 `csproj` 的 `<TargetFramework>net8.0-windows</TargetFramework>`。**不要改成 net9**：.NET 9 的 apphost 在本机 Win10（19043）会崩，报 `CLR: Assert failure: !AreShadowStacksEnabled()`。任何「升级到 net9 试试」的念头先否掉。
- **工作负载**：需要 WPF + Windows Forms 的 net8 桌面支持（`UseWPF`、`UseWindowsForms` 都已开）。`WinForms` 仅用于托盘图标（`NotifyIcon`/`ContextMenuStrip`），WPF 负责主界面与 DPI。
- **Python（仅脚本/验证用）**：`scripts/*.py` 需要 `Pillow`（`pip install Pillow`），且只能在本机 Windows 上跑（依赖 `ctypes.windll`）。
- **IDE**：Rider / VS 均可；本机无 VS 也能用 `dotnet build` 命令行。

---

## 2. 常用命令

```bash
# 还原依赖
dotnet restore

# Debug 构建
dotnet build -c Debug

# Release 构建（win-x64 RID，仓库 obj 里就是 net8.0-windows/win-x64）
dotnet build -c Release -r win-x64

# 跑起来（Debug 产物）
dotnet run --project . -c Debug
# 或直接双击 dist/TimeGuard.exe

# 单文件发布到 dist/（framework-dependent 单文件；以下为本机实测通过的参数）
# 发布前先确认 dist\TimeGuard.exe 没在运行，否则报 MSB4018 being used by another process
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=none -o dist
```

嫌麻烦就双击根目录 `publish.cmd`：它自己查占用（在跑就提示并退出，不动文件）→ 执行上面这条 publish → 打印 `dist\TimeGuard.exe` 的时间戳。

> 🔴 **产物目录认准 `dist\`**：桌面快捷方式的目标是 `E:\codes\TimeGuard\dist\TimeGuard.exe`。
> `dotnet build`（产物在 `bin\Release`）和 publish 是两件事——只 build 不 publish，点桌面图标跑的还是旧程序。
> 同理，**任何一次「改完验收」都要对 `dist\` 里那份 exe 跑自检**，不能只测 `bin\Release`：
> `dist\TimeGuard.exe --selftest _verify/dist-selftest.txt`、`dist\TimeGuard.exe --snapshot _verify/dist-snapshot.png`。
> 另：程序有单实例锁，旧实例在跑时点图标只会把旧窗口唤到前面（新进程瞬间退出），看起来就像"发布没生效"。先退出旧实例。
> MSBuild 对内容没变的文件会跳过复制，所以「发布成功但时间戳没变」是正常的，看 publish 有没有报错即可。

自检（详见 README「自检命令」，全部隔离配置、不碰真实 `config.json`）：

```bash
dotnet run --project . -c Release -- --selftest  _verify/selftest.txt
dotnet run --project . -c Release -- --snapshot  _verify/snapshot.png
dotnet run --project . -c Release -- --icon      _verify/trayicon.png
dotnet run --project . -c Release -- --uicheck   _verify/uicheck.txt
dotnet run --project . -c Release -- --uitest    _verify/uitest.log
# 配合外部脚本验证输入链路（真实鼠标+键盘）：
python scripts/uitest.py _verify/uitest.log 45m
```

图标与快捷方式：

```bash
python scripts/make_icon.py                 # 重新生成 Assets/TimeGuard.ico
python scripts/make_shortcut.py dist\TimeGuard.exe   # 重建桌面 TimeGuard.lnk
```

> 发布后务必跑一次 `--selftest` 和 `--icon` 确认规则与图标正常；改了界面再跑 `--snapshot` 看对照图。

---

## 3. 「我想改 X，该动哪里」对照表

下列「改哪」均指源码文件 + 关键位置（行号是撰写时的快照，结构不变时仍准确，重构后请按名字搜）。

| 想改的东西 | 该动的文件 / 位置 | 备注 |
|-----------|------------------|------|
| **字号** | `MainWindow.xaml` 里各 `TextBlock` 的 `FontSize`（分数 `ScoreText` 68、钟点 `ClockText` 13、状态 `StatusText` 13、倒计时 `CdTime` 33、说明 `CdLabel` 11；输入框 `CdInput` 13） | **改完必须重算横向总宽**（见第 4 节），否则控件溢出被裁切 |
| **窗口尺寸 / 宽** | 主窗口：`MainWindow.xaml` 的 `Width="183"`（高由 `UpdateWindowHeight` 手算，别改 `SizeToContent`）。设置窗口：`SettingsWindow.CardWidth`（当前 456，XAML 用 `{x:Static}` 引用它，只此一处） | 宽改了，所有 `Margin="15,..."` 和可用宽度随之变化，**必须重算每一行控件总宽**（见第 4 节）；设置窗口可直接跑 `--snapshot` 看 `.layout.txt` 的实测余量 |
| **改扣分规则**（出现/扣分/隐藏时刻、每几分扣几分） | `SleepRules.cs` 的 `Default`（默认值），或运行时走设置窗口 → 写进 `AppConfig` 的 `ShowAtText`/`DeductAtText`/`HideAtText`/`MinutesPerPoint`/`PointsPerStep` | 校验在 `SleepRules.IsValid`；`MaxScore` 默认 100，改它要同步 `ScoreEngine.MaxScore` 与 `ScoreVisual.Ramp` 端点 |
| **改状态文案** | 四句话的**默认值**在 `SleepRules.cs`（`StatusBefore` / `StatusHigh` / `StatusMid` / `StatusLow`）；用户可在设置窗口改，落在 `AppConfig` 的 `StatusBeforeText`…`StatusLowText`。分档界线在 `ScoreVisual.HighLine`(=80) / `MidLine`(=60)，取哪句由 `ScoreVisual.StatusForScore(score, rules)` 决定 | 留空 = 该档不显示文字（界面会连间距一起收掉）。断言在 `--selftest`：`AppendVisualChecks` 的"自定义文案"段 + `AppendSettingsChecks` 的配置往返 |
| **改配色渐变** | `ScoreVisual.cs` 的 `Ramp`（6 个分数支点 RGB）+ `ColorForScore` 插值；倒计时颜色在 `Countdown.ColorFor` | `Ramp` 首末点要对应分数 0 与 `MaxScore`；相邻色差自检阈值 12/255（`SelfTest` 里 `worst <= 12` 判 PASS） |
| **加成一条面板**（第三块） | 新增 `XxxPanel` 用户控件 + 在 `MainWindow.xaml` 的 `StackPanel` 内插入（注意细线逻辑 `SetPanelVisibility`/`UpdateWindowHeight` 只认「分数 + 倒计时」两块，需扩展）；配置加对应 `Hidden`/`Pinned` 字段与 `Refresh` 判定 | 当前 `scoreShow`/`cdShow` 是硬编码两块，加块要改 `Refresh`、`SetPanelVisibility`、`UpdateWindowHeight`、`SaveConfig`、`SyncMenuChecks` 等多处，工作量不小 |
| **改倒计时解析规则** | `Countdown.cs` 的 `TryParse`（接受写法、单位、纯数字≤23 当钟点 >23 当分钟）与 `TryParse` 调用的 `SleepRules.TryParseHm` | 解析用例在 `--selftest`（`AppendCountdownChecks`），新增写法后补一条用例 |
| **加一条自检断言** | `SelfTest.cs` 对应 `Append*` 段（视觉 `AppendVisualChecks`、设置 `AppendSettingsChecks`、倒计时 `AppendCountdownChecks`） | 断言是 `PASS/FAIL` 文本；`Write` 末尾扫 `FAIL` 决定总结论 |
| **重新生成图标** | `python scripts/make_icon.py`（`make_icon.py` 里的画法参数：`ARC_BOX`/`TEAL_ARC`/`BLUE_ARC`/颜色常量）；改完 `csproj` 会重新嵌入 `Assets/TimeGuard.ico`，不需手动改 csproj | 生成后立即 `python scripts/verify_exe_icon.py dist\TimeGuard.exe` 确认进 exe |
| **重建桌面快捷方式** | `python scripts/make_shortcut.py dist\TimeGuard.exe`（参数：目标 exe [快捷方式名]） | 见下方 PITFALLS：必须带 IDList，GUID 字节序 `01 14 02 00…` |
| **改热键** | `MainWindow.xaml.cs` 的 `VK_*` 常量 + `OnSourceInitialized` 里的 `RegisterHotKey`（`HOTKEY_CLICKTHROUGH`→L、`HOTKEY_MINIMIZE`→M 失败回退 H、`HOTKEY_CD_TOGGLE`→T 失败回退 K） | 改了要在 `WndProc` 的 `WM_HOTKEY` switch 也加对应分支；托盘灰色提示文案 `_minHotkeyDesc`/`_cdHotkeyDesc` 同步 |
| **改托盘菜单项** | `MainWindow.xaml.cs` 的 `BuildTray()` | 增删项注意 `CheckOnClick` 与 `_suppressTrayEvent` 防回环 |
| **改鼠标穿透行为** | `ApplyClickThrough`（`WS_EX_TRANSPARENT` 开关）+ `SetClickThrough` + `UpdateBtnRow` + `AppConfig.ClickThrough` | 默认 `false` 的约束见 PITFALLS「输入框打不进字」 |
| **改「到点式不可暂停」** | `Countdown.PauseAllowed` + `Pause`/`Resume`/`Restore` + `MainWindow.SyncPrimaryButton`/`SyncCdMenuItems`/`ToggleCountdown` | 四处入口（主按钮/右键/托盘/热键）必须同步禁用，缺一不可 |
| **改隐藏时段判定** | `SleepRules` 三时刻 + `MainWindow.Refresh` 的 `daytime`/`scoreShow`/`cdShow` | 白天的判定依赖 `NightPhase.Daytime` |

---

## 4. 改字号时必须重算横向总宽

窗口 `Width=183`，分数块与倒计时块左右各 `Margin=15`，所以**可用内容宽 = 183 − 30 = 153**。任何一行里并排控件的宽度之和不能超过 153，否则被裁切或撑破布局。

以倒计时那行为例（当前布局，`CdPanel` 内）：

- 输入框 `CdInput` `Width=68`
- 右侧按钮组：`CdPrimary` `Width=38` + `Margin="5,0,0,0"` + `CdReset` `Width=38`
- 整行 = 68 + (38 + 5 + 38) = **149 ≤ 153** ✅

**改字号时的检查步骤**：
1. 改 `FontSize` 前先记下当前控件宽。
2. 若字号变大导致文字变宽，要同步调 `Width`（如 `CdInput` 从 68 调到 72，则 72 + 81 = 153，正好顶满；再大就溢出）。
3. 或在 XAML 里直接量：把并排控件宽度 + 间距加起来，对照 153。
4. 改完跑 `dotnet publish` 后看 `--snapshot` 对照图，确认那一行没被裁。

> 同理，若改了窗口 `Width`（比如变 200），可用宽变成 `200 − 30 = 170`，上述 149 更宽裕；但若缩窗，要反向收紧控件宽度。

### 4.1 「别估，跑测量」——而且必须量**真窗口**

横向余量这种东西不要靠加法心算，`--snapshot` 会把设置窗口的每一行量出来写到 `.layout.txt`。

**⚠️ 关键约束：判据必须来自真实窗口的 `ActualWidth`。**
2026-09-22 的教训（`PITFALLS.md` §13）：`LayoutReport()` 原来是"把内容摘下来、按常量 `CardWidth` 离屏 Measure/Arrange"，
而 XAML 里的 `Width` 是另写的一份（396 ≠ 常量 456）。于是报告写着"每行都在可视区内、余量 45px"，
用户真窗口里「分」字却被完全挤出窗口 —— **量的是一个并不存在的宽度**。现在：

- `LayoutReport()` 会 `Show()` 一个真窗口（挪到屏幕外 `Left/Top=-6000`），读真实 `ActualWidth`，
  并**断言 `ActualWidth == CardWidth`**（XAML 里再出现一处硬写的宽度，这条就红）；
- 顺带用 `PrintWindow` 把真窗口抓成 `<png>.real.png`，可以直接看用户看到的那个样子；
- `--uicheck` 第 7 项也带一条 `WidthCheck()`，日常自检就能拦住宽度漂移。

**新加/修改任何测量断言时，先用一个已知故障值校准它**：把宽度改回 396 跑一次，必须看到 FAIL（实测余量负值）；
恢复后必须全 PASS。没被故障校准过的探针，只是又一份好看的数字。

### 4.1 设置窗口：别估算，直接跑实测

设置窗口控件多、还有字体度量误差，手算容易差出十几像素。2026-09-22 那次「扣分速率」一行就是这么漏的：
按汉字 13px 估算是 206 而可用 200，但实际溢出多少、最右边的「分」被裁掉没，只有量一遍才算数。

```bash
TimeGuard.exe --snapshot _verify/snapshot.png
cat _verify/snapshot.layout.txt
```

每行给出「右边界 x=…，余量 …px」，**余量为负就是被裁**（实现见 `SettingsWindow.LayoutReport`）。

两个坑：
- 判据要用「**内容区右边界坐标**」= 窗口宽 − 右 padding，不是「内容宽度」。两者差 2×padding，
  混起来会把余量算小 44px，虚报一批 FAIL（第一版就写错了）。
- 图片能看出成字的裁切，但吃不掉 1–2px 的贴边。**数字为准，图作旁证。**

改设置窗口宽度或任何一行控件宽度后，重跑上面这条命令再收工。

---

## 5. 调试与排错入口

- **窗口不显示 / 闪退**：先确认是隐藏时段（06:30–20:30 默认不显示）。非时段仍不显，查 `Refresh` 里 `shouldShow` 判定与 `_userMinimized`。
- **双击 exe 多开 / 弹回**：单实例互斥失效？查 `App.xaml.cs` 的 `MutexName` 与 `ShowEventName`；多开通常是旧进程没退干净。
- **图标空白 / 不对**：跑 `python scripts/probe_shell_icon.py` 区分「图标本身坏」还是「Windows 图标缓存陈旧」。
- **输入框打不进字**：先查 `ClickThrough` 是否被设成 `true`（包括老配置迁移）——见 PITFALLS。
- **改完逻辑想快速核对**：直接 `dotnet run -- --selftest` / `--snapshot`，不用手点界面。

---

## 6. 提交前自查清单

- [ ] 没把 `TargetFramework` 改成 `net9`（致命）
- [ ] 改了 `Ramp`/`ColorFor` 跑了 `--selftest` 确认配色连续（色差 ≤12）
- [ ] 改了 `TryParse` 在 `SelfTest` 补了解析用例
- [ ] 改了字号/窗口宽，重算过横向总宽（≤153），并看了 `--snapshot`
- [ ] 改了设置窗口的宽或某行控件宽，跑了 `--snapshot` 并读过 `.layout.txt` 的余量（必须全为非负）
- [ ] 改了状态文案，`--selftest` 的"自定义文案"与"设置窗口取值校验"两段都过
- [ ] 改了「到点式不可暂停」四处入口同步
- [ ] 新增配置字段给了默认值且在 `Sanitize` 有兜底，没删老字段读取
- [ ] 改了图标后 `verify_exe_icon.py` 确认进 exe
