#!/usr/bin/env python3
"""Run repeatable source checks; never label them as C#/Windows execution."""
from __future__ import annotations

import concurrent.futures
import datetime
import importlib.util
import json
import platform
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
CHECKS = {
    "full_static": ["Tests/FollowupSourceValidation/followup_static_check.py"],
    "settings_source": ["Tests/SettingsUxSourceValidation/validate_settings_ux.py"],
    "april_source": ["Tests/AprilFoolsMotionRegression/validate_source.py"],
    "preview_layout_source": ["Tests/PreviewNavigationRegression/validate_layout.py"],
    "debug_wiring_source": ["Tests/DebugShortcutsRegression/validate_contracts.py"],
    "plugin_wiring_source": ["Tests/PluginManagementRegression/validate_contracts.py"],
    "catalog_source": ["Tests/FeatureSearchRegression/validate_catalog.py"],
    "catalog_generation_consistency": ["Tests/FeatureSearchRegression/generate_catalog.py", "--check"],
    "catalog_python_regression": ["Tests/FeatureSearchRegression/test_runtime_catalog.py"],
}


def run(item):
    name, arguments = item
    command = [sys.executable, str(ROOT / arguments[0]), *arguments[1:]]
    try:
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=60)
        record = {"status": "passed" if result.returncode == 0 else "failed",
                  "command": "python " + " ".join(arguments), "exit_code": result.returncode,
                  "stdout": result.stdout, "stderr": result.stderr}
        if name == "full_static" and result.returncode == 0:
            record["source_assertions"] = json.loads(result.stdout)
        return name, record
    except subprocess.TimeoutExpired:
        return name, {"status": "timed_out", "command": "python " + " ".join(arguments), "deadline_seconds": 60}
    except Exception as error:
        return name, {"status": "failed", "reason": str(error)}


def main():
    jobs = dict(CHECKS)
    has_ast = all(importlib.util.find_spec(name) is not None for name in ("tree_sitter", "tree_sitter_c_sharp"))
    if has_ast:
        jobs["preview_ast_source"] = ["Tests/PreviewInteractionRegression/validate_contracts.py"]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        results = dict(executor.map(run, jobs.items()))
    if not has_ast:
        results["preview_ast_source"] = {"status": "not_run", "reason": "tree_sitter and/or tree_sitter_c_sharp is unavailable"}
    sdk = shutil.which("dotnet")
    csharp_reason = "No .NET SDK is available" if sdk is None else "C# execution is not performed by this source-only script; use Tests/Test-Ux30.ps1"
    projects = [str(path.relative_to(ROOT)) for path in sorted((ROOT / "Tests").rglob("*.csproj"))]
    record = {
        "kind": "Executed source checks and Python tests; not a compiled or runtime-certified release",
        "generated_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "environment": {"system": platform.system(), "python": platform.python_version(), "dotnet": sdk,
                        "powershell": shutil.which("pwsh") or shutil.which("powershell")},
        "checks": results,
        "wpf_build": {"status": "not_run", "reason": csharp_reason},
        "compiler_diagnostics": {"status": "not_run", "warnings": None, "errors": None},
        "csharp_regressions": [{"project": path, "status": "not_run", "reason": csharp_reason} for path in projects],
        "windows_test_runner": {"status": "not_run", "reason": "This Python script does not execute PowerShell or Windows process lifecycle tests"},
        "windows_visual_and_integration": {"status": "manual_pending",
            "reason": "Rendering, DPI, touch, real game/Java launch, login, live APIs, updater and rollback require Windows verification"},
        "interpretation": [
            "Resource-key uniqueness is a separate source-assertion category, not thousands of behavioral tests.",
            "Empty static warnings do not establish compiler or runtime warning counts.",
            "Historical Ux30/Ux50 and per-feature reports refer to their own runs; do not add their counts as new coverage.",
            "Passing these checks does not establish that the application has no bugs or that future versions remain compatible.",
        ],
    }
    (HERE / "verification-status.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if "source_assertions" in results["full_static"]:
        (HERE / "full-static-report.json").write_text(json.dumps(results["full_static"]["source_assertions"], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"executed": {name: item["status"] for name, item in results.items()},
                      "csharp_projects_not_executed": len(projects), "report": str(HERE / "verification-status.json")},
                     ensure_ascii=False, indent=2))
    return int(any(item["status"] in ("failed", "timed_out") for item in results.values()))


if __name__ == "__main__":
    raise SystemExit(main())
