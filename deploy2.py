#!/usr/bin/env python3
"""
Bumps patch version, commits version.txt, pushes tag → CI builds Windows + Linux.
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent

# A Windows console defaults to the legacy ANSI codepage (cp1250 here), which cannot encode the arrow
# below — and printing it is the first thing this script does, so it died before bumping anything.
# errors="replace" as well, because a mangled character must never be what stops a release either.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):  # redirected to something that cannot be reconfigured
        pass


def run(cmd: list[str], check: bool = True) -> subprocess.CompletedProcess:
    print(f"$ {' '.join(cmd)}")
    return subprocess.run(cmd, check=check)


def main() -> None:
    version_file = ROOT / "version.txt"
    version = version_file.read_text().strip()
    major, minor, patch = version.split(".")
    new_version = f"{major}.{minor}.{int(patch) + 1}"
    tag = f"v{new_version}"

    print(f"Version: {version} → {new_version}")

    version_file.write_text(new_version + "\n")

    run(["git", "add", "version.txt"])
    run(["git", "commit", "-m", f"chore: version {new_version}"])
    run(["git", "push"])

    run(["git", "tag", tag])
    run(["git", "push", "origin", tag])

    print(f"\nTag {tag} pushed — GitHub Actions will build Windows + Linux and create the release.")


if __name__ == "__main__":
    main()
