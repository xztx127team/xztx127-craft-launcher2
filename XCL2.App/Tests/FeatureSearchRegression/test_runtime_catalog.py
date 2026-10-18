#!/usr/bin/env python3
"""Source-only tests for real runtime metadata routes. Does not construct WPF controls."""
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import generate_catalog as generator
from validate_catalog import validate


class RuntimeSettingCatalogTests(unittest.TestCase):
    def test_all_explicit_controls_route_to_the_real_experience_group(self):
        preferences = generator.experience_preferences()
        entries = generator.build()
        self.assertEqual(validate(entries), len(preferences))
        names = {item["Control"] for item in preferences}
        self.assertIn("Experience_Ux50SearchHistory", names)
        self.assertIn("Experience_EnableAprilFoolsEasterEggs", names)
        self.assertNotIn("Experience_Ux50TableHiddenColumns", names)
        self.assertNotIn("Experience_Ux50SearchHistoryItems", names)
        self.assertEqual(entries, generator.build())

    def test_unannotated_storage_name_is_rejected(self):
        entries = [dict(entry) for entry in generator.build()]
        target = next(entry for entry in entries if entry.get("TargetName") == "Experience_Ux50SearchHistory")
        target["TargetName"] = "Experience_Ux50TableHiddenColumns"
        with self.assertRaises(AssertionError):
            validate(entries)

    def test_label_cannot_diverge_from_runtime_metadata(self):
        entries = [dict(entry) for entry in generator.build()]
        target = next(entry for entry in entries if entry.get("TargetName") == "Experience_Ux50SearchHistory")
        target["Title"] = "invented setting title"
        with self.assertRaises(AssertionError):
            validate(entries)

    def test_escaped_labels_and_runtime_naming_contract(self):
        # Minimal explicit source fixture under the writable project directory.
        runtime = (generator.APP / "Views/SettingsPage.Experience.cs").read_text(encoding="utf-8-sig")
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as directory:
            root = Path(directory)
            (root / "Views").mkdir()
            (root / "Models").mkdir()
            runtime_path = root / "Views/SettingsPage.Experience.cs"
            runtime_path.write_text(runtime, encoding="utf-8")
            (root / "Views/SettingsPage.xaml.cs").write_text("InitializeExperienceSettings(cfg);", encoding="utf-8")
            (root / "Models/AppConfig.Test.cs").write_text(r'''
public enum TestMode { One, Two }
public partial class AppConfig {
    [ExperiencePreference("Example", "Quoted \"label\"", Description = "description [details]")]
    public bool RealPreference { get; set; }
    [ExperiencePreference("Example", "Mode")]
    public TestMode ActualMode { get; set; }
    public string StorageWithoutAnnotation { get; set; }
}
''', encoding="utf-8")
            with patch.object(generator, "APP", root):
                preferences = generator.experience_preferences()
                self.assertEqual(len(preferences), 2)
                actual = next(item for item in preferences if item["Property"] == "RealPreference")
                self.assertEqual(actual["Label"], 'Quoted "label"')
                self.assertEqual(actual["Description"], 'description [details]')
                runtime_path.write_text(runtime.replace('control.Name = "Experience_" + property.Name;', 'control.Name = "Changed_" + property.Name;'), encoding="utf-8")
                with self.assertRaises(ValueError):
                    generator.experience_preferences()


if __name__ == "__main__":
    unittest.main()
