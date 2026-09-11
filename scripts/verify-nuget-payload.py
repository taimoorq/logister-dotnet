#!/usr/bin/env python3
"""Compare tested content after NuGet signing and .NET 10 pack metadata IDs.

Signature authenticity must also be checked with `dotnet nuget verify --all`.
https://devblogs.microsoft.com/dotnet/Introducing-Repository-Signatures/
"""
import sys
import re
import xml.etree.ElementTree as ET
import zipfile


def payload(path):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive members")
        files = {name: archive.read(name) for name in names if name != ".signature.p7s"}
    # .NET 10's packer assigns a fresh opaque core-properties filename and
    # relationship ID on each invocation. Preserve the complete metadata bytes;
    # normalize only those generated IDs, never DLLs, nuspecs or metadata values.
    core = [name for name in files if re.fullmatch(r"package/services/metadata/core-properties/[a-f0-9]{32}\.psmdcp", name)]
    if core:
        if len(core) != 1:
            raise ValueError("Expected exactly one NuGet core-properties part")
        canonical = "package/services/metadata/core-properties/verified.psmdcp"
        if canonical in files:
            raise ValueError("Unexpected canonical metadata member")
        relationships = ET.fromstring(files["_rels/.rels"])
        matches = 0
        for relation in relationships:
            if relation.get("Type") == "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties":
                if relation.get("Target") != "/" + core[0] or not re.fullmatch(r"R[A-F0-9]{16}", relation.get("Id", "")):
                    raise ValueError("Invalid core-properties relationship")
                relation.set("Target", "/" + canonical)
                relation.set("Id", "verified-core-properties")
                matches += 1
        if matches != 1:
            raise ValueError("Expected one core-properties relationship")
        files[canonical] = files.pop(core[0])
        files["_rels/.rels"] = ET.tostring(relationships)
    return files


def verify(expected, published):
    if payload(expected) != payload(published):
        raise ValueError("Published NuGet payload differs from the tested package")


if __name__ == "__main__":
    verify(*sys.argv[1:])
