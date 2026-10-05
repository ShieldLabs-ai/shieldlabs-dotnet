#!/usr/bin/env python3
"""Compile the real SDK against deliberately changed contracts in an isolated directory."""
import argparse
import copy
import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile

import yaml

ROOT = Path(__file__).resolve().parents[1]
module_spec = importlib.util.spec_from_file_location("wire_generator", ROOT / "scripts/generate-wire.py")
generator = importlib.util.module_from_spec(module_spec)
module_spec.loader.exec_module(generator)


def mutations(document):
    yield "baseline", document, True
    for schema, field in [("HistoryPage", "total"), ("HistoryRow", "score"), ("DomainProfile", "Weight"),
                          ("IdentificationScoredData", "risk_score"),
                          ("Signal", "weight"), ("TrafficSource", "channel"),
                          ("DetectionFlags", "vpn"), ("IdentificationScoredEvent", "event_type")]:
        changed = copy.deepcopy(document)
        # Named object references remain representable, but are incompatible with the reader.
        changed["components"]["schemas"][schema]["properties"][field] = {"$ref": "#/components/schemas/IpInfo"}
        yield f"{schema}.{field} type", changed, False
    changed = copy.deepcopy(document)
    props = changed["components"]["schemas"]["HistoryRow"]["properties"]
    props["renamed_score"] = props.pop("score")
    yield "HistoryRow.score rename", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["parameters"]["HistoryLimit"]["schema"]["type"] = "string"
    yield "History limit type", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["parameters"]["HistorySearchType"]["schema"]["type"] = "integer"
    yield "History search_type type", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["parameters"]["ShieldDomain"]["name"] = "X-New-Domain"
    yield "Profile header rename", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["parameters"]["HistorySearchType"]["schema"]["enum"].remove("user_hid")
    yield "Lookup value removal", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["schemas"]["HistoryPage"]["properties"]["data"]["items"] = {"type": "string"}
    yield "History row array item type", changed, False
    changed = copy.deepcopy(document)
    changed["paths"]["/api/v1/history/{search_type}/{value}"]["get"]["responses"]["200"]["content"]["application/json"]["schema"] = {"$ref": "#/components/schemas/DomainProfile"}
    yield "History operation response binding", changed, False
    changed = copy.deepcopy(document)
    for hook in changed["webhooks"].values():
        if hook["post"]["operationId"] == "identificationScored":
            hook["post"]["requestBody"]["content"]["application/json"]["schema"] = {"$ref": "#/components/schemas/WebhookPingEvent"}
    yield "Scored webhook request body binding", changed, False
    changed = copy.deepcopy(document)
    changed["components"]["schemas"]["HistoryRow"]["properties"]["future_hint"] = {"type": "string"}
    yield "Optional additive field", changed, True
    for path in ("/api/v1/history/{search_type}/{value}", "/v1/profile"):
        changed = copy.deepcopy(document)
        changed["paths"]["/new" + path] = changed["paths"].pop(path)
        yield f"Route changed: {path}", changed, False
    for parameter, location in (("HistoryLimit", "header"), ("HistorySearchType", "query"), ("ShieldDomain", "query")):
        changed = copy.deepcopy(document)
        changed["components"]["parameters"][parameter]["in"] = location
        yield f"Parameter location changed: {parameter}", changed, False
    for path in ("/api/v1/history/{search_type}/{value}", "/v1/profile"):
        for required in (True, False):
            changed = copy.deepcopy(document)
            changed["paths"][path]["get"].setdefault("parameters", []).append(
                {"name": "future_parameter", "in": "query", "required": required, "schema": {"type": "string"}})
            yield f"New {'required' if required else 'optional'} parameter: {path}", changed, not required
    changed = copy.deepcopy(document)
    changed["components"]["schemas"]["WebhookPingEvent"]["properties"]["created_at"] = {"type": "integer"}
    yield "Ping envelope timestamp type", changed, False
    changed = copy.deepcopy(document)
    props = changed["components"]["schemas"]["WebhookPingEvent"]["properties"]
    props["renamed_version"] = props.pop("schema_version")
    yield "Ping envelope version rename", changed, False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--docker", action="store_true", help="Use the installed .NET 8 Docker image")
    parser.add_argument("--case", action="append", help="Run named cases plus the baseline")
    args = parser.parse_args()
    document = yaml.safe_load((ROOT / "resources/shieldlabs-api.yaml").read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="shieldlabs-wire-") as temporary:
        work = Path(temporary)
        shutil.copytree(ROOT / "src", work / "src", ignore=shutil.ignore_patterns("bin", "obj"))
        shutil.copy2(ROOT / "Directory.Build.props", work)
        (work / "probe").mkdir()
        shutil.copy2(ROOT / "tests/PackageConsumer/Program.cs", work / "probe/Program.cs")
        (work / "probe/Probe.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>'
            '<Reference Include="ShieldLabs"><HintPath>../src/ShieldLabs/bin/Release/net8.0/ShieldLabs.dll</HintPath>'
            '</Reference></ItemGroup></Project>', encoding="utf-8")
        command = ["dotnet", "build", "src/ShieldLabs/ShieldLabs.csproj", "-c", "Release",
                   "-warnaserror", "-p:TargetFrameworks=net8.0", "-p:NuGetAudit=false", "--nologo"]
        if args.docker:
            command = ["docker", "run", "--rm", "--cpus=2", "-v", f"{work}:/src", "-w", "/src",
                       "mcr.microsoft.com/dotnet/sdk:8.0"] + command
        cases = list(mutations(document))
        if args.case and not set(args.case).issubset({name for name, _, _ in cases}):
            raise SystemExit("Unknown mutation case")
        for name, changed, should_pass in cases:
            if args.case and name != "baseline" and name not in args.case:
                continue
            try:
                content = generator.generate(changed)
            except generator.UnsupportedContract as error:
                if should_pass:
                    raise SystemExit(f"FAIL: {name}: {error}") from error
                print(f"PASS: {name}: rejected unsupported contract: {error}", flush=True)
                continue
            (work / "src/ShieldLabs/Internal/WireContract.g.cs").write_text(content, encoding="utf-8")
            result = subprocess.run(command, cwd=work, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
            passed = result.returncode == 0
            # Infrastructure errors must never be mistaken for a successful negative test.
            compiler_failure = "error CS" in result.stdout
            if passed != should_pass or (not should_pass and not compiler_failure):
                print(result.stdout)
                raise SystemExit(f"FAIL: {name} (expected compile {'success' if should_pass else 'failure'})")
            print(f"PASS: {name}: compile {'succeeded' if passed else 'rejected incompatible contract'}", flush=True)
            if should_pass and name.startswith("New optional parameter:"):
                probe = command[:command.index("dotnet")] + ["dotnet", "run", "--project", "probe/Probe.csproj", "-c", "Release"]
                result = subprocess.run(probe, cwd=work, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
                if result.returncode != 0:
                    print(result.stdout)
                    raise SystemExit(f"FAIL: {name}: runtime request probe")
                print(f"PASS: {name}: exact outgoing requests unchanged; optional parameter not sent", flush=True)


if __name__ == "__main__":
    main()
