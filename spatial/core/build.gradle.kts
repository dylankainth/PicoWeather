plugins {
    kotlin("jvm") version "1.9.24"
}

repositories {
    mavenCentral()
}

dependencies {
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
