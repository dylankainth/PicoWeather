plugins {
    kotlin("jvm") version "1.9.24"
    kotlin("plugin.serialization") version "1.9.24"
}

repositories {
    mavenCentral()
}

dependencies {
    // kotlinx.serialization rather than org.json or Moshi: it works identically on the
    // JVM (so these tests exercise the shipping path) and on Android, and it is the one
    // dependency this module takes.
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")
    testImplementation(kotlin("test"))
}

kotlin {
    // 17 rather than 21: this module is compiled into an Android app later, and
    // Android's toolchain tops out below the JDK installed here.
    jvmToolchain(17)
}

tasks.test {
    useJUnitPlatform()
    testLogging {
        events("passed", "failed", "skipped")
        showStandardStreams = true
    }
}
