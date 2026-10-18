#!/usr/bin/env python3
"""Dependency-free source/layout checks; not a WPF runtime or C# compilation test."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

app = Path(__file__).resolve().parents[2]
x = "{http://schemas.microsoft.com/winfx/2006/xaml}"
p = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
tree = ET.parse(app / "Views/MainWindow.xaml").getroot()
named = {e.get(x + "Name"): e for e in tree.iter() if e.get(x + "Name")}
source = (app / "Views/MainWindow.WinUiPreview.cs").read_text(encoding="utf-8-sig")
plugin = (app / "Views/MainWindow.PluginUi.cs").read_text(encoding="utf-8-sig")
checks = {}
def check(name, value): checks[name] = bool(value)

check("preview category bar is initially collapsed", named["PreviewCategoryBar"].get("Visibility") == "Collapsed")
check("top categories wrap rather than overflow", named["PreviewCategoryTabs"].tag == p + "WrapPanel")
buttons = list(named["PreviewCategoryTabs"])
check("five top categories dispatch through the shared handler", {e.get("CommandParameter") for e in buttons} == {"launch", "downloads", "multiplayer", "tools", "settings"} and all(e.get("Click") == "PreviewCategory_Click" for e in buttons))
check("sidebar and main content share second body row", all(named[n].get("Grid.Row") == "1" for n in ("SidebarAreaBorder", "ContentAreaBorder")))
entries = list(named["NavButtonsPanel"])
expected = {"home", "instances", "downloads", "multiplayer", "worlds", "mods", "servers", "toolbox", "bedrock", "about", "accounts", "settings", "more", "logs", "ai", "experimental", "plugins"}
check("all original navigation entries have independent category wrappers", {e.get("Tag") for e in entries} == expected and all(e.tag == p + "Border" and len(e) == 1 and e[0].tag == p + "Button" and e[0].get("Click") for e in entries))
check("category selection raises original button click", "target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))" in source)
check("actual page observer restores cancelled navigation", "_previewNavigation.ObservePage(active)" in source and "UpdatePreviewNavigationSelection();" in source)
check("category eligibility uses actual feature visibility and permission gates", "item.Button.Visibility == Visibility.Visible && item.Button.IsEnabled" in source)
check("plugin contributions keep native creation and permission checks", "definition.CreateContent" in plugin and "Permissions.Demand(PluginPermissionNames.UiNavigation)" in plugin)
check("plugin entries use the same category wrappers and stable page key", "AddPreviewPluginNavigationButton(button, entry.Id)" in plugin and "RemovePreviewPluginNavigationButton(button)" in plugin and 'Tag = "plugin:" + id' in source)
check("plugin page selection refreshes after async creation", "_activePluginPage = entry.Id; UpdatePreviewNavigationSelection();" in plugin)
check("plugin visibility is not overwritten by category filtering", "button.Visibility =" not in source)
for name in ("OverlayRoot", "DragHintLayer", "ToastHost", "ActionToastHost", "BrightnessOverlay", "AiFloatingButton", "AprilFoolsFlagButton"):
    check("both navigation rows covered: " + name, named[name].get("Grid.RowSpan") == "2")
check("switching layouts retains existing page instance", not re.search(r"MainContent\.Content\s*=|\bnew\s+(?:HomePage|SettingsPage|VersionSelectPage)\b", source))
failed = [name for name, success in checks.items() if not success]
report = {"kind": "source_layout_contracts", "checks": checks, "passed": len(checks) - len(failed), "failed": failed, "CSharp_compilation": "NOT_RUN", "Windows_visual_and_interaction_testing": "NOT_RUN"}
(Path(__file__).parent / "layout-validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({"passed": report["passed"], "failed": failed}, ensure_ascii=False))
raise SystemExit(1 if failed else 0)
