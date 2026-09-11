import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("audit", Path(__file__).with_name("check-dependency-audit.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def test_clean_and_transitive_vulnerability(self):
        audit.verify({"version": 1, "projects": [{"path": "test"}]})
        with self.assertRaisesRegex(ValueError, "transitive-dependency"):
            audit.verify({"version": 1, "projects": [{"frameworks": [{"transitivePackages": [{"id": "transitive-dependency", "vulnerabilities": [{"severity": "Low"}]}]}]}]})
        with self.assertRaisesRegex(ValueError, "schema"):
            audit.verify({"error": "provider unavailable"})


if __name__ == "__main__":
    unittest.main()
