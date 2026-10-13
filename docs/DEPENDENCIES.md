# 依赖与资源来源

以下清单对应当前直接依赖，便于公开前核对；传递依赖和发布包内的通知文件仍需随实际版本保留。此清单不替代作者为项目自身选择 LICENSE。

| 组件 | 版本 / 用途 | 上游授权与来源 |
|---|---|---|
| .NET / WPF / Windows Forms | .NET 8，桌面运行与界面 | [WPF MIT 许可证](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)，[.NET Runtime](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Kotlin | 1.9.24，Android 语言与标准库 | [Apache 2.0 及第三方组件说明](https://github.com/JetBrains/kotlin/blob/master/license/README.md) |
| AndroidX Core KTX | 1.13.1，运行依赖 | [AndroidX Apache 2.0](https://github.com/androidx/androidx/blob/androidx-main/LICENSE.txt) |
| AndroidX AppCompat | 1.7.0，运行依赖 | 同上 |
| AndroidX ConstraintLayout | 2.1.4，运行依赖 | [官方来源 / Apache 2.0](https://github.com/androidx/constraintlayout) |
| Material Components Android | 1.12.0，运行依赖 | [Apache 2.0](https://github.com/material-components/material-components-android/blob/master/LICENSE) |
| Gradle Wrapper | 8.9，仅构建 | [Gradle Apache 2.0](https://github.com/gradle/gradle/blob/master/LICENSE)；仓库保留生成脚本中的授权头 |
| Android Gradle Plugin | 8.7.3，仅构建 | [Android 构建工具来源](https://android.googlesource.com/platform/tools/base/) |
| JUnit 4 | 4.13.2，仅测试 | [Eclipse Public License 1.0](https://github.com/junit-team/junit4/blob/main/pom.xml) |
| Robolectric | 4.17，仅测试 | [Apache 2.0](https://github.com/robolectric/robolectric/blob/master/LICENSE) |

版本由 android/build.gradle.kts 和 android/app/build.gradle.kts 声明。Windows 当前没有额外 NuGet 运行依赖。

桌面图标的生成脚本位于 desktop/scripts/brand_icon.py、make_icon.py，源资源位于 desktop/Assets；Android 图标与通知图标位于 android/app/src/main/res。界面使用系统字体，仓库不随包分发 Microsoft 字体文件。

文档效果图直接由项目实际界面采集：Windows 原生 WPF 快照、空白 Android 模拟器中的演示配置。Android 悬浮窗截图包含模拟器自带的系统界面 / 壁纸，用于展示跨应用效果；它们不是本项目自行设计的图标或壁纸资源。