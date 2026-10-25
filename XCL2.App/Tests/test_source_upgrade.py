"""Regression tests for user-data preservation and source-upgrade rollback."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("upgrade_source", Path(__file__).resolve().parents[1] / "tools/upgrade_source.py")
upgrade = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upgrade)


class SourceUpgradeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source, self.target = self.root / "new", self.root / "old"
        for root in (self.source, self.target):
            self.put(root, "XCL2.App.csproj", "<Project />")
            self.put(root, "App.xaml", "original")
            self.put(root, "App.xaml.cs", "original")
        self.put(self.source, "App.xaml", "new xaml")
        self.put(self.source, "App.xaml.cs", "new code")
        self.put(self.source, "Views/InstalledVersionComponentsDialog.xaml.cs", "new partial")
        self.put(self.target, "Views/InstalledVersionComponentsDialog.cs", "old class")
        self.put(self.target, "Views/VersionSelectPage.Components.cs", "old duplicate handler")
        self.put(self.target, ".minecraft/saves/My World/level.dat", "world data")
        self.put(self.target, "json/config.json", "configuration")
        self.put(self.target, "Views/local-settings.json", "custom data")

    def tearDown(self):
        self.temp.cleanup()

    def put(self, root, relative, text):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def contents(self, root):
        return {str(p.relative_to(root)): p.read_bytes() for p in root.rglob("*") if p.is_file()}

    def test_plan_does_not_change_files(self):
        before = self.contents(self.target)
        _, _, copies, obsolete = upgrade.plan_update(self.source, self.target)
        self.assertEqual(before, self.contents(self.target))
        self.assertEqual({str(p) for p in obsolete}, {"Views/InstalledVersionComponentsDialog.cs", "Views/VersionSelectPage.Components.cs"})
        self.assertIn(Path("App.xaml"), copies)

    def test_success_quarantines_old_source_preserves_user_data_and_is_idempotent(self):
        before = self.contents(self.target)
        plan = upgrade.plan_update(self.source, self.target)
        backup = upgrade.perform_update(*plan)
        self.assertEqual(backup.parent, self.target.parent)
        self.assertFalse((self.target / "Views/InstalledVersionComponentsDialog.cs").exists())
        self.assertEqual((backup / "original/Views/InstalledVersionComponentsDialog.cs").read_text(), "old class")
        self.assertEqual((self.target / "Views/InstalledVersionComponentsDialog.xaml.cs").read_text(), "new partial")
        for name in (".minecraft/saves/My World/level.dat", "json/config.json", "Views/local-settings.json"):
            self.assertEqual((self.target / name).read_bytes(), before[name])
        self.assertEqual(upgrade.plan_update(self.source, self.target)[2:], ([], []))

    def test_mid_install_failure_restores_every_original(self):
        before = self.contents(self.target)
        real_copy = upgrade.atomic_copy

        def inject(source, target):
            if source == self.source / "App.xaml.cs":
                raise OSError("injected disk-write failure")
            return real_copy(source, target)

        with mock.patch.object(upgrade, "atomic_copy", side_effect=inject):
            with self.assertRaisesRegex(OSError, "injected"):
                upgrade.perform_update(*upgrade.plan_update(self.source, self.target))
        self.assertEqual(before, self.contents(self.target))

    def test_refuses_overlapping_project_roots(self):
        with self.assertRaises(ValueError):
            upgrade.plan_update(self.source, self.source)
        inner = self.source / "nested"
        self.put(inner, "XCL2.App.csproj", "<Project />")
        with self.assertRaises(ValueError):
            upgrade.plan_update(self.source, inner)

    def test_refuses_junction_or_symlink_in_source_tree(self):
        external = self.root / "external"
        external.mkdir()
        try:
            (self.target / "Views/escape").symlink_to(external, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        with self.assertRaisesRegex(ValueError, "symlink/junction"):
            upgrade.plan_update(self.source, self.target)


if __name__ == "__main__":
    unittest.main()
