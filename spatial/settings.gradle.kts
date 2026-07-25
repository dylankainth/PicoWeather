rootProject.name = "weathervr-spatial"

// `core` is deliberately a plain Kotlin/JVM library with no Android or PICO Spatial
// dependency. That is what makes it testable on a desktop JVM — the same reason
// tools/verify.py exists on the Unity side, and the reason this module is the part of
// the port that can be proven correct before any SDK is available.
include(":core")
