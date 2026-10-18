"""Supplemental integration checks for the October follow-up.

Run from any directory. This validates source contracts and XAML only; it cannot
substitute for a .NET build or the Windows interaction checks in the release.
"""
from contextlib import redirect_stdout
from pathlib import Path
from xml.etree import ElementTree as ET
import argparse
import glob
import io
import json
import re
import runpy

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("root", nargs="?", type=Path,
                    default=Path(__file__).resolve().parents[2])
ROOT = parser.parse_args().root.resolve()
XAML = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
errors, warnings = [], []
checks = 0
assertions_by_category = {}
category = "source_wiring"


def check(condition, message):
    global checks
    checks += 1
    assertions_by_category[category] = assertions_by_category.get(category, 0) + 1
    if not condition:
        errors.append(message)


def local(tag):
    return tag.rsplit("}", 1)[-1]


def type_name(value):
    if not value:
        return None
    value = value.strip()
    return re.sub(r"^\{x:Type\s+([^}]+)\}$", r"\1", value)


# Reuse the existing project, handler, and service checks instead of maintaining
# a second copy. Both reports explicitly describe their static-only scope.
baseline_text = io.StringIO()
try:
    with redirect_stdout(baseline_text):
        runpy.run_path(str(Path(__file__).with_name("integration_static_check.py")),
                       init_globals={"SOURCE_ROOT": ROOT})
except SystemExit:
    pass
try:
    baseline = json.loads(baseline_text.getvalue())
except json.JSONDecodeError:
    baseline = {"errors": ["Existing integration script did not produce a JSON report"]}
errors.extend(baseline.get("errors", []))
warnings.extend(baseline.get("warnings", []))

# ResourceDictionary keys are scoped. Nested template resources can legitimately
# reuse a key; duplicate implicit styles in the same scope cannot.
resource_scopes = 0
resource_keys = 0
category = "resource_key_uniqueness"
for path in ROOT.rglob("*.xaml"):
    try:
        tree = ET.parse(path)
    except ET.ParseError:
        continue  # Already reported by the base checker.
    for scope in tree.iter():
        tag = local(scope.tag)
        if tag != "ResourceDictionary" and not tag.endswith(".Resources"):
            continue
        resource_scopes += 1
        seen = set()
        for node in scope:
            if local(node.tag) == "ResourceDictionary":
                continue  # Its own scope is checked separately.
            key = node.get(KEY)
            if key is not None:
                # An explicit x:Type key equals the corresponding implicit key.
                key = ("type", type_name(key)) if key.startswith("{x:Type ") else ("key", key)
            elif local(node.tag) == "Style" and node.get("TargetType"):
                key = ("type", type_name(node.get("TargetType")))
            elif local(node.tag) == "DataTemplate" and node.get("DataType"):
                key = ("template", type_name(node.get("DataType")))
            else:
                continue
            resource_keys += 1
            check(key not in seen, f"{path.relative_to(ROOT)}: duplicate resource key {key} in one scope")
            seen.add(key)

# Validate the new table styles against WPF inheritance rather than allowing
# arbitrary properties that XML itself accepts. This deliberately targets the
# modified styles; it is not a complete WPF metadata/reflection substitute.
framework = set("Width Height MinWidth MinHeight MaxWidth MaxHeight Margin HorizontalAlignment VerticalAlignment LayoutTransform FlowDirection Style ToolTip".split())
ui = set("Opacity Visibility IsEnabled IsHitTestVisible Focusable RenderTransform RenderTransformOrigin Clip ClipToBounds SnapsToDevicePixels UseLayoutRounding Cursor".split())
control = framework | ui | set("Background Foreground BorderBrush BorderThickness FontFamily FontSize FontStretch FontStyle FontWeight Padding Template HorizontalContentAlignment VerticalContentAlignment IsTabStop OverridesDefaultStyle FocusVisualStyle".split())
allowed = {
    "DataGrid": control | set("RowBackground AlternatingRowBackground HorizontalGridLinesBrush VerticalGridLinesBrush GridLinesVisibility EnableRowVirtualization EnableColumnVirtualization RowHeight ColumnHeaderHeight HeadersVisibility RowHeaderWidth AlternationCount SelectionMode SelectionUnit IsReadOnly CanUserAddRows CanUserDeleteRows CanUserResizeColumns CanUserResizeRows CanUserReorderColumns CanUserSortColumns AutoGenerateColumns".split()),
    "DataGridRow": control | set("IsSelected DetailsVisibility IsEditing Item ItemsPanel".split()),
    "DataGridCell": control | set("IsSelected IsReadOnly IsEditing Content ContentTemplate".split()),
    "DataGridColumnHeader": control | set("Content ContentTemplate SortDirection IsFrozen CanUserSort DisplayIndex".split()),
    "TextBlock": framework | ui | set("Background Foreground FontFamily FontSize FontStretch FontStyle FontWeight Padding Text TextWrapping TextTrimming TextAlignment LineHeight LineStackingStrategy".split()),
    "TextBox": control | set("Text IsReadOnly CaretBrush SelectionBrush AcceptsReturn AcceptsTab TextWrapping TextAlignment".split()),
    "CheckBox": control | set("IsChecked IsThreeState Content ContentTemplate".split()),
}
attached = {
    "ScrollViewer.CanContentScroll", "ScrollViewer.HorizontalScrollBarVisibility",
    "ScrollViewer.VerticalScrollBarVisibility", "VirtualizingPanel.IsVirtualizing",
    "VirtualizingPanel.VirtualizationMode", "VirtualizingPanel.ScrollUnit",
}
app_tree = ET.parse(ROOT / "App.xaml")
grid_styles = 0
category = "table_style_properties"
for style in app_tree.iter(XAML + "Style"):
    target = type_name(style.get("TargetType"))
    key = style.get(KEY, "")
    if target not in {"DataGrid", "DataGridRow", "DataGridCell", "DataGridColumnHeader"} and not key.startswith("ThemedDataGrid"):
        continue
    grid_styles += 1
    check(target in allowed, f"App.xaml: unsupported table Style.TargetType {target}")
    if target not in allowed:
        continue
    # Nested templates may target their own named elements. Only direct setters
    # and un-targeted Style.Triggers belong to this Style.TargetType.
    setters = list(style.findall(XAML + "Setter"))
    trigger_group = style.find(XAML + "Style.Triggers")
    if trigger_group is not None:
        setters += [n for n in trigger_group.iter(XAML + "Setter") if not n.get("TargetName")]
    for setter in setters:
        prop = setter.get("Property", "")
        check(prop in allowed[target] or prop in attached,
              f"App.xaml: {target} style has invalid or unsupported property {prop}")

# Production dependencies must be present in standalone regression projects.
# Project-local stubs can cover platform types, but not the pure sources added
# during this follow-up.
dependencies = {
    "DownloadService.cs": {"DownloadTargetLock.cs", "NativeDirectoryPolicy.cs"},
    "DownloadEndpoints.cs": {"DownloadTargetLock.cs"},
    "DownloadTransferPolicy.cs": {"DownloadTargetLock.cs"},
    "ConfigService.cs": {"ConfigMergeStore.cs"},
    "FolderService.cs": {"VersionInheritanceResolver.cs", "ArchiveImportSafety.cs"},
    "InstanceIntegrityService.cs": {"VersionInheritanceResolver.cs"},
    "DownloadQueueService.cs": {"DownloadQueueService.Recovery.cs", "DownloadRecoveryStore.cs", "DownloadNotificationPolicy.cs"},
    "CommandLineService.cs": {"LauncherDebugShortcuts.cs"},
    "PerformanceMetricsMath.cs": {"PerformanceMetrics.cs"},
    "Web2BrowserTool.xaml.cs": {"EmbeddedWebView.cs", "ExternalLinkService.cs"},
    "FeatureSearchBox.LayoutEditor.cs": {"FeatureSearchLayoutPolicy.cs"},
    "MainWindow.WinUiPreview.cs": {"PreviewNavigationState.cs"},
    "MemoryOptimizerService.cs": {"MemoryAllocationPolicy.cs", "MemoryAllocation.cs"},
    "MemoryAllocationPolicy.cs": {"MemoryAllocation.cs"},
    "LauncherService.cs": {"LegacyMinecraftCompatibility.cs", "LegacyAssetLayout.cs", "VersionInheritanceResolver.cs", "LaunchArgumentRulePolicy.cs", "JavaExecutableArchitecture.cs", "NativeDirectoryPolicy.cs"},
    "NativeDirectoryPolicy.cs": {"VersionManifest.cs", "JavaExecutableArchitecture.cs"},
    "VersionRepairPlan.cs": {"VersionInheritanceResolver.cs"},
    "JavaService.cs": {"JavaExecutableArchitecture.cs", "DownloadQueueService.cs", "JavaProcessProbe.cs", "JavaVersionOutputPolicy.cs"},
    "AiAssistantService.cs": {"AiAgentPlanRecovery.cs", "AiChatContextWindow.cs", "AiResponseLimits.cs"},
    "AiStreamingCompletion.cs": {"AiResponseLimits.cs"},
    "AiAssistantService.Workbench.cs": {"AiPlanProtocol.cs"},
    "AiControlProtocol.cs": {"AiPlanProtocol.cs"},
    "GameProcessInfo.cs": {"LegacyTouchWorldEvidence.cs"},
    "LegacyTouchInputBridge.cs": {"LegacyTouchMotion.cs", "LegacyTouchWorldEvidence.cs", "LegacyTouchCursorEvidence.cs"},
}
test_projects = 0
category = "standalone_project_dependencies"
for path in (ROOT / "Tests").rglob("*.csproj"):
    test_projects += 1
    tree = ET.parse(path)
    sources = {p.resolve() for p in path.parent.glob("*.cs")}
    for node in tree.findall(".//Compile"):
        include = node.get("Include", "")
        if "$" not in include:
            sources.update(Path(p).resolve() for p in glob.glob(str(path.parent / include.replace("\\", "/")), recursive=True))
    names = {p.name for p in sources}
    for source, required in dependencies.items():
        if source in names:
            for dependency in required:
                check(dependency in names, f"{path.relative_to(ROOT)}: {source} needs {dependency}")

# Changed XAML event handlers (including EventSetter.Handler) must exist in the
# corresponding code-behind/partials. XML well-formedness alone does not check it.
events = set("Click Checked Unchecked SelectionChanged Loaded Unloaded PreviewKeyDown KeyDown TextChanged MouseDown MouseUp MouseMove MouseEnter MouseLeave SizeChanged ScrollChanged ValueChanged IsVisibleChanged GotFocus LostFocus DragEnter DragOver DragLeave Drop MouseLeftButtonDown ContextMenuOpening".split())
target_files = [ROOT / "App.xaml", ROOT / "Views" / "AiAssistantSettingsPanel.xaml", ROOT / "Views" / "SettingsPage.xaml", ROOT / "Views" / "AiAssistantPanel.xaml",
                ROOT / "Views" / "MainWindow.xaml", ROOT / "Views" / "FeatureSearchBox.xaml",
                ROOT / "Views" / "ServerNetworkGuideWindow.xaml", ROOT / "Views" / "Tools" / "Web2BrowserTool.xaml",
                ROOT / "Views" / "PerformanceOverlayWindow.xaml"]
handlers = 0
category = "xaml_handler_names"
for path in target_files:
    tree = ET.parse(path)
    code = "\n".join(p.read_text(encoding="utf-8") for p in path.parent.glob(path.stem + "*.cs"))
    for node in tree.iter():
        for attr, handler in node.attrib.items():
            is_event = local(attr) in events or local(node.tag) == "EventSetter" and local(attr) == "Handler"
            if not is_event or handler.startswith("{"):
                continue
            handlers += 1
            check(bool(re.search(r"\b" + re.escape(handler) + r"\s*\(", code)),
                  f"{path.relative_to(ROOT)}: missing handler {handler}")

# Every statically mapped imported plugin call must name an actual host API.
# This catches obsolete or invented method names without executing any plugin.
plugin_root = ROOT / "Services" / "Plugins"
permission_source = (plugin_root / "PluginUpgradePermissions.cs").read_text(encoding="utf-8")
permission_map = permission_source.split("internal static IEnumerable<string> ForMember", 1)[1].split("internal static string? ForEvent", 1)[0]
plugin_sources = {path: path.read_text(encoding="utf-8") for path in plugin_root.glob("*.cs")}
mapped_api_members = 0
category = "plugin_api_names"
mapped_groups = re.findall(r'\("([^\"]+)", ([^\n]*?)\)', permission_map)
mapped_groups += re.findall(r'type == "([^\"]+)" && method is \(([^)]*)\)', permission_map)
for target, methods in mapped_groups:
    candidates = [text for text in plugin_sources.values()
                  if re.search(r"\b(?:class|interface)\s+" + re.escape(target) + r"\b", text)]
    for method in re.findall(r'"([^\"]+)"', methods):
        mapped_api_members += 1
        check(any(re.search(r"\b" + re.escape(method) + r"\s*(?:<[^>]*>)?\s*\(", text) for text in candidates),
              f"PluginUpgradePermissions: {target}.{method} is not a production API")
check("reader.MethodSpecifications" not in permission_source,
      "PluginUpgradePermissions: MetadataReader has no MethodSpecifications collection")
check("GetTableRowCount(TableIndex.MethodSpec)" in permission_source and "MetadataTokens.MethodSpecificationHandle" in permission_source,
      "PluginUpgradePermissions: generic event scan needs the ECMA-335 MethodSpec table API")

# These checks verify that the changed components remain wired to the common
# policy paths. Their behavior still needs the linked C# tests and Windows QA.
control_source = (ROOT / "Services" / "AiControlProtocol.cs").read_text(encoding="utf-8")
category = "source_wiring"
plan_source = (ROOT / "Services" / "AiPlanProtocol.cs").read_text(encoding="utf-8")
check('new("ACCESS", display, plan.Request, plan.Error)' in control_source,
      "Text Plan must be normalized to the legacy ACCESS/update_task dispatch path")
check('action = "update_task"' in plan_source and "AiControlProtocol.ValidateAccessArguments(request)" in plan_source,
      "Text Plan must produce only validated update_task requests")
check('payload.Contains("<<XCL2_", StringComparison.Ordinal)' in plan_source,
      "Text Plan must reject nested or mixed control blocks")

settings_source = (ROOT / "Views" / "SettingsPage.xaml.cs").read_text(encoding="utf-8")
color_source = (ROOT / "Views" / "SettingsPage.ColorSelection.cs").read_text(encoding="utf-8")
fingerprint = settings_source.split("private string BuildSettingsUiFingerprint()", 1)[1].split("private void RollbackAutoSave()", 1)[0]
check("FindVisualChildren" not in fingerprint and "FindSettingsControls" in fingerprint,
      "Settings fingerprint must use stable logical controls, not transient template/scroll visuals")
check("_committedCustomAccentColor" in fingerprint and "_pendingSkinCandidates" in fingerprint,
      "Settings fingerprint must include committed accent and candidate edits")
check("SettingsSearchBox" in fingerprint and "AccentRSlider" in fingerprint,
      "Settings fingerprint must exclude search and uncommitted RGB browsing values")
check("LogicalTreeHelper.GetChildren" in color_source,
      "Settings control traversal must remain independent of scroll virtualization")

touch_source = (ROOT / "Views" / "TouchOverlayWindow.xaml.cs").read_text(encoding="utf-8")
mode_source = (ROOT / "Services" / "TouchInputModePolicy.cs").read_text(encoding="utf-8")
check("_inputTimer.Tick += FlushPendingInput" in touch_source and "_inputTimer.Tick -= FlushPendingInput" in touch_source
      and "CompositionTarget.Rendering" not in touch_source and "_inputTimer.Stop();" in touch_source,
      "Legacy input dispatch must be independent of WPF rendering and cleaned up on teardown")
check("worldJoined && cursorCentered" in mode_source,
      "Hidden unconfined cursor alone must not be accepted as a joined world")

report = {
    "kind": "static source validation; no compilation or Windows runtime verification",
    "baseline": {k: v for k, v in baseline.items() if k not in {"errors", "warnings"}},
    "checks": checks,
    "assertions_by_category": assertions_by_category,
    "non_resource_assertions": checks - resource_keys,
    "count_meaning": "Source assertions only, not executed feature or behavioral tests. Resource-key checks are counted separately.",
    "compiler_diagnostics": {"status": "not_run", "reason": "This script does not invoke a C# compiler."},
    "warning_scope": "Static script diagnostics only; an empty list does not mean the compiler or runtime has no warnings.",
    "resource_scopes": resource_scopes,
    "resource_keys": resource_keys,
    "table_styles_checked": grid_styles,
    "standalone_test_projects": test_projects,
    "changed_xaml_handlers": handlers,
    "mapped_plugin_api_members": mapped_api_members,
    "errors": errors,
    "warnings": warnings,
}
print(json.dumps(report, ensure_ascii=False, indent=2))
raise SystemExit(bool(errors))
