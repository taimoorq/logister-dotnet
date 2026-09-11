import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("verify", Path(__file__).with_name("verify-nuget-payload.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class NugetPayloadTests(unittest.TestCase):
    def test_only_generated_core_ids_are_normalized(self):
        with tempfile.TemporaryDirectory() as directory:
            a, b = Path(directory) / "a.zip", Path(directory) / "b.zip"

            def package(path, identity, metadata=b"unchanged metadata", target=None):
                core = f"package/services/metadata/core-properties/{identity * 32}.psmdcp"
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr(core, metadata)
                    archive.writestr("lib/sdk.dll", b"exact code")
                    archive.writestr("_rels/.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                        '<Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" '
                        f'Target="/{target or core}" Id="R{identity.upper() * 16}" /></Relationships>')
            package(a, "a")
            package(b, "b")
            module.verify(a, b)
            package(b, "b", metadata=b"changed metadata")
            with self.assertRaisesRegex(ValueError, "differs"):
                module.verify(a, b)
            package(b, "b", target="unexpected-target")
            with self.assertRaisesRegex(ValueError, "relationship"):
                module.verify(a, b)

    def test_repository_signature_is_the_only_ignored_member(self):
        with tempfile.TemporaryDirectory() as directory:
            expected, published = Path(directory) / "tested.zip", Path(directory) / "published.zip"
            with zipfile.ZipFile(expected, "w") as archive:
                archive.writestr("lib/sdk.dll", b"tested code")
            with zipfile.ZipFile(published, "w") as archive:
                archive.writestr("lib/sdk.dll", b"tested code")
                archive.writestr(".signature.p7s", b"repository signature")
            module.verify(expected, published)
            for members in ({"lib/sdk.dll": b"changed"}, {}, {"lib/sdk.dll": b"tested code", "extra": b"data"}):
                with zipfile.ZipFile(published, "w") as archive:
                    for name, data in members.items():
                        archive.writestr(name, data)
                with self.assertRaisesRegex(ValueError, "differs"):
                    module.verify(expected, published)


if __name__ == "__main__":
    unittest.main()
