#!/usr/bin/env python3
"""Check catalog routes and exact source-backed XAML/runtime settings names without Windows."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from generate_catalog import APP, PAGES, X, build, experience_preferences


def validate(entries):
    preferences = experience_preferences()
    experience_code = (APP / "Views/SettingsPage.AprilFools.cs").read_text(encoding="utf-8-sig")
    assert 'Name = "AprilFoolsCodeButton"' in experience_code
    assert 'Content = "输入彩蛋码"' in experience_code
    dynamic_names = {item["Control"] for item in preferences} | {"ExperienceDetailsGroup", "AprilFoolsCodeButton"}
    assert len(entries) > 400
    assert len({entry["Id"] for entry in entries}) == len(entries)
    routes = {page[0]: file for file, page in PAGES.items()}
    routes.update(worlds="WorldArchivePage", api="PluginApiDocumentationPage")
    assert all(entry["PageKey"] in routes for entry in entries)
    trees = {page[0]: ET.parse(APP / "Views" / (file + ".xaml")).getroot() for file, page in PAGES.items()}
    for entry in entries:
        assert entry["Title"] and not entry["Title"].startswith("{"), entry["Id"]
        if entry.get("TargetName"):
            names = {element.get(X + "Name") for element in trees[entry["PageKey"]].iter()}
            if entry["PageKey"] == "settings": names |= dynamic_names
            assert entry["TargetName"] in names, entry["Id"]
        if entry.get("SectionKey"):
            section_names = {element.get(X + "Name") for element in trees[entry["PageKey"]].iter()}
            if entry["PageKey"] == "settings": section_names.add("ExperienceDetailsGroup")
            assert entry["SectionKey"] in section_names, entry["Id"]
    tabs = [entry for entry in entries if entry["Id"].startswith("toolbox.tab.")]
    assert [entry["TabIndex"] for entry in tabs] == list(range(36))
    assert any(entry.get("TargetName") == "LaunchGameBtn" and entry["PageKey"] == "main" for entry in entries)
    assert any(entry.get("TargetName") == "CatVersion" and "下载游戏" in entry["Keywords"] for entry in entries)
    assert sum(entry["PageKey"] == "settings" for entry in entries) >= 100
    assert any(entry.get("TargetName") == "GlobalWindowOpacitySlider" for entry in entries)
    assert any(entry.get("TargetName") == "ShowFeatureSearchCheck" and entry["PageKey"] == "settings" for entry in entries)
    assert any(entry.get("TargetName") == "FeatureSearchEditCheck" for entry in entries)
    assert any(entry.get("TargetName") == "FeatureSearchOpacitySlider" and entry["Title"] == "搜索框不透明度" for entry in entries)
    assert any(entry.get("TargetName") == "TaskTrackingButton" for entry in entries)
    assert any(entry["PageKey"] == "ai" and entry["Title"] == "已完成任务" for entry in entries)
    assert any(entry["Id"] == "worlds.tab.3" and entry["Title"] == "二维地图" and entry["TabIndex"] == 3 for entry in entries)
    map_code = (APP / "Views/WorldMapPanel.cs").read_text(encoding="utf-8-sig")
    for entry in entries:
        if entry["Id"].startswith("worlds.map."):
            assert entry["TabIndex"] == 3 and '"' + entry["TargetText"] + '"' in map_code, entry["Id"]
    by_id = {entry["Id"]: entry for entry in entries}
    for preference in preferences:
        entry = by_id["settings." + preference["Control"]]
        assert entry["TargetName"] == preference["Control"] and entry["Title"] == preference["Label"], entry["Id"]
        assert entry["SectionKey"] == "ExperienceDetailsGroup", entry["Id"]
        assert entry["SectionTitle"] == "体验细节 › " + preference["Group"], entry["Id"]
        assert preference["Property"] in entry["Keywords"], entry["Id"]
    assert by_id["settings.ExperienceDetailsGroup"]["TargetName"] == "ExperienceDetailsGroup"
    code_entry = by_id["settings.AprilFoolsCodeButton"]
    assert code_entry["TargetName"] == "AprilFoolsCodeButton" and code_entry["Title"] == "输入彩蛋码"
    assert code_entry["SectionKey"] == "ExperienceDetailsGroup"
    assert code_entry["SectionTitle"] == "体验细节 › 趣味与彩蛋"
    assert "跳过" in code_entry["Keywords"] and "xztx127" in code_entry["Keywords"]
    assert "settings.Experience_AprilFoolsExitChallengeEnabled" not in by_id
    settings_code = (APP / "Views/SettingsPage.xaml.cs").read_text(encoding="utf-8-sig")
    local_search = settings_code[settings_code.index("private static void CollectSettingsSearchMatches("):]
    assert "LogicalTreeHelper.GetChildren(root)" in local_search
    assert "CheckBox check => check.Content as string" in local_search
    assert "TextBlock block => block.Text" in local_search
    assert "Expander expander => expander.Header as string" in local_search
    navigation = (APP / "Views/MainWindow.SearchNavigation.cs").read_text(encoding="utf-8-sig")
    assert "EnumerateFeatureSearchTree(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name)" in navigation
    assert "LogicalTreeHelper.GetChildren(current)" in navigation
    assert "ancestor is Expander expander && !expander.IsExpanded" in navigation
    return len(preferences)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--from-source", action="store_true", help="Validate planned source-built routes without writing or checking the catalog file")
    args = parser.parse_args()
    if args.from_source:
        entries = build()
    else:
        path = APP / "Resources/Data/FeatureSearchCatalog.json"
        entries = json.loads(path.read_text(encoding="utf-8"))
        assert entries == build(), "generated search catalog is stale"
    count = validate(entries)
    scope = "planned source-built" if args.from_source else "generated"
    print(f"PASS: {len(entries)} {scope} catalog destinations; source-backed names ({count} runtime settings), routes, panels and 36 tab indices verified. No Windows execution.")


if __name__ == "__main__":
    main()
