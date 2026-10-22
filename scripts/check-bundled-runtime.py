#!/usr/bin/env python3
"""Fails unless every package bundles the latest .NET 10 runtime patch.

Both programs are self-contained, so each package carries the runtime of whichever SDK built
it, and an operating system update never patches it. A self-hosted runner with a stale SDK
once shipped 0.17.0's armhf packages on runtime 10.0.7 while the rest had 10.0.13, months of
security fixes behind, and nothing noticed. This reads the runtime version out of each
package's program and compares it with the latest 10.0 runtime Microsoft lists.

Usage: scripts/check-bundled-runtime.py <package.deb|package.zip>...
"""
import json
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
PROGRAMS = ("pdn-mailcast-receiver", "pdn-mailcast-headend", "pdn-mailcast-receiver.exe")
RUNTIME = re.compile(rb"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def program_bytes(package):
    if package.suffix == ".zip":
        with zipfile.ZipFile(package) as z:
            names = [n for n in z.namelist() if n.rsplit("/", 1)[-1] in PROGRAMS]
            return [(n, z.read(n)) for n in names]
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["dpkg-deb", "-x", str(package), tmp], check=True)
        return [(str(p.relative_to(tmp)), p.read_bytes())
                for p in Path(tmp).rglob("*") if p.name in PROGRAMS and p.is_file() and not p.is_symlink()]


def main(packages):
    if not packages:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for package in map(Path, packages):
        programs = program_bytes(package)
        if not programs:
            print(f"::error::{package.name}: no program found to check")
            bad = True
        for name, data in programs:
            found = sorted({m.decode() for m in RUNTIME.findall(data)})
            print(f"{package.name}: {name} bundles {', '.join(found) or 'nothing recognisable'}")
            if found != [want]:
                print(f"::error::{package.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                      f"not {want}. Update the .NET 10 SDK on the runner that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
