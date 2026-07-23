"""Run the project's pure-logic checks outside Unity.

Compiles the runtime scripts and ``tools/verify_logic/Verify.cs`` straight from
source against the Unity managed assemblies, then runs the result.

Why this exists rather than Unity EditMode tests: the Unity editor holds an
exclusive lock on the project, so while it is open nothing else can run the
test runner -- and during development that is essentially always. Everything
checked here is deterministic maths over managed types (Mathf, Vector3, and our
own code), which runs perfectly well on a plain .NET runtime. So these checks
can run at any time, in a second, against the *real* baked data, instead of
only when someone remembers to open the Test Runner window.

Anything needing the engine proper -- ScriptableObject, Texture3D, MonoBehaviour
lifecycles, shaders -- is out of scope here by construction and has to be
verified in the editor.

Compiling the scripts from source rather than referencing Unity's
``Assembly-CSharp.dll`` is deliberate: it checks what is on disk right now
rather than whatever the editor last happened to build, so edits are covered
before the editor has noticed them. The project's *package* assemblies do still
come from ``Library/ScriptAssemblies``, so Unity has to have opened the project
at least once.

    python tools/verify.py
"""

from __future__ import annotations

import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)
BUILD_DIR = os.path.join(HERE, "verify_logic", "build")

UNITY_GLOBS = [
    r"C:\Program Files\Unity\Hub\Editor\*\Editor\Data",
    r"C:\Program Files\Unity\Editor\Data",
    "/Applications/Unity/Hub/Editor/*/Unity.app/Contents",
    os.path.expanduser("~/Unity/Hub/Editor/*/Editor/Data"),
]

RUNTIME_CONFIG = """{
  "runtimeOptions": {
    "tfm": "net6.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "%s" }
  }
}
"""


def find_unity_data() -> str:
    candidates: list[str] = []
    for pattern in UNITY_GLOBS:
        candidates.extend(glob.glob(pattern))
    if not candidates:
        raise SystemExit(
            "Could not find a Unity installation. Set UNITY_DATA to the editor's "
            "Data directory and try again."
        )
    return sorted(candidates)[-1]  # newest version by name


def find_runtime_version(unity_data: str) -> str:
    shared = os.path.join(unity_data, "NetCoreRuntime", "shared", "Microsoft.NETCore.App")
    versions = sorted(os.listdir(shared)) if os.path.isdir(shared) else []
    if not versions:
        raise SystemExit(f"No .NET runtime found under {shared}")
    return versions[-1]


def main() -> int:
    unity_data = os.environ.get("UNITY_DATA") or find_unity_data()
    dotnet = os.path.join(unity_data, "NetCoreRuntime", "dotnet.exe")
    if not os.path.exists(dotnet):
        dotnet = os.path.join(unity_data, "NetCoreRuntime", "dotnet")
    csc = os.path.join(unity_data, "DotNetSdkRoslyn", "csc.dll")

    # Runtime scripts only. The Editor tree references UnityEditor, which drags in
    # the whole editor assembly for no benefit to these checks.
    sources = sorted(glob.glob(
        os.path.join(REPO_ROOT, "Assets", "WeatherVR", "Scripts", "**", "*.cs"),
        recursive=True))
    if not sources:
        raise SystemExit("No runtime scripts found under Assets/WeatherVR/Scripts.")

    netstandard = os.path.join(unity_data, "NetStandard", "ref", "2.1.0", "netstandard.dll")
    engine_dlls = glob.glob(os.path.join(unity_data, "Managed", "UnityEngine", "UnityEngine*.dll"))

    # Package assemblies the runtime scripts touch -- UnityEngine.UI for the
    # provenance label, and whatever else the project pulls in.
    #
    # Only ScriptAssemblies, not PackageCache: PackageCache also carries native
    # binaries (sqlite3, libonigwrap) that the C# compiler rejects outright, and
    # ScriptAssemblies is exactly the managed set Unity itself compiles against.
    # Assembly-CSharp is excluded because these sources are being compiled here and
    # a stale copy would collide with them.
    script_assemblies = os.path.join(REPO_ROOT, "Library", "ScriptAssemblies")
    if not os.path.isdir(script_assemblies):
        raise SystemExit(
            "Library/ScriptAssemblies not found. Open the project in Unity once so "
            "it compiles the packages, then run this again."
        )

    package_dlls = [
        dll for dll in glob.glob(os.path.join(script_assemblies, "*.dll"))
        if not os.path.basename(dll).startswith("Assembly-CSharp")
    ]

    os.makedirs(BUILD_DIR, exist_ok=True)
    output = os.path.join(BUILD_DIR, "Verify.dll")

    response_path = os.path.join(BUILD_DIR, "verify.rsp")
    with open(response_path, "w", encoding="utf-8") as handle:
        handle.write("-target:exe\n-nostdlib+\n-langversion:9.0\n-nowarn:0169,0414,0649,0618\n")
        handle.write(f'-r:"{netstandard}"\n')
        for dll in engine_dlls:
            handle.write(f'-r:"{dll}"\n')
        for dll in package_dlls:
            handle.write(f'-r:"{dll}"\n')
        for source in sources:
            handle.write(f'"{source}"\n')
        handle.write(f'"{os.path.join(HERE, "verify_logic", "Verify.cs")}"\n')

    print(f"Compiling {len(sources)} runtime scripts plus the verifier...")
    compile_result = subprocess.run(
        [dotnet, csc, f"@{response_path}", f"-out:{output}"],
        capture_output=True, text=True,
    )
    if compile_result.returncode != 0:
        print(compile_result.stdout)
        print(compile_result.stderr, file=sys.stderr)
        return 1

    # The verifier runs as a plain .NET app, so the assemblies it references have
    # to sit beside it rather than being resolved out of the Unity install.
    import shutil

    for dll in [*engine_dlls, netstandard, *package_dlls]:
        shutil.copy2(dll, BUILD_DIR)

    with open(os.path.join(BUILD_DIR, "Verify.runtimeconfig.json"), "w", encoding="utf-8") as handle:
        handle.write(RUNTIME_CONFIG % find_runtime_version(unity_data))

    env = dict(os.environ, PROJ=REPO_ROOT)
    return subprocess.run([dotnet, output], env=env).returncode


if __name__ == "__main__":
    raise SystemExit(main())
