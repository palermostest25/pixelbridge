plugins {
    id("com.android.application")
}

android {
    namespace = "dev.pixelbridge.app"
    compileSdk = 37

    defaultConfig {
        applicationId = "dev.pixelbridge.app"
        minSdk = 31
        targetSdk = 37
        versionCode = 10
        versionName = "0.4.4"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
