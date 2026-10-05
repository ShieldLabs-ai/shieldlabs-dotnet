#!/usr/bin/env python3
"""Restore and run a consumer of the packed NuGet artifact, never a project reference."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--docker", action="store_true")
    args = parser.parse_args()
    version = ET.parse(ROOT / "src/ShieldLabs/ShieldLabs.csproj").findtext(".//Version")
    package = ROOT / f"artifacts/ShieldLabs.{version}.nupkg"
    if not package.is_file():
        raise SystemExit("Pack the Release build into artifacts/ first.")
    with tempfile.TemporaryDirectory(prefix="shieldlabs-consumer-") as temporary:
        work = Path(temporary)
        shutil.copytree(ROOT / "tests/PackageConsumer", work / "consumer", ignore=shutil.ignore_patterns("bin", "obj"))
        (work / "packages").mkdir()
        shutil.copy2(package, work / "packages")
        commands = [
            ["dotnet", "restore", "consumer/PackageConsumer.csproj", "--source", "packages", "--packages", ".nuget", f"-p:SdkPackageVersion={version}"],
            ["dotnet", "run", "--project", "consumer/PackageConsumer.csproj", "-c", "Release", "--no-restore", f"-p:SdkPackageVersion={version}"],
        ]
        for command in commands:
            if args.docker:
                command = ["docker", "run", "--rm", "--cpus=2", "-v", f"{work}:/src", "-w", "/src", "mcr.microsoft.com/dotnet/sdk:8.0"] + command
            subprocess.run(command, cwd=work, check=True, timeout=180)


if __name__ == "__main__":
    main()
