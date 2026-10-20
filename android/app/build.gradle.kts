import java.util.Properties
import org.gradle.jvm.toolchain.JavaLanguageVersion
import org.gradle.jvm.toolchain.JavaToolchainService

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

/**
 * 发布签名信息**不进版本库**。
 *
 * 为什么：私钥（keystore/timeguard.jks）和它的口令要是都躺在工程目录里，任何拿到
 * 这份源码的人都能给这个 App 签出一个「官方更新包」——安卓只认签名，认不出人。
 * 之前的口令是明文写在 build.gradle.kts 里的，属于典型的「把钥匙和锁挂一起」。
 *
 * 取值顺序（先到先用）：
 *   ① 环境变量 TIMEGUARD_STORE_PASSWORD / TIMEGUARD_KEY_PASSWORD
 *      （也可用 TIMEGUARD_KEY_ALIAS / TIMEGUARD_STORE_FILE 覆盖路径和别名）
 *   ② 工程根目录的 keystore.properties（已在 .gitignore 里排除）
 *
 * 两者都没有时**不报错**：退化成不签名的 release 包，并在构建时打印一句提示。
 * 本机（你的开发机）用 keystore.properties 走日常流程即可，口令只需写在那一份里。
 */
val keystorePropsFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply {
    if (keystorePropsFile.exists()) keystorePropsFile.inputStream().use { load(it) }
}

fun secret(envName: String, propName: String): String? =
    System.getenv(envName)?.takeIf { it.isNotBlank() }
        ?: keystoreProps.getProperty(propName)?.takeIf { it.isNotBlank() }

val storeFilePath = secret("TIMEGUARD_STORE_FILE", "storeFile") ?: "../keystore/timeguard.jks"
val storePass = secret("TIMEGUARD_STORE_PASSWORD", "storePassword")
val keyAliasValue = secret("TIMEGUARD_KEY_ALIAS", "keyAlias") ?: "timeguard"
val keyPass = secret("TIMEGUARD_KEY_PASSWORD", "keyPassword")

val hasSigning = storePass != null && keyPass != null

android {
    namespace = "com.hzy.timeguard"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.hzy.timeguard"
        minSdk = 26
        targetSdk = 35
        versionCode = 14
        versionName = "1.2.11"
    }

    signingConfigs {
        if (hasSigning) {
            create("release") {
                storeFile = file(storeFilePath)
                storePassword = storePass
                keyAlias = keyAliasValue
                keyPassword = keyPass
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // 没有口令时不挂签名配置：产出不带签名的 release 包，而不是悄悄用一个硬编码口令
            if (hasSigning) {
                signingConfig = signingConfigs.getByName("release")
            }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        viewBinding = true
    }
    testOptions {
        unitTests.isIncludeAndroidResources = true
        unitTests.all {
            // JDK 17 eagerly initializes old-SDK proxy types used by Robolectric 4.17.
            it.javaLauncher.set(project.extensions.getByType<JavaToolchainService>().launcherFor {
                languageVersion.set(JavaLanguageVersion.of(21))
            })
            // Keep Robolectric's cache and download lock inside the project's build directory.
            val testHome = layout.buildDirectory.dir("test-home").get().asFile
            it.doFirst { testHome.mkdirs() }
            it.systemProperty("user.home", testHome.absolutePath)
            it.systemProperty("robolectric.dependency.repo.url", "https://repo.maven.apache.org/maven2")
            it.jvmArgs("--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/java.util=ALL-UNNAMED", "--add-opens=java.base/java.io=ALL-UNNAMED")
        }
    }
}

// 每次配置阶段都提示一句，免得「包没签名」被当成莫名奇妙的问题去查
if (!hasSigning) {
    logger.lifecycle(
        "⚠ 未找到发布签名口令：release 包将不带签名。" +
            "请在 $keystorePropsFile 里写 storePassword / keyPassword，" +
            "或设置环境变量 TIMEGUARD_STORE_PASSWORD / TIMEGUARD_KEY_PASSWORD。"
    )
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.17")
}
