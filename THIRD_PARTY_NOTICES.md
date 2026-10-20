# 第三方组件声明

本项目的非商业许可仅适用于 h3y12 有权授权的原创代码与资源，不改变第三方的版权和原有许可。不得把第三方独立组件误标为禁止商用。

Android 下列清单来自实际解析的 releaseRuntimeClasspath（含传递依赖，按坐标去重）。这些组件的 Maven POM 声明 Apache License 2.0；Guava ListenableFuture 从 guava-parent:26.0-android 继承。完整许可见 [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)。运行构件未提供单独 NOTICE 文本；未来更新依赖须重新核对，保留新增的上游通知。

AndroidX / Android Data Binding 归 Android Open Source Project 及其贡献者；Material Components、Guava、Error Prone 归 Google 及其贡献者；Kotlin、协程和 JetBrains Annotations 归 JetBrains 及其贡献者。具体权利归属以各上游项目为准。

| 运行构件 | 许可 |
|---|---|
| `androidx.databinding:viewbinding:8.7.3` | Apache-2.0 |
| `com.google.android.material:material:1.12.0` | Apache-2.0 |
| `androidx.constraintlayout:constraintlayout:2.1.4` | Apache-2.0 |
| `androidx.appcompat:appcompat-resources:1.7.0` | Apache-2.0 |
| `androidx.appcompat:appcompat:1.7.0` | Apache-2.0 |
| `androidx.emoji2:emoji2-views-helper:1.3.0` | Apache-2.0 |
| `androidx.emoji2:emoji2:1.3.0` | Apache-2.0 |
| `androidx.viewpager2:viewpager2:1.0.0` | Apache-2.0 |
| `androidx.fragment:fragment:1.5.4` | Apache-2.0 |
| `androidx.activity:activity:1.8.0` | Apache-2.0 |
| `androidx.drawerlayout:drawerlayout:1.1.1` | Apache-2.0 |
| `androidx.coordinatorlayout:coordinatorlayout:1.1.0` | Apache-2.0 |
| `androidx.transition:transition:1.5.0` | Apache-2.0 |
| `androidx.dynamicanimation:dynamicanimation:1.0.0` | Apache-2.0 |
| `androidx.vectordrawable:vectordrawable-animated:1.1.0` | Apache-2.0 |
| `androidx.vectordrawable:vectordrawable:1.1.0` | Apache-2.0 |
| `androidx.legacy:legacy-support-core-utils:1.0.0` | Apache-2.0 |
| `androidx.loader:loader:1.0.0` | Apache-2.0 |
| `androidx.viewpager:viewpager:1.0.0` | Apache-2.0 |
| `androidx.recyclerview:recyclerview:1.1.0` | Apache-2.0 |
| `androidx.customview:customview:1.1.0` | Apache-2.0 |
| `androidx.core:core:1.13.1` | Apache-2.0 |
| `androidx.savedstate:savedstate:1.2.1` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-livedata:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-viewmodel:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-process:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-livedata-core:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-common:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-runtime:2.6.2` | Apache-2.0 |
| `androidx.lifecycle:lifecycle-viewmodel-savedstate:2.6.2` | Apache-2.0 |
| `androidx.core:core-ktx:1.13.1` | Apache-2.0 |
| `androidx.annotation:annotation-experimental:1.4.0` | Apache-2.0 |
| `androidx.cursoradapter:cursoradapter:1.0.0` | Apache-2.0 |
| `androidx.profileinstaller:profileinstaller:1.3.1` | Apache-2.0 |
| `androidx.resourceinspection:resourceinspection-annotation:1.0.1` | Apache-2.0 |
| `androidx.cardview:cardview:1.0.0` | Apache-2.0 |
| `androidx.interpolator:interpolator:1.0.0` | Apache-2.0 |
| `androidx.versionedparcelable:versionedparcelable:1.1.1` | Apache-2.0 |
| `androidx.collection:collection:1.1.0` | Apache-2.0 |
| `androidx.concurrent:concurrent-futures:1.1.0` | Apache-2.0 |
| `androidx.startup:startup-runtime:1.1.1` | Apache-2.0 |
| `androidx.tracing:tracing:1.0.0` | Apache-2.0 |
| `androidx.arch.core:core-runtime:2.2.0` | Apache-2.0 |
| `androidx.arch.core:core-common:2.2.0` | Apache-2.0 |
| `androidx.documentfile:documentfile:1.0.0` | Apache-2.0 |
| `androidx.localbroadcastmanager:localbroadcastmanager:1.0.0` | Apache-2.0 |
| `androidx.print:print:1.0.0` | Apache-2.0 |
| `androidx.annotation:annotation-jvm:1.6.0` | Apache-2.0 |
| `org.jetbrains.kotlinx:kotlinx-coroutines-core-jvm:1.6.4` | Apache-2.0 |
| `org.jetbrains.kotlinx:kotlinx-coroutines-android:1.6.4` | Apache-2.0 |
| `org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.8.22` | Apache-2.0 |
| `org.jetbrains.kotlin:kotlin-stdlib-jdk7:1.8.22` | Apache-2.0 |
| `org.jetbrains.kotlin:kotlin-stdlib:1.9.24` | Apache-2.0 |
| `org.jetbrains:annotations:13.0` | Apache-2.0 |
| `com.google.errorprone:error_prone_annotations:2.15.0` | Apache-2.0 |
| `androidx.constraintlayout:constraintlayout-core:1.0.4` | Apache-2.0 |
| `com.google.guava:listenablefuture:1.0` | Apache-2.0 |

Windows 发布包依赖用户单独安装的 .NET 8 Desktop Runtime，没有将 .NET 运行时打包进 EXE；运行时、系统字体和系统提示音保留各自原有权利。

Gradle Wrapper、Android Gradle Plugin、JUnit 和 Robolectric 仅用于构建或测试；来源见 [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md)。Gradle 包装脚本保留原始许可头。公开效果图可能包含操作系统界面，不将其认作本项目原创资源。

Android 构建自动将 LICENSE、NOTICE、本文与 licenses/ 复制到 APK 的 assets；Release 同时提供许可附件。修改依赖或分发方式时，应更新本声明并保留实际要求的许可和通知。
