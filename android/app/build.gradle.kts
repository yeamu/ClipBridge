import java.util.Properties

plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }

val releaseKeystoreProperties = Properties()
val releaseKeystorePropertiesFile = rootProject.file("keystore.properties")

if (releaseKeystorePropertiesFile.isFile) {
    releaseKeystorePropertiesFile.inputStream().use(releaseKeystoreProperties::load)
}

val releaseStoreFile =
    releaseKeystoreProperties.getProperty("storeFile") ?: System.getenv("CLIPBRIDGE_KEYSTORE_FILE")
val releaseStorePassword =
    releaseKeystoreProperties.getProperty("storePassword") ?: System.getenv("CLIPBRIDGE_KEYSTORE_PASSWORD")
val releaseKeyAlias =
    releaseKeystoreProperties.getProperty("keyAlias") ?: System.getenv("CLIPBRIDGE_KEY_ALIAS")
val releaseKeyPassword =
    releaseKeystoreProperties.getProperty("keyPassword") ?: System.getenv("CLIPBRIDGE_KEY_PASSWORD")
val hasReleaseKeystore =
    !releaseStoreFile.isNullOrBlank() &&
        !releaseStorePassword.isNullOrBlank() &&
        !releaseKeyAlias.isNullOrBlank() &&
        !releaseKeyPassword.isNullOrBlank()

android { namespace = "com.clipbridge"; compileSdk = 35
    defaultConfig { applicationId = "com.clipbridge"; minSdk = 29; targetSdk = 35; versionCode = 105; versionName = "1.2.2" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true }
    composeOptions { kotlinCompilerExtensionVersion = "1.5.14" }

    signingConfigs {
        create("release") {
            if (hasReleaseKeystore) {
                storeFile = file(releaseStoreFile!!)
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        getByName("release") {
            isDebuggable = false
            isMinifyEnabled = false
            if (hasReleaseKeystore) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }
}
dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation(platform("androidx.compose:compose-bom:2024.09.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
