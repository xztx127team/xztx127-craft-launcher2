#!/usr/bin/env python3
"""Source-only contract checks. Does not replace Windows compilation or UI acceptance."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
xaml = ROOT / 'Views/SettingsPage.xaml'
tree = ET.parse(xaml)
name_key = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
controls = {node.attrib[name_key]: node for node in tree.iter() if name_key in node.attrib}
source = (ROOT / 'Views/SettingsPage.Ux30.cs').read_text(encoding='utf-8-sig')
main = (ROOT / 'Views/SettingsPage.xaml.cs').read_text(encoding='utf-8-sig')
performance = (ROOT / 'Views/SettingsPage.Performance.cs').read_text(encoding='utf-8-sig')
configs = '\n'.join(path.read_text(encoding='utf-8-sig') for path in (ROOT/'Models').glob('AppConfig*.cs'))
checks = []
def check(condition, description):
    assert condition, description
    checks.append(description)

bindings = re.findall(r'\("(\w+)", "(\w+)"\)', source.split('private sealed record UxResetEntry')[0])
check(len(bindings) >= 130, 'broad explicit preference reset coverage')
for control, prop in bindings:
    check(control in controls, f'reset control exists: {control}')
    check(re.search(r'\b' + re.escape(prop) + r'\s*\{\s*(?:get|$)', configs, re.M) is not None,
          f'reset default property exists: {prop}')
check(len(set(name for name, prop in bindings)) == len(bindings), 'reset bindings have unique controls')
for excluded in ('GuestModeCheck', 'UseMachineWideRegistryCheck', 'CustomAccentColorBox', 'SettingsSearchBox', 'FeatureSearchEditCheck'):
    check(excluded not in {name for name, _ in bindings}, f'no automatic reset of session/permission/draft control: {excluded}')
check('ShowConfirm' in source[source.index('private void ConfirmUxGroupReset'):source.index('private void ResetUxEntries')], 'group reset confirms before applying')
check('_uxResetInProgress' in main[main.index('private void OnSettingsEdited'):main.index('private void MarkCurrentUiAsSaved')], 'composed group reset is one settings edit')
check('case TextBox text:' in source and 'ApplicationCommands.Copy' in source and 'ApplicationCommands.Paste' in source, 'editable text menus retain copy and paste')
search = main[main.index('private static void CollectSettingsSearchMatches'):main.index('\n    #endregion', main.index('private static void CollectSettingsSearchMatches'))]
check('LogicalTreeHelper.GetChildren' in search, 'closed expander settings are indexed logically')
check('DropShadowEffect' not in search, 'search highlight does not add blurred duplicate shadows')
check('AdornerLayer' in search and 'DrawRoundedRectangle' in source, 'search highlight uses crisp outline overlay')
check('expander.IsExpanded = true' in main, 'explicit result navigation opens containing section')
check(any(node.tag.endswith('AdornerDecorator') for node in tree.iter()), 'search outline has an adorner layer inside the scroll viewport')
for transient in ('SettingsSearchBox', 'CustomAccentColorBox', 'FeatureSearchEditCheck', 'AccentRSlider', 'AccentGSlider', 'AccentBSlider'):
    capture = source[source.index('private Dictionary<string, UxSettingValue> CaptureUxSettingsValues'):source.index('private List<string> GetUxPendingChanges')]
    check(f'ReferenceEquals' in capture and transient in capture, f'summary excludes transient input: {transient}')
check('Add(text, MeaningfulInputValue(text), "文本已修改")' in source, 'summary does not expose text field contents')
check('UxExternalConfigSaved' in source and 'nameof(AppConfig.DownloadSpeedLimitKBps)' in source and 'nameof(AppConfig.RememberTaskResults)' in source, 'scoped menu saves synchronize only changed central fields')
check('OpacityPercent = _pendingFeatureSearchLayout.OpacityPercent' in source, 'geometry reset preserves an unrelated opacity draft')
check('SyncUxExternallySavedControls(controls)' in performance, 'external overlay saves update summary baseline without replacing drafts')
check('SaveUxOptions(cfg);' in main and 'InitializeUxSettings();' in main and 'MarkUxValuesAsSaved();' in main, 'preferences and summary are hooked into existing save lifecycle')
check('Visibility="Collapsed"' in controls['UxChangeSummaryButton'].attrib.get('Visibility','') or controls['UxChangeSummaryButton'].attrib.get('Visibility') == 'Collapsed', 'change summary entry stays hidden before edits')
for group in ('UxHabitsSettingsGroup',):
    check(controls[group].attrib.get('IsExpanded', 'False') == 'False', 'low-frequency preference group starts collapsed')
check('WrapPanel' not in ET.tostring(controls['UxHabitsSettingsGroup'],encoding='unicode'), 'new preference group uses aligned vertical rows, no cramped wrapping action row')
for node in controls['UxHabitsSettingsGroup'].iter():
    if node.tag.endswith('Button'):
        check('Width' not in node.attrib and node.attrib.get('HorizontalAlignment') == 'Left', 'preference action button sizes to one line of content')
check('RecentToolTabs.Clear()' in source and 'InstanceUxService.ClearRecent' in source, 'explicit recent history clear covers both instances and tools')
check('VersionSelectPage.ClearRememberedBrowsePositions()' in source, 'turning off browser history clears old session view state')
check('if (!cfg.RememberTaskResults) TaskResultHistoryService.Clear();' in source, 'task result opt-out clears retained session results')
print(json.dumps({'scope':'source-only settings UX contracts','checks':len(checks),'reset_bindings':len(bindings),'status':'passed'},ensure_ascii=False,indent=2))
