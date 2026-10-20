# 息刻（Xike） · 安卓版

当前版本 1.2.12（versionCode 15）。铃声显式绑定系统闹钟通道，兼容仅声明音频用途时的音量映射问题；主页和响铃界面的音量键调整闹钟音量。保留连锁次数、间隔编辑及实际版本显示。

## 从源码构建

推荐安装 JDK 21（Robolectric 服务测试固定使用 JDK 21；应用编译目标仍是 Java 17）、Android SDK（平台 35、Build Tools 34.0.0），设置 `JAVA_HOME` 和 `ANDROID_HOME`，或通过 Android Studio 配置本机 `local.properties`。无需全局安装 Gradle；仓库自带 Gradle 8.9 Wrapper，并校验发行包 SHA-256。

```powershell
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest
# 构建脚本：debug 包无需作者的签名私钥
.\scripts\build-android.ps1 -Debug
```

macOS / Linux 使用 `./gradlew`。正式签名从本地 `keystore.properties` 或 `TIMEGUARD_*` 环境变量读取；缺少签名信息仍可生成未签名 release 包。私钥、口令与 SDK 路径不要提交。

熬夜健康分悬浮窗的手机版。和电脑版 [TimeGuard](../desktop) **同一套规则、同一套口径**，
三块内容也一一对应。

---

## 三块内容

| 块 | 是什么 | 依据 |
|---|---|---|
| 早睡健康分 | 100 分起步，到点慢慢扣 | **时间戳**：从「开始扣分」那一刻起，时间走一个步长就扣一次 |
| 手动倒计时 | 自己设的计时器，**与分数无关** | 到某个钟点，或从此刻起 N 分钟 |
| 闹钟 | 普通闹钟 + 防赖床的连锁起床闹钟 | 各自的**固定钟点**（不读系统时钟，可脱离框架单测） |

「晚睡扣分」按时间算，不是按「你玩了多久手机」算：

```
23:30 开始扣分，每 3 分钟扣 1 分
23:33  →  距 23:30 过了 3 分钟  →  扣 1 分  →  99
00:06  →  距 23:30 过了 36 分钟 →  扣 12 分 →  88
```

手机放在床头没碰，时间照样在走，分照样扣 —— 判定「晚睡」的依据是**时间**，不是屏幕亮没亮。

> **这条曾经写错过。** 手机版第一版自作主张改成了「只有亮屏才扣分」，后果是手机往床头一放
> 睡一整夜、分数纹丝不动，等于白装。现在口径与电脑版逐字相同，并有单元测试钉住
> （`ScoreEngineTest.untouchedPhoneStillLosesPoints`）。**不要再改回「亮屏累计」。**

---

## 和电脑版的差别

| | 电脑版 | 安卓版 |
|---|---|---|
| 计分规则 | 相同 | 相同 |
| 倒计时 | `23:00` 到点 / `45m` 时长 | 相同写法，相同状态机 |
| 闹钟 | 相同规则（`Alarm.cs`/`AlarmBook.cs` ↔ `Alarm.kt`） | 相同规则，**单测钉住两端一致** |
| 闹钟设置在哪 | 独立窗口（挂件右键 →「闹钟设置…」） | App 里的「闹钟」卡片 |
| 闹钟怎么提醒 | 临近半小时并入挂件 + 响铃弹窗 | 临近半小时写进**通知栏** + 响铃高优先级通知 |
| 载体 | 桌面无边框窗口 | 系统悬浮窗（`TYPE_APPLICATION_OVERLAY`） |
| 穿透 | `WS_EX_TRANSPARENT`，托盘菜单切 | `FLAG_NOT_TOUCHABLE`，**App 内**切（通知栏可选） |
| 后台存活 | 普通进程 | 前台服务（Android 要求必须挂通知） |

---

## 闹钟怎么用（手机端）

- App 里「闹钟」卡片就是设置页。**每个闹钟一个方块：左边时间、右边开关**；
  生效的近黑色，不生效的浅灰色；列表按时间排，**以凌晨四点为界**（所以 `01:00` 在 `23:00` 后面）。
- **加一个闹钟**：选时间 + 名字（可不填）+ 重复方式（仅一次 / 每天 / 每周勾星期几）。
  「仅一次」可以勾「响过之后自动删除」。
- **起床闹钟（连锁）**：填**起始时间 + 间隔 + 次数**，一次生成一串。
  也可以一个个手动加 —— 两条路都留着。
- **响铃时停止的语义**：停止当前响铃，后面的**仍按各自的时刻**响（8:00 关掉、8:03 照响，
  不会因为你在 8:02 才点而漂到 8:05）；在 App 闹钟列表关闭任意一个连锁成员，整组一起关闭；重新开启任意一个，整组一起开启。编辑窗口点击“删除整组”时，同组成员全部删除，普通闹钟和其他连锁组不受影响。
- 闹钟由系统独立调度，关闭悬浮窗、退出设置页面不影响到点响铃。Android 12+ 需要开启「闹钟和提醒」，Android 13+ 需要允许通知以显示操作按钮。
- 到点才启动独立响铃服务，循环播放铃声与震动；通知和 App 闹钟卡片都提供「停止响铃 / 稍后提醒」。一次性触发时立即删除，选择保留时自动关闭。

> **两端规则必须一致。** 判据在 `AlarmTest`（安卓）与 `SelfTest.cs` 的「闹钟」段（电脑）里是同一批：
> 四点分界排序、下次响铃时刻、连锁咬合不漂移、响过就删、错过不补、跨天重复。
> 改规则时**两边都要改**，并且两端这几个用例都要继续成立。

---

## 默认口径

```
20:30   悬浮窗出现，100 分
23:30   开始扣分：每 3 分钟扣 1 分
04:30   扣光
06:30   悬浮窗隐藏，直到次日 20:30
```

全部可在 App 里改：三个时刻、扣分速率、四档状态文案、透明度、显不显示分数 / 倒计时。

---

## 手动倒计时怎么填

| 填法 | 意思 |
|---|---|
| `23:00` | 走到那个钟点（今天已过就顺延到明天 23:00） |
| `23:00:30` | 精确到秒 |
| `45m` / `45分钟` | 45 分钟后 |
| `2h` / `1小时` | 2 小时后 |
| `45` | 大于 23 的纯数字 = 45 分钟 |
| `23` | 0–23 的纯数字 = 到 23:00 |

三颗按钮：**开始/重来**、**暂停/继续**、**重置**。

**「到钟点」不允许暂停**，按钮会是灰的，说明文字里也会写「不可暂停」——
它的截止点就是那个钟点本身，冻结剩余量再继续只会把 23:30 变成 23:30:10。
要停这种倒计时就用「重置」。只有「N 分钟后」那种时长计时才能暂停。

剩余时间一律用**绝对截止时刻**现算（`截止 − 现在`），所以锁屏、App 被杀、手机重启
都不会让它「停住」：重新亮屏时就按真实时间接着算。

---

## 安装（手机侧）

APK 是自签名的，装之前手机会拦一下，正常流程：

1. 把 `息刻-安卓版.apk` 传到手机（数据线 / QQ / 微信 / 网盘都行）。
   - 用微信或 QQ 传的话，部分机型会把它当未知格式，建议**用「文件管理」类 App 打开**，或先改回 `.apk` 后缀。
2. 用文件管理器点开 APK。
3. 系统提示「不允许安装未知应用」→ 点提示里的**设置**→ 允许该来源（文件管理器或浏览器）→ 返回。
4. 继续安装。

装好后**首次打开要做两件授权**，App 里都有按钮直接跳：

| 授权 | 在哪 | 为什么必须 |
|---|---|---|
| 显示在其他应用上层 | 设置 → 应用 → 特殊应用权限 → 显示在其他应用上层 | 不给这个，悬浮窗根本画不出来 |
| 通知 | 系统弹窗直接允许 | Android 13 起前台服务的通知要单独授权；不给的话服务容易被系统杀掉 |

在 App 里点「开启悬浮窗」即可。悬浮窗出现后：

- **拖动**：按住它拖。
- **轻点**：打开 App（回来改设置）。
- **穿透**：在 App 的「外观与交互」里开。开了以后悬浮窗收不到触摸，外观会淡一层作提示；
  要关掉就回到 App 里关——这也是为什么穿透开关做在 App 里而不是只做在通知栏。
- 通知栏里默认只有一个「关闭悬浮窗」。想加「切换穿透」按钮的话，
  在 App 里打开「通知栏里加一个『切换穿透』按钮」。

悬浮窗上下两块，**哪块显示由程序判断**：健康分白天（06:30–20:30）自动收起；
倒计时只在运行或暂停且有剩余时间时显示；未设置、重置和到点后自动隐藏；两块都不可见时，窗口整个摘掉，
不会留一个看不见的窗口挡触摸。

---

## 自己构建

环境（2026-10-06 已共享安装到 Program Files；JAVA_HOME、ANDROID_HOME、ANDROID_SDK_ROOT 和用户 PATH 已配置。其他项目可复用，新终端生效）：

| 组件 | 版本 | 路径 |
|---|---|---|
| JDK | Microsoft OpenJDK 21.0.12.1 | `C:\Program Files\Microsoft\OpenJDK\jdk-21.0.12.1+1` |
| Android SDK | platforms 35、build-tools 34.0.0/35.0.0、platform-tools | `C:\Program Files\Android\Sdk` |
| Gradle | 8.9 | `C:\Program Files\Gradle\gradle-8.9` |
| AGP | 8.7.3 | Gradle 自动下载 |
| Kotlin | 1.9.24 | Gradle 自动下载 |

```bash
export JAVA_HOME='C:\Program Files\Microsoft\OpenJDK\jdk-21.0.12.1+1'
cd C:\project\TimeGuardAndroid

# 跑单元测试
"C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat" --console=plain :app:testDebugUnitTest

# 出 release APK（自签名）
"C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat" --console=plain :app:assembleRelease
# 产物：app/build/outputs/apk/release/app-release.apk
```

PowerShell 一键构建并运行测试：`& .\scripts\build-android.ps1`，新版产物为 `dist\息刻-安卓版.apk`。

这里只安装编译工具，未安装模拟器、系统镜像或虚拟化驱动；下面的模拟器操作需先另行安装这些组件。

签名密钥在 `keystore/timeguard.jks`，**这个文件丢了就没法给已装的版本打覆盖安装包**，注意别删。

### ⚠️ 本机构建会「假死」十几分钟：两个已知原因，都不是代码坏了

排查耗时很长，记在这里免得下次重来一遍。看到构建长时间不动，**先按下面这两条对号入座**，
别急着改代码。

**① 删文件慢到病态（清旧输出时）**

本机通过构建系统逐文件删除极慢（实测一次 `mergeDebugResources` 要清 250 多个旧资源文件，
能占满十几分钟 CPU）。症状是上一个任务显示完成、下一个任务长时间没动静，但**在推进**。

判断：抓 Gradle 守护进程的线程栈，如果 worker 是 `RUNNABLE` 卡在
`sun.nio.fs.WindowsNativeDispatcher.DeleteFile0`，就是它。
```bash
tasklist /FI "IMAGENAME eq java.exe"          # 找内存较大的那个 java.exe
"C:\Program Files\Microsoft\OpenJDK\jdk-21.0.12.1+1\bin\jstack.exe" <pid> | grep -A 16 '"Execution worker'
```
对策：**别主动 `clean`**，也别说 `-t:Rebuild`；需要「全新构建」就换输出目录而不是删旧目录。
真要清 `app/build`，让文件系统去清（在资源管理器里删是秒级的）——别从命令行或构建系统里逐文件删。

**② Kotlin 编译「回退成进程内」后真死锁**

如果日志出现：
```
e: java.rmi.NoSuchObjectException: no such object in table
e: Daemon compilation failed: Could not connect to Kotlin compile daemon
Using fallback strategy: Compile without Kotlin daemon
```
Gradle 会回退成进程内编译。而**进程内编译 + `org.gradle.parallel=true`（本仓库的默认）会死锁**：
所有 worker 都 `WAITING`、没人在跑，构建永远不返回。

对策（任选一个，`--no-parallel` 最省事）：
```bash
# 先停掉可能残留的守护进程
"C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat" --stop
# 再非并行重跑
"C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat" --offline --no-parallel --console=plain :app:testDebugUnitTest
```
这种死锁有个便宜判据：`jstack` 里**没有任何一个 worker 是 `RUNNABLE`**（对比 ①，① 里总有一个在跑）。


### 发布签名口令放哪儿

口令**不写在 `build.gradle.kts` 里**（那等于把钥匙和锁挂一起：任何拿到源码的人都能签出一个
「官方更新包」，而安卓只认签名、认不出人）。取值顺序是「先到先用」：

| 顺序 | 来源 | 用途 |
|---|---|---|
| ① | 环境变量 `TIMEGUARD_STORE_PASSWORD` / `TIMEGUARD_KEY_PASSWORD`（另有 `TIMEGUARD_KEY_ALIAS` / `TIMEGUARD_STORE_FILE`） | CI 或别人的机器 |
| ② | 工程根目录的 `keystore.properties` | 本机日常构建（已有，且已在 `.gitignore` 里排除） |

两处都没有时**不会报错**，而是产出一个**不带签名**的 release 包，并在构建日志里打印一句提示。
本机照旧直接 `assembleRelease` 即可，口令只需维护 `keystore.properties` 一份。

`keystore.properties` 形如：

```properties
storeFile=../keystore/timeguard.jks
storePassword=……
keyAlias=timeguard
keyPassword=……
```

换机器或重装系统后，把 `keystore/timeguard.jks` 和 `keystore.properties` 一起带过去就行；
只要密钥文件不变，签出来的包签名指纹就不变，**已装的版本依然能覆盖更新**。

---

## 在电脑上调试（不接手机）

改一行代码就往手机里导一次 APK、再手动装一次，太慢。电脑上有两条路，都用不到手机。

### 1) 只验逻辑 —— 几秒钟

核心逻辑（扣分口径、手动倒计时状态机）是纯 Kotlin，不碰 Android API，
在 JVM 上直接跑，不用模拟器也不用手机：

```bash
"C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat" --console=plain :app:testDebugUnitTest
```

### 2) 连界面一起看 —— 安卓模拟器

双击工程根目录的 **`启动模拟器-在电脑上运行.cmd`**，它一条龙做完：
查加速驱动 → 编译 release APK → 启动模拟器 → 等开机 → 覆盖安装 →
代授悬浮窗权限 → 打开 App。以后改完代码再双击一次即可。

**需要你先做一次管理员操作**（只做一次，装完永久有效）：

> 右键点 `C:\Program Files\Android\Sdk\extras\google\Android_Emulator_Hypervisor_Driver\silent_install.bat`，
> 选「以管理员身份运行」。

x86_64 系统镜像强制要求硬件加速，没有这个驱动模拟器会直接退出（自检退出码 6，
输出「Android Emulator hypervisor driver is not installed on this machine」）。
本机 Hyper-V 没开，所以走 AEHD 这条驱动，不需要重启。

模拟器规格（720×1600 / 320dpi / 2GB RAM / 硬件键盘直通）记在
`%USERPROFILE%\.android\avd\TimeGuard.avd\config.ini`，
是照这台机器（i3-12100 无独显、RDP 远程会话拿不到宿主 OpenGL）调的：
屏幕调小、渲染走 swiftshader 软件栈，免得按 1080×2340 开到卡成幻灯片。
比真机慢，但看排版和交互够用。

### 3) 手机上看（模拟器也懒得开时）

手机与电脑同一 Wi-Fi，Android 11 以上开「开发者选项 → 无线调试」，配对一次之后：

```bash
adb install -r app\build\outputs\apk\release\app-release.apk   # 覆盖更新
adb shell am start -n com.hzy.timeguard/.MainActivity
```

数据线同理。`install -r` 覆盖安装会保留 App 数据，配置里的倒计时状态不丢。
比「导出到手机 → 文件管理器点安装」省事得多。

---

## 怎么确认它没算错

规则移植最怕「看着对、其实差一点」，所以核心逻辑全部是不碰 Android API 的纯 Kotlin，
可以脱离手机直接在 JVM 上跑：

```bash
gradle :app:testDebugUnitTest
```

| 测试 | 盯什么 |
|---|---|
| `SleepRulesTest` | 三个时刻的跨零点归属、宽松时间解析、参数校验 |
| `ScoreEngineTest` | **金标准数字**：23:33→99、00:06→88、00:00 恰好 90、04:30 扣光、06:30 隐藏；整夜不碰手机也照扣；以及「一分钟一格扫过整夜，与手算公式逐个对照」 |
| `ManualCountdownTest` | 六种输入写法、四状态机、时刻模式拒绝暂停、暂停冻结剩余、**「剩 12:19 时暂停 → 继续后从当前时间再推 12:19」这条秒级算例**、锁屏/被杀后按截止时刻接着算、跨天后不顺延、**颜色必须是看得见的（`everyCountdownColorIsVisible`：任何阶段 alpha ≥ 0xB0）** |
| `AlarmTest` | **四点分界**排序（`01:00` 排在 `23:00` 后、`04:00` 在 `03:59` 前）、下次响铃时刻（含跨天/跨周）、**连锁不漂移**（8:00 关掉后 8:03 仍准时响）、三种关闭语义、响过就删、错过不补、**跨天重复必须还响**（`dailyAlarm_ringsAgainTheNextDay` —— 这条对应电脑版早期的一个真 bug）、序列化容错 |
| `ScoreVisualTest` | 颜色支点与电脑版一致、80/60 两档文案分界准确、0–100 每个分数的颜色都不透明 |

`ScoreEngineTest` 与 `ScoreVisualTest` 里那些数字全部来自电脑版的既有行为——两边任何一个
算出差值就是移植走样。

---

## 已知限制

- 悬浮窗不自动开启；已保存的闹钟会在重启、更新、时区或时间变化后重新注册到系统，不在开机广播里直接播放声音。
- **targetSdk 保持 35**（compileSdk 35）。本机是 Android 16，跑 targetSdk 35 的应用完全正常；
  升到 36 会引入 edge-to-edge 强制、前台服务类型收紧等行为变更，收益不大、风险不小，先不动。
- 悬浮窗**每秒刷新一次**。扣分是按时间戳算的，不存在累计误差；每秒只是刷新显示与判断是否到点。
- 悬浮窗的**透明度**用的是窗口级 alpha（0.2–1.0），低于 1.0 时会走半透明合成，
  文字边缘在个别机型上可能略糊；这是系统合成方式决定的，不是设置坏了。
- 闹钟晚到超过 90 秒时不补响。系统强制停止应用后，需重新打开应用才能恢复系统定时注册。

1.2.2：悬浮窗取消扣分明细行，只显示分数与最多两行的状态文案，文案宽度上限从 132dp 缩为 88dp。

1.2.3：闹钟独立循环播放系统闹钟铃声（不可用时使用内置铃声），按设置时长持续震动；处理或到期后停止。补齐震动与播放唤醒权限；权限提示只显示未开启项目，读取实际通知及类别状态。

1.2.4：闹钟改由 AlarmManager 独立调度、AlarmRingService 独立响铃，与悬浮窗开关完全分离；通知与主界面均可停止或稍后提醒；一次性触发即删除。加入界面、系统调度、音频和震动的 Robolectric 集成回归测试。只生成 dist/息刻-安卓版.apk。

1.2.5：新增独立锁屏响铃页面，通知提供停止响铃/稍后提醒；显示实际电池优化与锁屏页面授权状态。iQOO/vivo 另提供自启动、后台耗电设置说明（厂商开关无法检测，不显示已授权）。使用 Android 35 模拟器验证：未开悬浮窗、锁屏并杀掉应用进程后，AlarmManager 到点仍能启动响铃服务；不代表已验证 iQOO 真机的后台策略。

最终验证：Android 35 模拟器实际保存一次性闹钟，关闭悬浮窗、锁屏并杀掉进程后，由系统到点唤起响铃服务与锁屏页面；直接点停止结束响铃；一次性闹钟触发即删除。138 项自动化测试通过，lintRelease 通过。iQOO 真机的整夜后台策略尚未验证，请开启自启动和允许后台运行后做锁屏测试。
