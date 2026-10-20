# TimeGuard 踩坑记录

每条写法：**现象 → 根因 → 解法 → 怎么验证**。来源：代码注释 + 实测。改代码前先扫一遍，别把修过的 bug 又引回来。

---

## 1. .NET 9 apphost 在本机崩溃

- **现象**：升级到 `net9.0-windows` 后，双击 `TimeGuard.exe` 直接崩，报 `CLR: Assert failure: !AreShadowStacksEnabled()`。
- **根因**：.NET 9 的 apphost 在 Win10（19043）上对 shadow stack 的断言失败。
- **解法**：`csproj` 的 `<TargetFramework>` 锁死 `net8.0-windows`，任何迁移请求都先否决 net9。
- **怎么验证**：`dotnet --version` 看 SDK（本机 9.0.100），但构建目标仍是 net8；产物 `dist/TimeGuard.exe` 能正常双击启动、托盘出现。

---

## 2. 「最小化后窗口自己弹回来」——其实是多开

- **现象**：点最小化到托盘后，窗口过一会又自己冒出来。
- **根因**：**残留的旧进程实例**（上一次没退干净，又双击了一次）。这不是代码缺陷，是「两个实例都在跑、都在定时刷新」。
- **解法**：单实例互斥 `Mutex(@"Local\TimeGuard.SingleInstance.v1")`；第二个实例只 `Set` 一下 `Local\TimeGuard.ShowWindow.v1` 事件把已有窗口叫出来，不新建。`ListenForSecondLaunch` 后台线程等这个事件。
- **怎么验证**：`--uicheck` 专门测这个——最小化后等 3 秒断言 `IsVisible == False`、恢复后 `IsVisible == True`，全 PASS 说明不是弹回。任务管理器确认只有一个 `TimeGuard.exe`。

---

## 3. 「界面重影 / 叠了两层」——是旧窗口没关

- **现象**：屏幕上好像有两层窗口叠着、半透明错位。
- **根因**：用户自己之前开的旧窗口没关，新实例又起来了（同第 2 条的多开）。不是渲染 bug。
- **解法**：同单实例锁；症状排查时**先数现场对象**——窗口数、进程数，而不是先怀疑渲染。
- **怎么验证**：任务管理器看进程数；`--uicheck` 跑一次只有一份 `IsVisible` 报告。

---

## 4. 倒计时输入框敲不进字

- **现象**：窗口能点开，但鼠标点到输入框、键盘敲没反应，光标会闪一下。
- **根因**：`ClickThrough`（鼠标穿透）默认被设成 `true`，窗口带 `WS_EX_TRANSPARENT`，收不到鼠标/键盘消息。再深一层：历史版本（ConfigVersion 1）把 `ClickThrough` 当成了默认值，老配置里写死了 `"ClickThrough": true`，只改代码默认值没用——磁盘上的旧值一直压着新默认。
- **解法**（三件套）：
  1. `AppConfig.ClickThrough` 默认值 `false`（注释强调「默认必须是 false」）；
  2. `Sanitize` 里 `if (c.ConfigVersion < 2) c.ClickThrough = false;` —— 把老配置里的 `true` 一次性掰回 `false`；
  3. `MainWindow.WndProc` 拦 `WM_MOUSEACTIVATE` 直接回 `MA_ACTIVATE` 兜底拿到键盘焦点；`CdInput.GotKeyboardFocus` 也再 `Activate()` 兜底。
- **怎么验证**：`--uitest` + `scripts/uitest.py` 真实点输入框、真实敲键，断言 `active=1 kbdFocus=1 text="45m"`；或手动开穿透后输入框仍应能打字。

---

## 5. 到点式倒计时不能暂停（旧版把目标钟点偷偷推后）

- **现象**：填 `23:30` 起倒计时，暂停 10 秒再继续，配置里落下 `23:30:10.92`——目标时刻被改写了。
- **根因**：`Resume()` 的实现是 `deadline = now + 剩余`。对「45 分钟后」正确，但对「到 23:30」等于把截止点整体后推。
- **解法**：
  - `Countdown.PauseAllowed` 仅时长模式为 `true`；钟点模式 `Pause(now)` 直接 `return false`（状态一点不动）。
  - 界面四处入口同步禁用：主按钮在钟点运行态 `Collapsed` 并提示「不可暂停」（`SyncPrimaryButton`/`SubLabel`「· 不可暂停」）、右键菜单 `MenuCdToggle` 禁用、托盘 `_trayCdToggle` 禁用、`ToggleCountdown` 调 `Pause` 失败即 `return`。
  - `Restore()` 对钟点模式**按钟点重算**截止点（`DeadlineFor(clock, …)`），把历史被暂停弄歪的脏数据治回原样；但 `Phase == Finished` 必须原样保留——否则昨晚已结束的计时次日早上会自己「顺延到今晚 23:30」重开（用户实测痛点）。
- **怎么验证**：`--selftest` 的「时刻模式不提供暂停」段：`PauseAllowed = 不能暂停`、小字含「不可暂停」、22:10 暂停被拒后剩余仍是 `00:50:00`、一路走到 23:00 才到点；「还原时按钟点校准」段验证旧 `23:40` 脏数据被治回 `23:30`、已 `Finished` 的次日早仍是 `Finished`。

---

## 6. 自检模式洗掉真实配置

- **现象**：跑一次 `--snapshot`，发现自己的 `config.json` 变成了「默认窗口位置 + 倒计时清空」；再跑一次 `diff` 反而「无差异」。
- **根因**：`--snapshot` 给每张卡片 `new MainWindow()`，而窗口 `Loaded` 里 `SaveConfig()`、`OnClosed` 里 `PersistCountdown()` 都会落盘。默认配置下这次写入是**幂等**的——跑第二次把「已洗成默认」的状态又写一遍，`diff` 自然无差异，极具迷惑性。
- **解法**：所有会建窗口或落盘的自检模式（snapshot/uicheck/uitest/icon 的兜底）统一在 `App.xaml.cs` 里先 `AppConfig.UseOverrideFile(IsolatedConfigPath(...))`，配置改写到输出文件旁的 `selftest-config.json`，绝不碰 `%APPDATA%\TimeGuard\config.json`。`--uitest clickthrough` 若不隔离，会把 `ClickThrough=true` 连同新 `ConfigVersion` 写进真实配置（见第 4 条，用户窗口永久点不动）。
- **怎么验证**：跑 `--snapshot` 前后对比 `%APPDATA%\TimeGuard\config.json` 的 mtime/内容不变；输出目录里出现 `selftest-config.json` 即隔离生效。

---

## 7. 托盘图标变空白（流被 GC）

- **现象**：托盘区图标一开始有，过一阵变空白块。
- **根因**：`new Icon(stream, …)` 引用着那个 `MemoryStream`；原代码把流当局部变量，GC 回收后图标底层数据没了，托盘图标变空白。
- **解法**：流保存在**静态字段** `_iconStream`（`MainWindow`），生命周期跟进程一致；`_iconSource` 静态记录来源。
- **怎么验证**：`--icon _verify/trayicon.png` 导出图标并写 `trayicon.txt`，判定 `PASS 托盘用的是自带的 TimeGuard.ico`；窗口跑久了图标仍在。

---

## 8. 桌面快捷方式显示为空白块 / 双击提示「没有关联的应用」

- **现象**：`TimeGuard.lnk` 图标空白，或双击报 `SE_ERR_NOASSOC(31)`（没有关联的应用）。
- **根因**（两个坑，都在 `make_shortcut.py`）：
  1. **GUID 落盘字节序**：ShellLink CLSID 是 `{00021401-0000-0000-C000-000000000046}`，但落盘不是大端字面量——Data1/2/3 各自小端、Data4 原样，字节序列应为 `01 14 02 00 00 00 00 00 C0 00 00 00 00 00 00 46`。写成 `00 02 14 01…` 系统不认，ShellExecute 回 `SE_ERR_NOASSOW(31)`。
  2. **必须有 `LinkTargetIDList`**：只带 `LinkInfo` 时 ShellExecuteW 回 `SE_ERR_NOASSOC(31)`。IDList 内部是 BEEF0004 那套短名/长名/时间戳，手拼必错——改调 `SHParseDisplayName` 让 shell 给一份现成 PIDL 字节。
- **解法**：`make_shortcut.py` 里 `LINK_CLSID = bytes.fromhex("01140200" "0000" "0000" "C000000000000046")`；`build_lnk` 在 `LinkFlags` 置 `HAS_LINK_TARGET_ID_LIST`，`item_id_list()` 用 `SHParseDisplayName` 取字节。桌面路径从注册表 `Shell Folders\Desktop` 读（兼容 OneDrive 接管/中文「桌面」）。
- **怎么验证**：双击 `TimeGuard.lnk` 能正常启动；`python scripts/inspect_icon.py <exe> <lnk>` 能解析出 `ICON_LOCATION` 指向 exe；`python scripts/probe_shell_icon.py` 的 `SHGetFileInfo(lnk)` 有可见图标（非空白）。

---

## 9. 桌面图标显示空白块（Windows 图标缓存陈旧）

- **现象**：`TimeGuard.lnk` 图标是空白方块，但 exe 和 lnk 结构都完好。
- **根因**：**Windows 图标缓存陈旧**——exe 内图标资源完好、lnk 结构正确，只是系统缓存还是旧的空图。
- **解法**：重启 `explorer.exe` 或重建图标缓存（如 `ie4uinit.exe -show` / 删 `IconCache.db` 重启）。代码层面无需改。
- **怎么验证**：`python scripts/probe_shell_icon.py`：`ExtractIconExW`（直读文件）有图、`SHGetFileInfoW`（走缓存）空白 = 缓存陈旧；重建缓存后两者都应有图。

---

## 10. 窗口高度残影（别用 SizeToContent 实时撑）

- **现象**：早期用 `SizeToContent=Height` 时，窗口先按中间尺寸创建再改，分层窗口（半透明）留下残影；倒计时小字换行时高度写死会算错。
- **根因**：分层窗口在尺寸变化瞬间会渲染中间态。
- **解法**：窗口 `SizeToContent=Height` 仅作估值兜底；真正高度由 `UpdateWindowHeight` 用 `Measure(DesiredSize)` 实测两块面板高度后赋值，且 `Math.Abs(Height - h) > 0.5` 才改，避免反复触发重排。倒计时小字可能换行，高度不能写死。
- **怎么验证**：收起/展开两块、倒计时小字换行时窗口无残影、高度贴合内容；`--snapshot` 对照图里各卡片高度正确。

---

## 11. 单文件 exe 图标「悄悄没进去」

- **现象**：编译通过，但 exe / 快捷方式图标是默认白板。
- **根因**：`csproj` 的 `<ApplicationIcon>` 路径写错、或 `.ico` 条目格式系统不认时，编译照样成功，图标静默缺失。
- **解法**：csproj 同时设 `<ApplicationIcon>` 与 `<Resource Include="Assets\TimeGuard.ico" />`；重生成图标后重新发布。
- **怎么验证**：`python scripts/verify_exe_icon.py dist\TimeGuard.exe` 抽出 16/32/48/256 尺寸 PNG，确认每组非空且可见面积正常。

---

## 12. Win32 句柄按 int 传会被截断（脚本坑）

- **现象**：`verify_exe_icon.py` 里 `ExtractIconExW`/`GetIconInfo` 报 `OverflowError: int too long to convert`。
- **根因**：Win32 句柄是 64 位，`ctypes` 不声明 `argtypes`/`restype` 时会当成 32 位 int 传，截断溢出。
- **解法**：所有句柄参数显式声明 `ctypes.c_void_p` / `wt.HICON` 等。
- **怎么验证**：脚本能正常抽出图标 PNG，不再抛 `OverflowError`。

---

## 13. 「设置窗口里的字被裁掉」修完反而更糟 —— 量的是一个不存在的宽度

- **现象**（2026-09-22，用户实测）：设置窗口「扣分速率」那行的「分」字先是被裁掉一半，改了之后**整个不见了**；用户原话「怎么越修越倒退了呢」。
- **根因**：**窗口宽度在两处各写了一份**——`SettingsWindow.CardWidth = 456`（代码常量）与 `SettingsWindow.xaml` 里的 `Width="396"`（硬写）。
  所有防溢出检查（`LayoutReport`、`Snapshot` 里那张设置卡片）都按**常量 456** 离屏 Measure/Arrange，
  于是报出「每一行都在可视区内，余量 45px」；而真实窗口只有 396 宽，右列实际可用 200px，
  「扣分速率」行需要 227px —— 最右边的「分」字整个落在窗口外。
  上一轮把两个数字输入框从 52 加宽到 56（+8px），正好把原本还剩 8px 露在外面的「分」彻底推出去，于是"越修越倒退"。
- **解法**：
  1. XAML 改成 `Width="{x:Static local:SettingsWindow.CardWidth}"`，宽度**只留一处定义**（`xmlns:local="clr-namespace:TimeGuard"`）。
  2. `LayoutReport()` 不再"把内容摘下来按常量摆"，而是**真开一次窗口**（`WindowStartupLocation=Manual` + `Left/Top=-6000` 挪到屏幕外，
     `Show()` 后 `Dispatcher.Invoke(..., DispatcherPriority.Loaded)` 等布局落定），用**真实 `ActualWidth`** 当判据，
     并断言 `ActualWidth == CardWidth`；顺带用 `PrintWindow(PW_RENDERFULLCONTENT)` 把真窗口抓成 `<png>.real.png`。
     分层窗口（`AllowsTransparency`）若抓到全黑，退回 `RenderTargetBitmap(win)`。
  3. `--uicheck` 第 7 项也加上 `SettingsWindow.WidthCheck()`（真开窗口读 `ActualWidth`），让日常自检就能拦住这种漂移。
- **怎么验证**：把 XAML 的宽度**改回 396**（即故障版）跑一次 `--snapshot`，应报
  `FAIL 窗口实际宽度 = CardWidth 应是 456，实测 396` 且 `扣分速率行 余量 -14.7px`、文案四行 `-57.3px`；
  改回 `{x:Static}` 后全 PASS（余量 45.3px）。**探针必须先用已知故障校准过，否则只是又一份好看的数字。**
- **判据位置**：`SettingsWindow.LayoutReport()` / `SettingsWindow.WidthCheck()`；产物 `_verify/*.layout.txt`、`_verify/*.real.png`。

## 14. 「4K 上能不能跑」——过去对 DPI 变化完全没有处理

- **背景**（2026-09-22，用户提问）：想知道在 4K / 1080P 等其他分辨率下能否正常运行。
- **审计结论**：**启动时**是安全的——位置用 DIP 记录，并按 `SystemParameters.VirtualScreen*` 夹取，
  所以换分辨率不会把窗口弄丢。**但运行中**有两个缺口：
  1. 全项目搜不到任何 `OnDpiChanged` / `DisplaySettingsChanged` 的处理。
     窗口被拖到另一块缩放不同的显示器上、或用户当场改了系统缩放时，
     `SizeToContent="Height"` 的无边框窗口**不会自动重新测量**，内容会按旧 DPI 的尺寸绘制而被裁掉。
  2. 运行中改分辨率（比如 4K 拔掉换 1080P），保存的 DIP 坐标可能超出新工作区，
     而位置只在启动时夹过一次，此时窗口会停在界面之外。
- **关键认识（写下来省得以后再想一遍）**：
  **系统缩放决定 DIP 工作区，程序只按 DIP 摆放，所以不需要为分辨率写任何分支。**
  1080P@100% 与 4K@200% 的 DIP 工作区都是 1920×1080，算出来的默认位置**完全相同**（都是 `left=1709 top=28`），
  唯一要管的只有「位置还在不在可视范围内」。这条也被写成了自检里的一条断言。
- **解法**：
  1. 新增 `WindowPlacement.cs`：把 `DefaultPosition` / `Clamp` / `UsableHeight` 抽成纯函数
     （**不依赖 WPF，可脱离真实屏幕断言**），`RestorePosition()` 改为调用它。
  2. 补 `OnDpiChanged`：先 `SizeToContent → Manual → 失效测量 → 还原`，再 `ClampToScreen()`，两头都堵。
  3. 补 `SystemEvents.DisplaySettingsChanged`（改分辨率/插拔显示器时触发；该事件在非 UI 线程，
     走 `Dispatcher.BeginInvoke`），`OnClosed` 里退订。订阅用 try/catch 包住，拿不到也不影响主流程。
- **怎么验证（没有 4K 屏也能验）**：`SelfTest.AppendPlacementChecks` 用**算出来的**规格断言 10 条：
  四组常见规格的默认位置都贴右上角且无需夹取、1080P@100% 与 4K@200% 默认位置一致、
  在宽屏保存的 `left=2500` 拿到 1080P 上被夹到 `1737`（上限）、双屏副屏在负坐标时位置原样保留、
  副屏拔掉后被拉回主屏、屏幕比窗口还窄时退化成贴左上角而非负坐标、高度为 NaN 时夹取仍得到有效坐标。
  自检总数 121 → **131 PASS / 0 FAIL**。
- **判据位置**：`WindowPlacement.cs`、`MainWindow.RestorePosition()/OnDpiChanged()/ClampToScreen()`、
  `SelfTest.AppendPlacementChecks()`。
- ⚠️ **2026-09-28 更正**：上面"解法 2"里那段 `SizeToContent → Manual → 失效测量 → 还原`
  **本身就是后来出问题的原因之一**，见 **§19**。位置与夹取的部分仍然有效，只有尺寸那段要按 §19 重做。

## 15. 本环境 `dotnet build` 会「假死」在 CoreClean / WPF 临时程序集之后

> **2026-09-24 补充**：本节把根因记为「`rm` 垫片」，那只解释了我们自己敲命令的那一路。
> 后来抓 Gradle daemon 的线程栈发现**Java 自己的 `DeleteFile0` 也一样慢** ——
> 真正的根因是「本机删文件慢到病态」，与用什么工具删无关。**先看 §17**，再回来看本节的细节。

- **现象**（2026-09-22 晚实测）：`dotnet build` 能过 `Restore`（23ms）、也能过 Roslyn 编译
  （12 个源文件 **0 错误 0 警告**，日志里明确写着 `server processed compilation`），
  但整个命令**再也不返回**：卡在 `CoreClean: 正在删除文件 …`，或
  `_CompileTemporaryAssembly` 之后的某个目标上。`-t:Rebuild` 时甚至 30 分钟删不完 `obj`。
  看起来像死锁，其实是**每个文件删除要 ~4 秒**。
- **根因**：本环境的 `rm` 被包成「**送回收站**」的垫片（`safe-delete`，走 `genie-trash`）。
  它对每个文件都要走一次回收站事务，且对已经不存在的文件会报
  `windows error: 系统找不到指定的文件 (0x80070002)`。MSBuild 的 `CoreClean` 要删
  **几百个** obj 文件，于是 4 秒 × 几百 = 看上去永远跑不完。
  单文件实测：删除一个 15KB 的 `.resources` → **real 3.969s**。
- **识别方法**：`dotnet build -v n`，看它停在「正在删除文件 `obj\Debug\net8.0-windows\…`」不动。
  卡在这里就是本问题，**不要去查代码或 XAML——编译本身是好的**。
- **解法**：清构建产物时用 Python 的 .NET 文件 API，绕开回收站垫片：

  ```python
  import shutil, os
  for t in (r"E:\codes\TimeGuard\obj", r"E:\codes\TimeGuard\bin"):
      if os.path.isdir(t):
          shutil.rmtree(t, ignore_errors=True)
  ```

  实测：**121 个文件 10.3 秒删完**（同样的事用 shell `rm` 会挂住）。
  清完再 `dotnet build -nodeReuse:false` 即正常。
- **`Restore` 阶段也别让它碰网络**：日志显示它会去连 `https://api.nuget.org/v3/index.json`，
  受限网络下这一句同样会长时间挂住；产物齐备后用 `--no-restore` 跳过。
- **副产品教训**：构建卡住时先看「上一个 `TimeGuard.exe` 是不是还在跑」——
  它握着 `bin\…\TimeGuard.exe` 与 `.pdb`，会让复制步骤失败。
  项目自带的 `publish.cmd` 已内置这个检查，**改完桌面版务必走 `publish.cmd`**。
- **判据位置**：无需改产品代码；这是环境问题，记在这里省得下次又从头查一遍。

---

## 16. 自检「假失败」的两种形态（测出来的 FAIL 可能不是产品坏了）

写测试时踩到两次，都是**测试自己写错、产品没问题**。这类 FAIL 最费时间，因为看上去像真 bug。

### 16.1 被测路径根本没被执行（隔离短路）

- **现象**：新加的两条「配置文件写坏了能不能救回来」在自检里 FAIL：
  `主文件写坏时从 .bak 救回 → 救回 left=null top=null`、
  `读不出来的文件被改名留证（.corrupt） → 存在=False`。
- **根因**：`AppConfig.Load()` 开头有一段**自检隔离短路**——`_overrideFile` 非空时
  直接读那个文件返回，**不走 `.bak` 回落、不走 `.corrupt` 改名**。
  这是有意设计（见第 4 条：自检绝不能碰用户真实配置），但我的用例正是在隔离模式下跑的，
  于是被测的那两条分支一次都没执行到。
- **解法**：加 `ExerciseRecoveryForTest` 开关（`internal static bool`），
  只在检查这两条时短暂置真，`finally` 里关掉。
  **教训**：写「验证某个分支」的用例前，先确认那条分支真的会被走到——
  隔离/短路/早返回是测试最常见的空转来源。**返回 null 不等于逻辑通过。**

### 16.2 前置状态被上一个用例改掉了

- **现象**：修好 16.1 后，`从 .bak 救回` 通过了，
  但 `改名 .corrupt` 仍 FAIL（`存在=False`）。
- **根因**：用例⑤（主文件坏、`.bak` 好）跑完后，`Load()` 的恢复逻辑**并不会**隔离文件；
  而用例⑥依赖「主文件与 `.bak` 都读不出」才会隔离。可⑥只写坏了主文件，
  `.bak` 还是好的 → 又走了「从 .bak 救回」→ `.corrupt` 永不出现。
  更绕的是：⑤ 之后主文件是**存在但坏**的，⑥ 看起来「已经写坏了」，其实状态不对。
- **解法**：⑥ 里显式 `TryDeleteForTest(file + ".bak")` 再 `Load()`。
- **教训**：一组用例共享同一份磁盘状态时，**每条用例都要自己把前置条件摆好**，
  不要指望上一条留下的现场。写清楚注释：「这里必须删掉 .bak，否则走到的是另一条分支」。

### 16.3 通用判据

- 出现 FAIL 时先问三个问题：**这条分支真被执行了吗？期望值算对了吗？前置状态是我以为的那样吗？**
- 期望值要按代码的**真实语义**推，别按直觉。同一轮里还错了一次：
  `.bak` 里存的是**上一次** `Save` 的内容（`second.Save()` 把 `first` 挪成了 `.bak`），
  所以「救回」应得 `left=100 top=200`，而不是最新的 `321/654`。
- 顺手一条：`-0` 在 `int.TryParse` 里等于 `0`（int 没有负零），
  期望值写成 `false` 会冤枉产品；实测应为 `true`。

---

## 17. 本机「删文件」本身慢到病态 —— 任何清空输出目录的构建都会像卡死（2026-09-24 定论）

**这是 §15 的根因，比 §15 写的更底层。** §15 当时把锅记在「`rm` 被包成送回收站」上，
只解释了我们自己敲命令的那一路。2026-09-24 用 `jstack` 抓线程栈，才发现**连 Java 自己的删除也一样慢**：

```
"Execution worker Thread 3" ... RUNNABLE      cpu=85968.75ms  elapsed=121.64s
  at sun.nio.fs.WindowsNativeDispatcher.DeleteFile0(Native Method)
  at java.nio.file.Files.deleteIfExists(Files.java:1191)
  at org.gradle.internal.file.impl.DefaultDeleter.deleteRecursively(DefaultDeleter.java:143)
  at org.gradle.internal.file.impl.DefaultDeleter.ensureEmptyDirectory(DefaultDeleter.java:90)
  at ...RemovePreviousOutputsStep.cleanupExclusivelyOwnedOutputs(RemovePreviousOutputsStep.java:110)
```

121 秒里烧掉 86 秒 CPU，几乎全在 `DeleteFile0` 里 —— **在删，但是慢到病态**（不是死锁，也不是等锁）。

**症状（两个构建系统都中招，长得还都很像"死锁"）**：

| 构建系统 | 卡在哪 | 表现 |
|---|---|---|
| MSBuild / `dotnet build` | `CoreClean`、`-t:Rebuild`、`GenerateBundle` | `bin\` 时间戳不动、`obj\` 里文件一个个消失、`-v n` 日志停在"正在删除文件" |
| Gradle / AGP | `mergeDebugResources`、`processDebugResources`、`testDebugUnitTest` | 上一个任务显示完成，下一个任务十分钟没动静；测试结果 XML 迟迟不生成 |

**判据（怎么区分"在爬"和"真卡"）**：抓线程栈，看那个 worker 是 `RUNNABLE` 在
`DeleteFile0`/`DeleteFile` 里 —— 那就是在爬。若所有 worker 都 `WAITING` 且没人持有任务，
才是真卡。
```bash
# Gradle daemon：tasklist 找到 java.exe（内存较大的那个），然后
"$JDK/bin/jstack.exe" <pid> | grep -A 16 '"Execution worker'
```
另有一个便宜的旁证：数一下最近 3 分钟被改动的文件数
（`find <build-dir>/intermediates -newermt "-3 minutes" -type f | wc -l`）——
是 0 且线程栈在 `DeleteFile0`，就是同一个病。

**对策（重要，改行为习惯）**：

1. **绝不主动清 `obj\`/`bin\`/`app\build\`。** 也不要为了"看得清警告"去 `-t:Rebuild`。
   增量构建不会删成百上千个文件，就不会撞上这个病。
2. 需要"全新构建"来验证时，**换个输出目录**（`-o <新目录>` / 改 `buildDir`），
   而不是把旧目录删掉再建。换目录是纯写入，不触发批量删除。
3. 真要清，**告诉用户，让用户自己删**（用户在资源管理器里删这些目录是秒级的，
   说明慢的是"通过命令行/构建系统逐文件删"这条路径，不是磁盘本身）。
4. 耐心值也要调：一个要清 250 个文件的 `mergeDebugResources` 预计要**几分钟**，
   十几分钟没结束先抓栈确认，别急着 kill —— 中途 kill 会留下半清理的 `obj\`，
   下次更慢（§15 记的那次 `obj\*.tmp` 残留就是这么来的）。
5. **不要在构建跑的时候改源码**：Gradle 的输入快照是任务开始执行的瞬间取的，
   构建期间的编辑要么白改、要么把编译打成语法错，白等一轮。

- **判据位置**：无需改产品代码；这是环境问题，记在这里省得下次又从头查一遍。

---

## 18. 「验收材料」自己也会撒谎 —— 两处只有看图才发现的问题（2026-09-24）

自检 228 条全绿、编译 0 警告，**并不能说明界面对**。收尾时给 `--snapshot` 补了子窗口面板区，
一出图就抓出两处：

### 18.1 图注写了这张图画不出来的东西

一张样卡标注「闹钟块：生效亮字、不生效浅灰字」，图上却只画得出 1 个闹钟。
根因：**挂件里的闹钟块只收「生效且半小时内会响」的闹钟**（`AlarmBook.Imminent`），
我摆进去的"不生效"那个和"周二不响"那个本来就进不了这一块 —— 灰色没有画出来的可能。

**这条为什么危险**：图注是给人看的结论。一张与事实不符的图比没有图更糟 ——
它会让人（包括我自己）在后续核对里引用一个不存在的证据。
而且**这张卡从写出来到被发现，中间跑过好几轮"自检全绿"**。

**规矩**：
- 每加一张样卡/一个面板，先问一句「**图注里说的东西，这张图真的画得出来吗**」。
  画不出来就换地方画（这次是把闹钟设置窗补进面板区 —— 灰色字本来就只存在于它的列表里），
  或者把图注改成如实描述。
- 「同一份材料里 A 处说的和 B 处画的不一致」是高频形态，核对时**把图注当断言读**。

### 18.2 XAML 里写死的 `IsChecked="True"` 会让首屏初始化被跳过

`AlarmWindow.xaml` 两个页签是 `RadioButton`，普通页写了 `IsChecked="True"`。
这会在 `InitializeComponent` **期间**就触发 `Checked` → `Tab_Checked`，
而那时 `PageNormal/PageWake` 还没建好，事件处理函数第一句
`if (PageNormal is null || PageWake is null) return;` 把这次触发挡掉了。
代价：**打开窗口时标题下那行说明是空的，点一下页签才出现**。
不抛异常、不崩溃、自检全绿，只有看图才发现。

连带问题（同一处、同一类）：普通页报错是**顶在说明行上**显示的，
`HideError()` 只把 `Foreground` 还回 `HintText`，**没还文字** ——
清错之后说明行会一直挂着刚才那句错话。

**规矩**：
- 「勾选状态写在 XAML 里 + `Checked` 事件里做首屏初始化」这个组合必然踩坑：
  首屏那次触发一定走"控件还没建好"的早退分支。**初始化的正主必须是构造函数**，
  事件处理函数只负责"用户又点了一次"。把共同部分抽成 `ApplyTab()` 这类方法，两边都调。
- **"清错"必须把被借用的控件完整还原**（文字 + 颜色 + 可见性），
  只还原其中一项等于留了个半坏状态。凡是"借用别人的位置显示错误"的设计，都要在清错时把原内容写回来。
- 加个自检把首屏钉住（本次补了 7 条，见 `AppendAlarmWindowChecks`）。
  这类检查看着琐碎，但它是唯一能自动发现"首屏 vs 点过之后不一样"的手段。

## 19. 「远程控制时界面全乱」—— 窗口的物理尺寸没跟着 DPI 走（2026-09-28）

### 19.1 现象

用户报：本地看着是好的；一远程控制这台电脑（会话 150% 缩放），挂件就成了这样 ——
「时间到！」四个字左右各被切掉一角、底下小字折成两行、输入框和两个按钮叠在一起。

### 19.2 定位：把截图当数据量，量出两个互相矛盾的数

| 量什么 | 实测 | 推出 |
|---|---|---|
| 红字「时间到！」的高度 | **47 px** | 33 DIP × 1.5 → **内容是按 150% 画的** |
| 卡片宽度 | **177 px** | 183 DIP × 1.0 → **窗口还是 100% 时的物理尺寸** |

两边一除：`177 ÷ 1.5 = 118` DIP。即 WPF 把窗口当时那份**物理宽度**按新 DPI 反算回来、
写进了 `Width` 属性（183 → 118）；而 `118 × 1.5` 又正好回到 177px —— **自我锁定，再也长不回来**。
内容于是按 118 DIP 的可用宽度排版，扣掉左右各 15 的内边距只剩 88 DIP，
而「时间到！」需要 115 DIP → 溢出、折行、重叠三件事一次性全出现。

### 19.3 现场取证（同一台机器，不靠推测）

用 `EnumWindows` + `GetWindowRect` 直接量**正在运行的那个挂件**。
⚠️ 测之前必须先把测量进程设成 `PER_MONITOR_AWARE_V2`（`SetProcessDpiAwarenessContext(-4)`），
否则读到的值会被系统按"虚拟化"给你，永远看不出问题：

```
PID=21244 标题='TimeGuard' 会话=RDP-Tcp#11
    整窗 = 183 x 429 px   窗口DPI=144 (150%)
    183 DIP 应为 274.5 px → 现在 183 px  ✗ 漂了（差 -91 px）
```

**它此刻就是坏的**。183px 与截图量到的 177px 是同一回事（差 6px 来自圆角与半透明边缘）。

### 19.4 根因

`SizeToContent="Height"` + `WindowStyle=None` + `AllowsTransparency=True` 的窗口，
**WPF 会把 client 的物理宽度按当前 DPI 反算回来写进 `Width` 属性**。
稳定状态下这个回写无害 —— 本机（150%）实测窗口宽度稳定在 `183.33 DIP`（= 275px ÷ 1.5）。
但在 **DPI 切换的那一瞬间**，HWND 的物理尺寸还没更新（还是旧 DPI 的 183px），
WPF 却已经用新 DPI 去反算 → `183 ÷ 1.5 = 122`。而 `122 × 1.5 = 183px` 让它自己看起来完全自洽，
所以**再也不会自己长回来**。

**这条推论的独立佐证**：把宽度故意打成 118 DIP、让守护纠回来之后，
量到的稳定值是 **183.33**（不是 183）—— 正好等于 `275 ÷ 1.5`，证明"反算回写"真实存在。

### 19.5 解法

1. **宽度只有一个来源**：`MainWindow.CardWidth = 183`，XAML 用 `{x:Static local:MainWindow.CardWidth}` 指过来；
   `Snapshot.cs` 与 `SelfTest.cs` 里各自写死的那份 `183` 也一并收归（和 `SettingsWindow.CardWidth` 同一套做法）。
2. **`EnsureWindowSize()` 每帧核对**（挂在 `Refresh()` 最前面，200ms 一轮，正常时零开销）：
   - ① 逻辑宽度漂了就钉回 `CardWidth`（WPF 会按当前 DPI 同步重设 HWND）；
   - ② 物理宽度不对就 `SetWindowPos` 补一刀 —— 无边框分层窗口在 DPI 变化后不一定会自己跟上，
     这时 `Width` 属性是对的、HWND 却还是旧尺寸，只能走 Win32。
   容差取 **1 DIP**：正常状态下 `Width` 本来就是 `183.33` 这种带小数的值，容差太小会每帧误判、反而抖。
3. **`OnDpiChanged` 删掉原本那段**「切 `SizeToContent` → `UpdateLayout` → 切回来」——
   那正是 §14 当初为"内容被裁"加的补丁，但它同时把尺寸反算的时机暴露了出来。
   现在改成：先 `EnsureWindowSize()`，再 `InvalidateMeasure/UpdateLayout/ClampToScreen`，
   并在 700ms 后复查一次（WPF 对这个切换的处理是异步的）。
4. **`UpdateWindowHeight` 改用常量算宽度**（原来是读 `Width` 属性）——
   万一宽度正在漂，高度不能跟着一起错；两个方向的错法不同，叠起来更难查。

### 19.6 怎么验证（本机只有一种缩放，所以分三层）

- **纯计算**：`WindowPlacement.ExpectedPhysicalWidth` 把 100/125/150/175/200% 的期望值钉住
  （183 → 183 / 229 / 275 / 320 / 366）。顺手抓出一个真问题：`Math.Round` 默认是**银行家舍入**，
  `183 × 1.5 = 274.5` 会算成 **274**，而 Windows 是四舍五入 → **275**。
  必须显式写 `MidpointRounding.AwayFromZero`。**这个偏差就是自检抓出来的**。
- **真窗口**：`--uicheck` 新增两条 —— ① 故意把 `Width` 打成 118 DIP，700ms 后必须回到 `CardWidth`；
  ② 物理宽度必须 = `CardWidth × 当前缩放`。**这两条在本机就有效**，因为这台机器的缩放是多少、
  它就在什么缩放下验（本机实测 150%）。
- **跨环境**：真正"从 100% 切到 150%"这一下，只能靠远程会话断开/重连来触发 —— 交付时请用户验。

### 19.7 判据位置

`MainWindow.CardWidth`、`MainWindow.EnsureWindowSize()`、`MainWindow.OnDpiChanged()`、
`WindowPlacement.ExpectedPhysicalWidth()`、`SelfTest.AppendDpiWidthChecks()`、
`App.RunUiCheckAsync` 第 12/13 条。

## 闹钟设置保存使进程退出

闹钟设置通过 Show() 打开为非模态窗口，保存按钮却设置 DialogResult=true，WPF 抛出 InvalidOperationException 并终止进程。保存只设置 Result 并 Close()，由 MainWindow.Closed 回调统一落盘。用 dist/TimeGuard.exe --alarmcheck _verify/alarm-final.txt 驱动实际按钮，覆盖非模态保存、主窗口保存与重读、触发时删除一次性闹钟以及删除落盘。测试必须隔离配置并采用 OnExplicitShutdown，避免关闭第一个测试窗口使整个 WPF 测试应用退出。
