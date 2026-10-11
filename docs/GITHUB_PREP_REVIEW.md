# GitHub 发布前审查与目录建议

> 2026-10-12 整改更新：本报告下面的 7 项发现已实施修复；初次审查内容作为历史证据保留。当前整改与验证结果见 [整改记录](GITHUB_FIXES.md)。不能把下面的“建议修复”解读为当前尚未修改。

> 仓库现名：`h3y12/Xike`（息刻）。文中 TimeGuard 是初次审查时的目录与内部工程名称。

范围：当前 Windows 桌面版和 Android 版源码、构建入口、配置存储、闹钟与悬浮窗关键路径，以及首次上传应排除的文件。本轮是代码与发布准备审查，不是完整渗透测试，也没有完成真机、整夜锁屏和混合 DPI 多屏验收。现有 `代码审查整改说明.md` 是历史整改记录，本报告按当前实现重新核对。

结论：建议一个 GitHub 仓库保留两个独立平台子项目，分别发布版本。当前源码可以进入审查阶段，但建议修复下列功能问题并补齐构建入口后再发布正式安装包。编译成功和现有测试通过，不能替代未覆盖场景的验证。

## 1. 两个文件夹怎样组织

建议结构：

```text
TimeGuard/
├── README.md                 产品介绍、两个平台的入口和下载链接
├── LICENSE                   作者选定的许可证
├── .gitignore
├── .gitattributes
├── .github/workflows/        两个平台的独立构建和检查
├── desktop/                  现有 TimeGuard 项目内容
│   ├── TimeGuard.csproj
│   ├── src/
│   ├── Assets/
│   ├── scripts/
│   └── docs/
└── android/                  现有 TimeGuardAndroid 项目内容
    ├── app/
    ├── gradle/wrapper/
    ├── gradlew
    ├── gradlew.bat
    └── scripts/
```

合并的是仓库管理和产品入口。C#/WPF 与 Kotlin/Android 的构建工程仍然独立；不需要把两种语言塞进同一个 `src/`，也不需要现在引入跨语言共享代码层。计分、倒计时、闹钟规则可用相同输入/期望结果的测试样例约束一致性。

这两个目录当前都没有 Git 仓库元数据，因此没有需要迁移的本地 Git 提交历史。迁移时要更新 README 互链和文档中的旧目录引用。使用 `desktop/` 作为桌面工程根目录，也能保持 MSBuild 默认源码扫描范围限于桌面子项目。

如果将来两个平台交给不同团队维护、发布权限不同或产品方向分开，再拆成两个仓库更合理；当前规模没有必须拆开的理由。

## 2. 源码和发行包怎样放

在同一个 GitHub 项目中管理，采用不同存储入口：

| 内容 | 位置 |
|---|---|
| C#、Kotlin、XAML/XML、测试、构建脚本、项目配置 | Git 仓库 |
| 构建必需的图标、矢量图、内置音频、文档配图 | Git 仓库 |
| 正式 EXE/ZIP、APK、发布说明、校验值 | GitHub Releases |
| `bin/`、`obj/`、`app/build/`、Gradle 缓存、SDK/JDK 下载 | 本地，忽略 |
| 签名私钥、`keystore.properties`、个人 SDK 路径、运行配置、诊断日志和临时截图 | 本地私有内容，忽略 |

本地 `dist/` 可以继续作为构建输出目录，安装包本体不提交 Git。GitHub Releases 支持附带二进制安装包和发布说明：[GitHub 官方说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。

建议分别使用 `desktop-v2.0.1`、`android-v1.2.6` 这种平台前缀标签，随后按各平台实际修复版本递增。标签关联实际用于构建的提交，不要求两个平台使用相同版本号。桌面版目前是依赖 .NET 8 桌面运行时的单文件包，发行说明需保留这个要求。

## 3. 功能与工程发现

以下均为 P2：有明确触发条件、需要修复的正确性或工程问题。复现程序只使用工程中的闹钟纯逻辑，未操作真实用户配置。

### F1. 桌面版长暂停后补响旧闹钟，跨天还可能漏响当前闹钟

位置：`src/AlarmBook.cs:189–220`；调用链 `src/MainWindow.xaml.cs:700–725`。

`DueBetween(from, to)` 用 `from - MissGraceSeconds` 判断是否过期，而 `from` 是睡眠前的上一次刷新时间。电脑从 07:59:59 睡眠到 09:00，08:00 的闹钟仍被认为有效。另一个问题是仅取搜索起点后的第一条候选；跨天长暂停时，它可能命中昨日已响过的候选后直接跳过，漏掉今天正好到期的那一条。

已运行实际源码复现：

```text
LONG_SLEEP: 08:00 alarm checked at 09:00 => firings=1 (expected 0)
NEXT_DAY_AFTER_SLEEP: firings=0 (expected 1)
```

建议按当前检查时间 `to` 限定实际补响宽限范围，并确保检索当前有效发生时刻。增加长睡眠、跨天恢复、时钟回拨后的场景。Android 当前实际响铃入口是 `AlarmRingService.acceptDelivery`，不能把这个桌面运行问题直接等同于 Android 系统调度问题；Android 纯逻辑中也有同类辅助算法，需要维护一致性。

### F2. 保存桌面闹钟编辑草稿会覆盖期间发生的实际状态变化

位置：`src/AlarmWindow.xaml.cs:42–48`；`src/MainWindow.xaml.cs:1445–1450`。

设置窗打开时复制闹钟数据，主窗口在保存时用 `_alarms = edited` 整体替换当前集合。设置窗是非模态的，期间主窗口仍可消费一次性闹钟、增加稍后提醒、改变开关。保存旧草稿会覆盖这些变化。草稿也没有当前集合的 `_firedAt` 去重状态，保存后宽限窗口内的重复闹钟可以立即再响。

已对实际 `AlarmBook` 复现草稿替换调用序列，并核对 UI 保存链路：

```text
LIVE_DELIVERY: firings=1
SAVE_UNCHANGED_DRAFT: firings=1 (expected 0)
ONE_SHOT_CONSUMED: count=0
SAVE_OLD_DRAFT: count=1 (expected 0)
```

建议按稳定 ID 合并用户真正修改的字段，保留运行时去重状态；对编辑期间已消费或删除的记录处理冲突，避免旧快照恢复闹钟。补充编辑窗打开期间响铃、稍后提醒和保存的集成测试。本轮复现了模型调用序列，没有做真实鼠标 UI 验收。

### F3. Android 穿透模式的窗口透明度仍可能阻断下层点击

位置：`OverlayService.kt:330–358`；`core/AppConfig.kt:58–60`。

默认 `LayoutParams.alpha` 为 0.88，开启穿透只设置 `FLAG_NOT_TOUCHABLE` 并降低根 View 的 alpha。Android 12 及以上对不受信任悬浮窗的透传点击检查的是 **WindowManager.LayoutParams.alpha**，通常要求不超过 0.8；降低根 View 的 alpha 不满足该条件。因此外观变淡、悬浮窗自身不接收触摸，但下层其他应用仍可能收不到点击。该结论来自代码和平台契约核对，尚未真机复现。

建议穿透时读取 `InputManager.getMaximumObscuringOpacityForTouch()` 并限制窗口 alpha，在创建、切换穿透和刷新外观时统一应用；用 Android 12+ 真机在其他应用上验证点击，不只在本应用内测试。

依据：[Android LayoutParams 官方文档](https://developer.android.com/reference/android/view/WindowManager.LayoutParams#FLAG_NOT_TOUCHABLE)、[Android 12 触摸行为变更](https://developer.android.com/about/versions/12/behavior-changes-all#untrusted-touch-events)。

### F4. 桌面自定义音频失败时，系统音回退会被自己的条件拦住

位置：`src/AlarmSound.cs:59–64`、`:96–109`。

存在但不能解码的音频触发 `MediaFailed` 后调用 `FallbackToSystem()`，没有释放并清空 `_player`。随后 `PingSystemSound()` 发现原文件存在且 `_player != null` 就返回。这样既没有自定义音频，也没有回退的系统音；后续定时重试也受同一条件阻挡。同步 `Open/Play` 失败且播放器已分配时也存在相同风险。

建议先停止和释放失败播放器，并显式记录当前音源模式；仅在自定义播放确实有效时跳过系统音。添加“文件存在但内容不是有效音频”的回退测试。本轮为静态控制流核对，未播放真实音频。

### F5. 桌面配置主文件缺失时不会恢复仍然有效的备份

位置：`src/AppConfig.cs:332–351`、`:397–405`。

备份恢复只在主文件存在且解析失败时执行。保存回退分支先 `File.Move(p, bak)`，再 `File.Move(tmp, p)`；若两步之间进程退出或第二步失败，主文件不存在但备份完整。下次启动会跳过备份，读取旧品牌配置或默认值。后续保存还可能用默认配置覆盖旧备份。

建议主文件缺失也尝试 `.bak`，缩小非原子回退路径，并在第二步失败时恢复主文件。加入“主文件不存在，备份有效”以及写入中断的恢复测试。本轮为静态路径核对，没有注入磁盘故障。

### F6. Android 公共构建入口依赖作者机器，缺少 Gradle Wrapper

位置：`scripts/build-android.ps1:4–10`；Android 工程根目录。

构建脚本覆盖调用者的 `JAVA_HOME`/SDK 路径，固定使用特定 `C:\Program Files` 安装目录；当前目录没有 `gradlew`、`gradlew.bat` 和 `gradle/wrapper`。别人克隆源码后无法按统一入口直接构建，作者机器路径迁移后也会失败，本机已实际遇到缺失 JDK 路径的错误。

建议提交固定版本的 Gradle Wrapper，尊重环境变量和 `local.properties`，提供无发布私钥也能执行的 debug 构建与测试命令。正式签名发布与普通开发构建使用明确的不同入口。私钥不进入公开仓库。

### F7. 桌面自检发现 FAIL 时进程仍以成功退出

位置：`src/App.xaml.cs:38–42`、`:26–33`；`src/SelfTest.cs:44–57`。

`SelfTest.Write` 和闹钟检查把 FAIL 写入文件；启动入口随后无条件调用默认成功的 `Shutdown()`。以后 CI 若仅检查进程退出码，会把未通过的自检当作成功。

建议检查方法返回通过状态，并用 `Shutdown(1)` 或等效失败退出码传播结果；CI 同时保留原始报告作为构建产物。本轮为静态调用链核对，没有修改产品断言以制造失败。

## 4. 上传内容与文档准备

- 两个平台已存在 `.gitignore`，Android 的私钥目录、口令文件和 SDK 路径在排除名单中。文本源码搜索没有发现仍在源码中硬编码的签名口令；这不代表签名目录可以上传。首次暂存后必须检查实际文件名单，采用 Git 发布，避免把整个本地目录压缩上传。
- 仓库根目录应补许可证，由作者确定授权方式。本轮没有替作者选定许可证。公开仓库的可查看/可 fork 与明确授予其他使用权不是同一件事，参考 [GitHub 许可证说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)。
- 清理公开说明中的作者专用路径、旧机器 SDK 版本描述和临时诊断步骤；把个人环境记录与面向贡献者的构建指南分开。
- 修正过时注释，例如 Android `SleepRules.minutesPerPoint` 仍说“亮屏累计”，实际规则已是时钟时间；`ensureServiceForCd` 的注释与实际权限拦截行为不一致。
- 建议新增两个平台独立的 CI 构建、测试与静态检查，修复 F7 后再把桌面自检作为发布门禁。无需为了目录整合重新设计计分引擎。
- APK 沿用现有发布签名；私钥与口令独立备份，正式构建使用受控的本机或 CI 秘密配置。桌面发布说明保留 .NET 8 桌面运行时要求。

## 5. 本轮验证与边界

- Windows 当前源码：Release 构建成功，0 警告、0 错误。
- Windows 闹钟：直接编译实际源码，运行隔离复现程序，确认 F1、F2 的异常输出。
- Android：重新运行编译产物中的 130 项纯逻辑 JUnit 测试，全部通过；它们没有覆盖 F3 的系统输入分发行为。
- Android 最新安装包的构建、签名一致性与 `lintRelease` 已在上轮悬浮窗修复中验证通过。本轮没有再次跑完整 Android 服务集成测试；该套测试在上轮被 Robolectric 依赖访问/下载阻塞。
- 没有完成真实音频故障、磁盘故障、真机悬浮窗穿透、整夜后台和混合 DPI 多屏验证。不以此报告承诺不存在其他缺陷。

本轮复现程序和原始结果位于 Android 工程的 `_verify/github-review/`，属于 `.gitignore` 排除的本地审查证据。报告给出的修复建议尚未实施。

建议顺序：修复 F1–F5 并补回归 → 补 F6/F7 与公共构建说明 → 整理单仓库目录 → 选定许可证 → 检查首次暂存名单 → 提交源码 → 为各平台添加标签和 Releases 安装包。
