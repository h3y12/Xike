# TimeGuard 架构文档

文中提到的桌面版 `.cs` / `.xaml` 文件均位于项目根目录下的 `src/`；构建与发布命令仍在项目根目录执行。

写给想接手、想读懂代码的人。读完这一篇，你应当能回答「当前时间是怎么变成屏幕上的分数和颜色的」「每个文件干什么」「为什么这么设计」。

---

## 1. 代码地图（逐文件职责）

### 入口与生命周期
- **`App.xaml.cs`** —— 应用入口（`App : Application`）。`OnStartup` 按 `e.Args[0]` 分发模式：无参数走正常 GUI；`--selftest`/`--snapshot`/`--icon`/`--uicheck`/`--uitest` 各自走自检分支。**所有会 `new MainWindow()` 的自检分支都先调 `AppConfig.UseOverrideFile(...)` 隔离配置**（否则会把真实 `config.json` 洗掉，见 PITFALLS）。正常启动走单实例互斥：
  - `MutexName = @"Local\TimeGuard.SingleInstance.v1"`（`Local\` 前缀 = 仅本会话/本用户命名空间，避免跨会话撞名）；第二个实例用 `ShowEventName = @"Local\TimeGuard.ShowWindow.v1"` 事件把已有窗口叫出来，而非再开一个。
  - `OnExit` 释放互斥体与信号。
- **`App.xaml`** —— 仅 `<Application.Resources />`，无内容。

### 配置
- **`AppConfig.cs`** —— 配置模型 + 加载/保存/迁移。字段逐条见 `README.md`「配置文件」。关键点：
  - `Load()`：优先读隔离文件（自检时 `_overrideFile` 非空）；否则读 `%APPDATA%\TimeGuard\config.json`，找不到再读旧 `%APPDATA%\SleepGuard\config.json` 并搬过来；损坏则静默回落默认。
  - `Sanitize()`：**所有新/旧配置都过这一关**——`ConfigVersion` 迁移、透明度/坐标合法性、`SleepRules` 合法性与倒计时字段修复。`Save()` 写 `WriteIndented` 的 JSON，**先写 `.tmp` 再原子替换并留一份 `.bak`**（直接覆盖写会在断电/磁盘满时留下空文件或半截 JSON，把窗口位置与规则全丢掉）；`Load()` 读不出主文件时回落到 `.bak`，两个都坏才回落默认值并把坏文件改名成 `.corrupt` 留证。
  - `Rules` 属性由文本字段推导出 `SleepRules`；`CaptureCountdown`/`RestoreCountdown` 把倒计时状态与 `Countdown` 互相序列化。
  - `UseOverrideFile(string)` 是 `internal static`，仅供自检分支设隔离文件路径 `IsolatedConfigPath`（输出文件旁的 `selftest-config.json`）。
    **隔离模式下 `Load()` 默认走短路分支**（只直接读那个文件，不做 `.bak` 回落、不做 `.corrupt` 改名）——因为恢复动作会改名/删磁盘文件，误伤到用户真实 `config.json` 就麻烦。要自检这条恢复逻辑本身，得先把 `ExerciseRecoveryForTest` 打开（同样是 `internal static`，只在 `AppendConfigFileChecks` 里短暂置真，`finally` 里务必关掉）。

### 纯逻辑层（不依赖 WPF，可独立单测/自检）
- **`SleepRules.cs`** —— 一夜的规则参数（`ShowAt`/`DeductAt`/`HideAt`/`MinutesPerPoint`/`PointsPerStep`/`MaxScore`，全 `init`），**外加四句状态文案** `StatusBefore`/`StatusHigh`/`StatusMid`/`StatusLow`（也由用户改，空串 = 该档不显示文字），含默认 `Default`。`Resolve(anyTimeInNight)` 把三个时刻落到「以所在那一夜为基准」的真实时间点上（跨零点按一夜顺序解释，不按当天先后）。`IsValid` 校验顺序与区间；`TryParseHm`/`FormatHm` 做 `HH:mm[:ss]` 解析（容错全角冒号、句点）。
- **`ScoreEngine.cs`** —— 计分纯函数 `Evaluate(now, rules) → ScoreState{Score, Phase(NightPhase), UntilDeadline}`。整数运算（`elapsedSeconds / stepSeconds * PointsPerStep`）避免浮点把整点算少一分。`NightPhase` 枚举：`Daytime`/`Countdown`/`Deducting`/`Exhausted`。`MaxScore = 100`。`FormatHms` 格式化 `h:mm:ss`。
- **`ScoreVisual.cs`** —— 分数 → 界面表现：`StatusForScore(score, rules)` 按 `HighLine`(=80)/`MidLine`(=60) 两个分界取规则里的文案（**文案不在代码里写死**）、`Ramp` 颜色支点（0/20/40/60/80/100 六档，逐分线性插值）、`ColorForScore`/`Hex`。独立于窗口，供 `--selftest` 直接断言。
- **`Countdown.cs`** —— 倒计时状态机（`Idle`/`Running`/`Paused`/`Finished`）+ 输入解析。
  - `TryParse(text, now, out plan, out err)`：认 `23:00`/`23:00:30`/`23`（到钟点）、`45m`/`45分钟`/`2h`/`45`（时长）；纯数字 `>23` 当分钟数。
  - `Start/Pause/Resume/Restart/Reset/Tick/Peek`：`Pause` 仅 `PauseAllowed`（= 时长模式）时才接受，钟点模式直接返回 `false`——这是「到点式不可暂停」的核心（见设计决策）。`Restore(...)` 从历史字段还原，并对钟点模式按钟点重算截止点治旧数据。
  - `Snapshot()` 把状态拍成扁平字段塞进 config；`ColorFor`/`BigText`/`Hms`/`SubLabel` 是界面渲染的纯函数入口。
- **`Alarm.cs`** —— 一个闹钟实体（`Id`/`Label`/`TimeOfDay`/`Enabled`/`Repeat`/`Days`/`DeleteAfterRing`/`ChainId`/`ChainIndex`）+ 重复规则 `AlarmRepeat{Once,Daily,Weekly}`。要点：
  - `SortKey` 把钟点折算成「**从凌晨四点起的分钟数**」（`DayCutoverHour = 4`）：04:00→0、23:00→1140、01:00→1260、03:59→1439。于是 `01:00` 排在 `23:00` **后面**，符合「今晚 01:30 睡」和「明晚 23:00 睡」是前后脚两件事的直觉。列表排序一律用它。
  - `NextRingAfter(after)` 从当天试到第 8 天（覆盖 WEEKLY 全 7 种星期），挑出第一个「严格晚于 `after` 且命中所选星期」的时刻；`Enabled=false` 直接返回 `null`。
  - `NightOf(now)` 同一条四点分界，用于分组；`DescribeCountdown` 出「3 小时后」「2 天后」这类中文说明。
  - `TryParseTimeText` 收用户的 `HH:mm` 输入（复用 `SleepRules` 的容错口径）。
- **`AlarmBook.cs`** —— 闹钟集合的纯逻辑（**刻意不读系统时钟**，时间一律由调用方传入，所以单测能任意快进，不必等到半夜三点）。
  - `Sort/NormalizeAll`：去重 + 规范化 + 按 `SortKey` 排；同一钟点内连锁串按 `ChainIndex` 排。
  - `DueBetween(from, to)`：报出这段时间里该响的闹钟。两条规则——① 同一个时刻只报一次；② 错过超过 `MissGraceSeconds`(90s) 的不补响（用户选的「错过就不补」，这个窗口的语义是「程序刚从睡眠里醒来时多久之内还认」，不是宽容度）。
  - **`_firedAt` 存的是「哪一次响铃报过了」（`id → 时刻`），不是布尔值**。写成布尔会变成「这个闹钟响过一次就永久报废」，「每天 08:00」第二天再也不会响——桌面版早期真踩过，见 §3 决策 11。
  - `AddChain(spec, label)`：按「起始时间 + 间隔 + 次数」生成一整串，共用一个 `ChainId`；每个成员**各自钉在自己的钟点上**，所以关掉 08:00 那个不影响 08:03 准时响。`InferChainSpec` 反推参数供设置窗回显。
  - `Dismiss(a)` / `DismissWholeChain(a)`：关掉一次响铃 / 手动关整串。语义见 §3 决策 12。
  - `Imminent(now)`：半小时内（含）要响的（`Alarm.ImminentMinutes = 30`）——挂件据此决定显不显示闹钟块。
- **`AlarmRinger.cs`** —— 响铃调度：把「到期事件」变成「开始响 / 响够了停 / 用户处理掉」。`AlarmBook` 只管"到点了没"，本类只管"响了之后怎样"。同一时刻最多响一个，后到的排队（`Accept`/`Tick`/`DismissCurrent`/`StopAll`）。`RingingState` 记住起止时刻，弹窗上那行「还有 N 秒」由它算。**也不读系统时钟**。
- **`WindowPlacement.cs`** —— 窗口摆放的纯计算：`DefaultPosition(workAreaRight, workAreaTop, width)` 给出贴右上角的默认位置；`Clamp(left, top, w, h, screenLeft, screenTop, screenW, screenH)` 把位置夹进**单个**矩形（兼容旧签名，内部转发给下面这个）；`ClampToNearest(left, top, w, h, screens)` 是**多屏**用的正路——先按「窗口中心离哪块屏中心最近」挑一块显示器，再在那块屏的工作区内夹取；`UsableHeight(height)` 把 `SizeToContent` 产生的 `NaN` 换成可用估值（`FallbackHeight = 260`）；`EdgeMargin = 28`；`ExpectedPhysicalWidth(dipWidth, dpiScale)` 算出「某个逻辑宽度在某个缩放下应该是多少物理像素」——**取整必须用 `MidpointRounding.AwayFromZero`**，因为 `Math.Round` 默认的银行家舍入会把 `183 × 1.5 = 274.5` 算成 274，而 Windows 是 275（这个偏差是自检抓出来的）。运行期的 `EnsureWindowSize` 与自检共用这一个函数，不各写一套。另附四组常见显示规格常量（`Fhd100`/`Qhd100`/`Uhd150`/`Uhd200`）供自检引用。
  **全部使用 DIP**：系统缩放决定 DIP 工作区，程序只按 DIP 摆放，因此不需要为分辨率写任何分支——1080P@100% 与 4K@200% 的 DIP 工作区同为 1920×1080，算出的默认位置完全一样。真正要管的只有「位置还在不在可视范围内」。抽成纯函数是为了能脱离真实屏幕断言（没有 4K 屏也能验），见 `PITFALLS.md` §14。
  **多屏必须用 `ClampToNearest`，不要用 `Clamp`**：`Clamp` 把虚拟桌面（所有屏幕的外接矩形）当成一整块，而两块并排的屏拼出的矩形**中间那条缝在物理上并不存在**——窗口会被夹到缝里，跨在两块屏之间、哪块上都摆不正。调用方 `MainWindow.ScreenWorkAreas()` 逐块屏给出工作区（`Screen.AllScreens`，主屏排第一，物理像素按 `TransformFromDevice` 换算成 DIP）；枚举失败时退化成「一块虚拟桌面」，行为与改动前一致。

### 界面层
- **`MainWindow.xaml`** —— 主窗口：`Width="{x:Static local:MainWindow.CardWidth}"`（**宽度只有这一个来源**，值写在 `MainWindow.CardWidth` 常量里）、`SizeToContent=Height`、无边框、`AllowsTransparency`、`Topmost`、`ShowInTaskbar=False`。`ScorePanel`(高 143, Margin 15,0,15,0) → `Divider`(1px) → `CdPanel`(Margin 15,9,15,12)。含右键 `ContextMenu`、最小化/关闭按钮、倒计时输入框与开始/重置按钮的样式与布局。
- **`MainWindow.xaml.cs`** —— 主界面逻辑（约 1300 行，最大文件）。职责：
  - Win32 钩子：`GetWindowLong/SetWindowLong`（`WS_EX_TRANSPARENT` 控穿透）、`RegisterHotKey/UnregisterHotKey`（三套热键）、`WndProc`（`WM_MOUSEACTIVATE` 强制激活、`WM_HOTKEY` 分发）。
  - 主循环 `Refresh()`：每 200ms（`_tick`）取 `ScoreEngine.Evaluate` + `Countdown.Tick`，算 `scoreShow`/`cdShow`/`shouldShow`，决定 `Show/Hide`、面板可见性、`UpdateWindowHeight`（手写高度，不用 SizeToContent 防残影）、分数/倒计时渲染、托盘文案。**第一句是 `EnsureWindowSize()`** —— 尺寸不对的话，后面量出来的每一块高度都是错的。
  - 托盘 `BuildTray()`：完整菜单（显示/最小化、两块显隐、唤起/开始/重置/快捷分钟、设置、鼠标穿透、透明度、热键说明）；图标 `LoadAppIcon()` 从内嵌资源读 `Assets/TimeGuard.ico`，流存静态字段 `_iconStream` 防 GC 清空。
  - 收起/展开、设置窗口调用、鼠标穿透切换、输入框焦点兜底（`GotKeyboardFocus` + `WM_MOUSEACTIVATE`）。
  - **窗口摆放与分辨率适配**：`RestorePosition()` 调 `WindowPlacement.DefaultPosition/ClampToNearest` 决定初始位置
    （`ScreenWorkAreas()` 逐块屏给工作区，多屏时保证整窗落在**某一块**屏内，不会停在两屏之间的缝上）。
    **`EnsureWindowSize()` 每帧核对尺寸**（正常时零开销）：① 逻辑宽度漂了就钉回 `MainWindow.CardWidth`；
    ② 物理宽度不对（`GetClientRect` ≠ `CardWidth × 缩放`）就 `SetWindowPos` 补一刀。
    没有它的话，**DPI 一切换窗口的物理尺寸就不跟着走** —— 实测远程 150% 下窗口只有 183px 而该有 274.5px，
    于是内容按 1.5 倍画、容器只有 1.0 倍，大字被裁、小字折行、按钮重叠（见 `PITFALLS.md` §19）。
    `OnDpiChanged` 里先 `EnsureWindowSize()`，再 `InvalidateMeasure/UpdateLayout`，最后 `ClampToScreen()`，
    并在 700ms 后复查一次；`SystemEvents.DisplaySettingsChanged`（改分辨率/插拔显示器）经 `Dispatcher.BeginInvoke`
    走同一条 `ClampToScreen()`，订阅在 `OnClosed` 退订。
    ⚠️ **不要再用「`SizeToContent` 切 Manual → 失效测量 → 切回来」那套**（§14 的老写法）：
    它会把尺寸反算的时机暴露出来，正是 §19 那个 bug 的来源。
  - **尺寸相关的现场记录**：`LogSize()` 在 DPI 变化/宽度漂移时往 `%TEMP%\TimeGuard-dpi.log` 追加一行
    （逻辑宽、物理宽、WPF 的 DPI、期望值）。这个漂移只在"另一种缩放"上出现，本机复现不了，
    留一份现场记录省得下次从头猜。正常运行不写这个文件。
  - **自检钩子**：`SettingsRoundTripCheck`/`CountdownUiRoundTripCheck`/`StartUiTestLog`/`PrepareSnapshot`/`DumpTrayIcon` 等 `internal` 方法，供 `App.xaml.cs` 与 `SelfTest`/`Snapshot` 调用且不污染用户配置。
- **`SettingsWindow.xaml(.cs)`** —— 规则与文案设置窗（宽 `SettingsWindow.CardWidth` = 456，XAML 用 `{x:Static}` 引用，只此一处定义）。非模态（`Show` 而非 `ShowDialog`，改的过程中挂件照常刷新）；`Esc` 取消、`Enter` 保存；分档标签按 `ScoreVisual.HighLine`/`MidLine` 生成，改阈值不会让标签对不上。`TryBuildRules` 抽成 `internal static`（10 参版带四句文案，6 参版为兼容旧用例转发）供 `--selftest` 做取值校验；`LoadForTest`/`SaveForTest` 供 `--uicheck` 调；**`LayoutReport()` 真开一次窗口（挪到屏幕外）量出每行最右控件相对窗口右边缘的余量，并断言 `ActualWidth == CardWidth`**，专治"控件挤出可视区、字被裁掉却不报错"（见 `DEVELOPING.md` §4.1；历史坑见 `PITFALLS.md` §13 —— 曾经因为按常量离屏量、而 XAML 里宽度另写一份，检查全绿但用户看到的字被裁掉）。`WidthCheck()` 是同一断言的轻量版，供 `--uicheck` 调用。
- **`AlarmWindow.xaml(.cs)`** —— 闹钟设置窗（宽 `AlarmWindow.CardWidth` = 500，比设置窗宽一点，因为闹钟方块要横排时间 + 开关）。两页签：**闹钟**（普通列表，逐条增删改）与**起床闹钟**（连锁：起始时间 + 间隔 + 次数 → 一键生成一串；也能逐条手动加）。列表项与挂件里是同一套「一个闹钟一个方块、只显示时间 + 开关、生效黑字不生效浅灰字」的规矩。窗口可滚动（内容比设置窗长）。关闭时把整本 `AlarmBook` 交回 `MainWindow`。
- **`RingWindow.xaml(.cs)`** —— 响铃弹窗。置顶、无边框、出现在主屏右下（不挡挂件）。显示闹钟名 + 提示语（`AlarmRinger.RingingText`）+ 倒计时秒数 + 两个动作：**知道了**（`Dismiss`）/ **稍后提醒**（`Snooze`）。到点自动静音时弹窗不关，仍留着等你处理——防赖床靠的是「下一个按间隔再响」，不是这一下响多久。连锁串还会多一行「还有 N 个会继续响」。
- **`AlarmSound.cs`** —— 发声。默认走 `SystemSounds`（零依赖）；配了自定义音频文件时用 WPF 自带的 `MediaPlayer` 播（支持 mp3/wav/wma，不引第三方库）。系统音的「循环」不自己做循环，而是由 `MainWindow.Refresh` 每 200ms 敲一次——WPF 的 `SystemSounds` 是一次性的，自己开线程循环会在停铃时留下一个停不掉的线程。

### 自检与验证
- **`SelfTest.cs`** —— `--selftest` 报告：逐分钟模拟与引擎对拍（`CrossCheck`，两套独立实现互不参考）、边界断言、`ScoreVisual` 文案配色断言、`SettingsWindow.TryBuildRules` 取值表、`Countdown` 解析/状态机/落盘还原全链路。起点固定（`BaseNight = 2026-09-21`），结论与「现在几点」无关。另有 `AppendDpiWidthChecks`（§19 的防回归）：把 100/125/150/175/200% 下的期望物理宽度全钉住，并断言窗口宽度真的来自 `MainWindow.CardWidth` 这个常量 —— ⚠️ **这个漂移截图看不出来**（离屏渲染会强行把宽度设成常量），只能直接读 `Window.Width` 属性。
- **`Snapshot.cs`** —— `--snapshot` 离屏渲染：对每张样本 `new MainWindow()` 后 `PrepareSnapshot` 摆好状态，用 `RenderTargetBitmap` 出图拼成对照大图；下半部分是**子窗口面板区**，把 `SettingsWindow`、`AlarmWindow` 的两页、`RingWindow` 各按自己的真实宽度离屏渲染——宽度一律 `TakePanel(win, caption, XxxWindow.CardWidth, MaxHeight)`，不许在这里另写一份数字（写两份必然漂移）。卡片高度按 `Measure` 实测，分数块收起那几张会自然变矮。**同时把 `SettingsWindow.LayoutReport()` 写到同名的 `.layout.txt`**，给出每行的防溢出实测。`App.xaml.cs` 里跑这个模式前强制 `UseOverrideFile`，否则会把用户真实配置洗掉。

  ⚠️ **样卡的文字标注也是结论，写错就是假证据。** 2026-09-24 踩到：一张标注"生效亮字、不生效浅灰字"的卡其实只画得出 1 个闹钟——挂件的闹钟块只收「生效且半小时内会响」的，灰字根本进不来；而"黑色字生效 / 浅灰色字不生效"这个观感**只存在于 `AlarmWindow` 的列表里**。改法是给面板区补上闹钟设置窗，卡片标注改成如实描述。**每加一张卡/一个面板，先问一句「标注里说的东西，这张图真的画得出来吗」**；画不出来就换地方画，或者改标注。
- **`app.manifest`** —— DPI 感知 `PerMonitorV2` + `dpiAware true/pm`；`<supportedOS>` 声明 Win10。WinForms 仅用于托盘，WPF 负责 DPI，故 csproj 屏蔽 `WFAC010/WFO0003`。
- **`TimeGuard.csproj`** —— `WinExe` + `net8.0-windows` + `UseWPF` + `UseWindowsForms` + `Nullable` + `LangVersion 12`；`ApplicationIcon`/`ApplicationManifest` 指向资源；`<Resource Include="Assets\TimeGuard.ico" />` 把图标嵌进程序集。
- **`scripts/*.py`**（见下节「脚本」）。

---

## 2. 数据流：从「当前时间」到「屏幕上的分数与颜色」

正常 GUI 路径（自检路径在虚线后分支，不经过窗口）：

```
系统时钟 DateTime.Now
        │
        ▼
Refresh()  (每 200ms, _tick)
        │
        ├─ ScoreEngine.Evaluate(now, _rules)  ──→  ScoreState{ Score, Phase, UntilDeadline }
        │        │  SleepRules.Resolve 把 ShowAt/DeductAt/HideAt 落到本夜真实时刻
        │        │  整数运算算扣分，>=0 锁 0
        │        ▼
        │   ApplyScoreState(now, st)
        │        ├─ ScoreText.Text       = st.Score
        │        ├─ ClockText.Text       = now.ToString("HH:mm")
        │        ├─ StatusText.Text      = 规则里的文案（扣分前 StatusBefore；扣分中 StatusForScore(score, rules)）
        │        │                         空串 → Visibility.Collapsed（连间距一起收掉）
        │        └─ ScoreText.Foreground = BrushForScore(score)
        │                                      = ScoreVisual.ColorForScore(score)（Ramp 插值）
        │                                      └─ 缓存进 _scoreBrushCache（Freeze 后复用）
        │
        └─ _cd.Tick(now)  ──→  CountdownState{ Phase, Remaining }
                 ▼
            ApplyCdState(cd)
                 ├─ SetCdVisual(...)  → CdTime.Text / CdTime.Foreground（Countdown.ColorFor）
                 │                    → CdLabel.Text / Foreground（SubLabel / 错误色）
                 ├─ SyncPrimaryButton(phase)  → 开始/暂停/继续/再来；到点式运行态直接 Collapsed
                 └─ SyncCdMenuItems(phase)    → 右键/托盘菜单那一项跟着变（到点式禁用）
```

关键解耦点：**`ScoreEngine` / `Countdown` / `SleepRules` / `ScoreVisual` 全是纯函数/纯数据，不引用任何 WPF**。窗口只是「把 ScoreState/CountdownState 画出来」。所以 `--selftest` 可以不建窗口、独立核对全部规则与配色。

面板显隐的判定（也在 `Refresh`）：
- `scoreShow = !ScoreHidden && !daytime`（`daytime` = 阶段为 `Daytime`）
- `cdShow = !CountdownHidden && (Phase != Idle || !daytime || CountdownPinned)`
- `alarmShow = !AlarmPanelHidden && _alarms.Imminent(now).Count > 0`（**只有半小时内有闹钟才出现**，平时完全不占位置）
- `shouldShow = (scoreShow || cdShow || alarmShow) && !_userMinimized`
- 高度是手算的 `UpdateWindowHeight`（测 `DesiredSize`），不靠 `SizeToContent=Height` 实时撑（避免分层窗口残影）。

⚠️ **`Refresh` 里的顺序不能换**：必须先 `ApplyAlarmState(now)` 把闹钟方块填进 `AlarmList`，**再** `UpdateWindowHeight(...)`。反过来的话量到的是空列表，窗口高度会少算一整块，最下面那个方块被裁掉——而且因为实时刷新每 200ms 跑一次，用户看到的是「块时有时无」而不是稳定错误，很难截图说明。`PrepareSnapshot` 里同理（曾经就是这里先量后填，出图时才暴露）。

⚠️ **XAML 里写死的 `IsChecked="True"` 会在 `InitializeComponent` 期间触发 `Checked`，而那时兄弟控件还没建好。** `AlarmWindow` 的页签就是这么写的，`Tab_Checked` 里第一句 `if (PageNormal is null || PageWake is null) return;` 把这次提前触发挡掉了——代价是**打开窗口时标题下那行说明是空的**，点了页签才冒出来（2026-09-24 由快照面板抓出）。修法是把"页签落实下去"抽成 `ApplyTab()`，构造函数和 `Tab_Checked` 都调它。**凡是"勾选状态写在 XAML 里 + 事件里做首屏初始化"的地方都有这个坑**：首屏会走"控件还没建好"那条早退分支，必须由构造函数补一次。

闹钟这条链（`Refresh` 内）：

```
_alarms.DueBetween(lastTick, now)  ──→  [AlarmFiring{Alarm, DueAt, IsLastInChain}]
        │
        ▼
_ringer.Accept(due, now, AlarmRingSeconds)   // 没在响就立刻开始响第一个，否则排队
        │
        ▼
_ringer.Tick(now, AlarmRingSeconds)  ──→ justStopped
        │                                    │
        │                                    └─ true → 静音；弹窗留着等你处理
        ▼
AlarmSound.Poke(...) 每 200ms 敲一次系统音（自定义音频走 MediaPlayer）
        │
        ▼
RingWindow 显示：闹钟名 + 提示语 + 剩余秒数 + [知道了 / 稍后提醒]
```

---

## 3. 关键设计决策及其理由（已回代码核对）

1. **锁 `net8.0-windows`，绝不升 net9** —— `csproj` 写死。`App.xaml.cs`/PITFALLS 记载：.NET 9 apphost 在本机 Win10 19043 崩 `CLR: Assert failure: !AreShadowStacksEnabled()`。任何迁移请求都先否决这一条。

2. **单实例互斥用 `Local\` 前缀命名** —— 见 `App.xaml.cs` 的 `MutexName`/`ShowEventName`。`Local\` 把命名体限定在本会话命名空间，避免与其他用户/会话的同名互斥体冲突；第二个实例不新建窗口，只 `Set()` 一下 `ShowEventName` 事件，主实例的 `ListenForSecondLaunch` 后台线程 `WaitOne` 到就呼叫窗口。这同时根治了「最小化后窗口自己弹回来」的误报——那其实是残留旧进程（多开），不是渲染问题。

3. **「最小化后弹回来」= 多开，不是 bug** —— 同上，单实例锁 + `ShowSignal` 机制后，正常只有一个窗口。`--uicheck` 专门验证：最小化后窗口保持 `IsVisible=False`，恢复后才回来。

4. **界面重影 = 用户自己的旧窗口没关** —— 不是渲染 bug。教训写在 PITFALLS：症状像叠加/重影，先数现场对象（窗口数、进程数）。

5. **鼠标穿透默认 `false`，且用 `ConfigVersion` 迁移把老配置里的 `true` 掰回 `false`** —— `AppConfig.ClickThrough` 注释明确「默认必须是 false」。历史版本（ConfigVersion 1）把它当默认，导致输入框收不到键盘。修复三件套：`ClickThrough` 默认 `false` + `Sanitize` 里 `if (c.ConfigVersion < 2) c.ClickThrough = false` + `WndProc` 的 `WM_MOUSEACTIVATE` 强制回 `MA_ACTIVATE` 兜底拿到键盘焦点。穿透开着时按住 `Ctrl` 临时恢复可交互。

6. **到点式倒计时不可暂停** —— `Countdown.PauseAllowed` 仅时长模式为 `true`；`Pause(now)` 在钟点模式直接 `return false`。理由：`Resume()` 的实现是 `deadline = now + 剩余`，对「45 分钟」正确，但对「到 23:30」等于把目标时刻往后推（实测暂停 10 秒后配置落下 `23:30:10.92`）。修法覆盖四个入口：主按钮在钟点运行态 `Collapsed` 并提示「不可暂停」（`SyncPrimaryButton`/`SubLabel`）、右键与托盘菜单那一项禁用且文案变「到点式不可暂停」（`SyncCdMenuItems`）、热键与 `ToggleCountdown` 调用 `Pause` 失败即 `return`。`Restore()` 对钟点模式按钟点**重算**截止点（治旧脏数据），但 `Phase == Finished` 必须原样保留——否则昨晚已结束的计时次日早上会自己「顺延到今晚」重开。

7. **自检模式必须隔离配置** —— 见 `App.xaml.cs` 注释与 `IsolatedConfigPath`：`--snapshot` 会给每张卡片 `new MainWindow()`，而窗口 `Loaded` 里有 `SaveConfig()`、`OnClosed` 里有 `PersistCountdown()`，跑一次快照就会把用户真实 `config.json` 洗成「默认位置 + 倒计时清空」。而且写入是**幂等**的——跑第二次 `diff` 反而「无差异」，极具迷惑性。所有会建窗口或落盘的自检模式统一 `AppConfig.UseOverrideFile(IsolatedConfigPath(...))`。

8. **托盘图标流必须存静态字段** —— `MainWindow._iconStream` 静态持有 `MemoryStream`；`LoadAppIcon` 用 `new Icon(stream, SystemInformation.SmallIconSize)` 读内嵌 `Assets/TimeGuard.ico`。若流被 GC，托盘图标变空白。`_iconSource` 静态记录来源，`--icon` 自检据此判定是否退回系统默认。

9. **窗口高度手算，不用 SizeToContent 实时撑** —— `UpdateWindowHeight` 用 `Measure` 量 `DesiredSize` 再赋值，且 `Math.Abs(Height - h) > 0.5` 才改。理由注释：`SizeToContent=Height` 会让窗口先按中间尺寸创建再改，分层窗口留残影；倒计时小字可能换行，高度也写不死。

10. **图标/清单/资源共建** —— csproj 把 `Assets\TimeGuard.ico` 既当 `<ApplicationIcon>` 又当 `<Resource>` 嵌进程序集；托盘图标从内嵌资源读，保证 exe / 托盘 / 快捷方式同一图案。清单负责 PerMonitorV2 DPI 感知。

11. **`AlarmBook._firedAt` 必须记「时刻」，不能记「响过没有」** —— 这是闹钟功能里最容易写错、也最容易被测试漏掉的一处。写成 `HashSet<string>` + `if (_firedAt.Contains(a.Id)) continue;` 时，行为退化成「**这个闹钟响过一次就永久报废**」：一次性闹钟看不出来（本来就要删），但「每天 08:00」的闹钟**第二天再也不会响**。而只测「今天响不响」的用例全绿。现在记的是 `Dictionary<string, DateTime>`，判据是 `_firedAt[a.Id] == ringAt`——只有「同一个时刻已经报过」才跳过，新的一天算出新时刻，照常响。回归用例见 `SelfTest.AppendAlarmChecks` 里那组跨三天 + 跨周的断言。

12. **连锁起床闹钟「关掉一个，下一个仍准时响」= 每个成员各自钉在自己的钟点上** —— 不是「关掉的时刻 + 间隔」。`08:00 + 3分 + 3次` 生成的 `08:00 / 08:03 / 08:06` 三个闹钟互相独立；用户 08:00 关掉第一个（哪怕拖到 08:02 才点「知道了」），第二个仍在 `08:03:00` 响，不会漂成 08:05。三种关闭语义（用户逐条确认过的）：
    - 关掉串里**不是最后一个** → 只记这一响过了，后面的照常响（这就是防赖床）；
    - 关掉**最后一个** → 这一串结束；若整串都是「仅一次」，整串删掉，重复的只结束这一轮；
    - **手动关整串**（`DismissWholeChain`）→ 剩下的**一起关**。

13. **「以凌晨四点为界」是排序与「哪一夜」的统一口径** —— `Alarm.DayCutoverHour = 4`。设成 0 点的话，「今晚 01:30 睡」和「明晚 23:00 睡」会被排到列表首尾两端，而它们在用户心里是前后脚的两件事。含义（用户确认过的原话：「把 1 点–3 点的闹钟放到 24 点以后，4 点作为新一天」）：`04:00 → 0` 是新一天的起点，`03:59 → 1439` 是这一天的最后一个，所以列表顺序是 `… 22:00 → 23:00 → 01:00 → 02:00 → 03:00`，且 `04:00` 排在 `03:59` **前面**。`SortKey` 与 `NightOf` 共用这一个常量。

---

## 4. 配置的版本迁移机制（ConfigVersion 是干什么的、为什么需要）

`ConfigVersion` 是「配置格式版本」，当前值 `2`（`AppConfig.CurrentConfigVersion`）。

**为什么需要**：配置是「代码行为」与「用户偏好」之间的契约。代码改了默认行为或字段语义时，已经在磁盘上的老 `config.json` 不会自动跟着变。若只改代码里的默认值，老用户磁盘上的旧值会一直压着新默认——典型就是「鼠标穿透被老配置里的 `true` 锁死，怎么改代码都没用」。

**机制**：
- 每次 `Load()` 都过 `Sanitize()`，其中：
  ```csharp
  if (c.ConfigVersion < CurrentConfigVersion) c.ClickThrough = false;  // v1→v2：把老默认 true 掰回 false
  c.ConfigVersion = CurrentConfigVersion;                              // 升级标记，避免下次再冲
  ```
- 之后用户自己开穿透会被记成 `ConfigVersion = 2`，重启后不再被这段逻辑改回——迁移只针对「更老」的配置。
- `Sanitize` 还负责所有越界/写坏字段的兜底（透明度、坐标 NaN、时刻解析失败、分钟/扣分区间、`CdMode`/`CdPhase` 非法清空倒计时），让「手改坏 config.json」也不会让窗口消失或永驻。

**新增字段怎么办**（接手者必读）：将来若又要改默认行为，照下面做——
1. 把 `CurrentConfigVersion` 加 1（如变 `3`）；
2. 在 `Sanitize` 里加 `if (c.ConfigVersion < 3) { /* 把老字段迁到新语义 */ }`；
3. **不要删除老字段的读取**，否则旧配置反序列化失败会整体回落默认值（虽然 `Load` 有 catch 兜底，但会丢用户设置）。新字段给好默认值即可。

---

## 5. scripts/ 里的 Python 工具（非运行时依赖，仅开发/验证用）

| 脚本 | 作用 | 关键坑（详见 PITFALLS） |
|------|------|------------------------|
| `make_icon.py` | 按托盘画法生成 `Assets/TimeGuard.ico`（9 尺寸：16/20/24/32/40/48/64 用 BMP 条目、128/256 用 PNG 条目）+ `_verify/icon-preview.png` | 每个尺寸单独渲染后手写 ICO 容器；小尺寸必须 BMP 条目兼容性最稳 |
| `make_shortcut.py` | 直接拼 `.lnk` 二进制写桌面快捷方式（本机禁 COM，走不了 WScript.Shell） | ① GUID 落盘不是大端字面量，`LinkCLSID` 应为 `01 14 02 00 … C0 …`；② `.lnk` 必须带 `LinkTargetIDList`，只带 `LinkInfo` 时 `ShellExecuteW` 回 `SE_ERR_NOASSOC(31)`；IDList 不手拼，调 `SHParseDisplayName` 让 shell 给 |
| `uitest.py` | 配合 `--uitest`：真实鼠标点击 + 键盘敲键验证输入框可用 | 必须 `SetProcessDPIAwareness(2)` 在坐标 API 之前；绕过前台锁先敲 ALT 再 `SetForegroundWindow` |
| `verify_exe_icon.py` | 从编译好的 exe 抽图标存 PNG，确认图标真写进去了 | Win32 句柄按 `void*` 声明，否则 64 位句柄被当 int 截断报 `OverflowError` |
| `inspect_icon.py` | 只读诊断：ICO 各条目 / exe 资源段 / lnk 指向 | 区分「图标本身坏」与「Windows 图标缓存陈旧」 |
| `probe_shell_icon.py` | 用 `ExtractIconExW`（直读文件）与 `SHGetFileInfoW`（走 shell 缓存）对比 | 前者有图后者空白 = 缓存陈旧 |
| `grab_desktop.py` | `PrintWindow` 抓桌面图标层（不改屏幕内容） | 桌面在 `Progman/WorkerW → SHELLDLL_DefView → SysListView32` 串里 |
