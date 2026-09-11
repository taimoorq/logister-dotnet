#!/usr/bin/env python3
"""Turn dotnet's informational vulnerability report into a failing CI gate."""
import json
import sys


def verify(report):
    if report.get("version") != 1 or not isinstance(report.get("projects"), list):
        raise ValueError("Unexpected NuGet audit schema")
    affected = []
    for project in report["projects"]:
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    if package.get("vulnerabilities"):
                        affected.append(package["id"])
    if affected:
        raise ValueError("Vulnerable dependencies: " + ", ".join(sorted(set(affected))))


if __name__ == "__main__":
    with open(sys.argv[1]) as handle:
        verify(json.load(handle))
