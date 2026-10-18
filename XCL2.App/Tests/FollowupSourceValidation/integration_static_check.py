from pathlib import Path
from xml.etree import ElementTree as ET
import json, re, glob, os

ROOT = Path(globals().get('SOURCE_ROOT') or os.environ.get('XCL2_SOURCE_ROOT') or
            Path(__file__).resolve().parents[1] / 'source' / 'XCL2.App')
errors=[]; warnings=[]; projects=0; xamls=0; event_handlers=0; named_controls=0; contracts=0
for path in ROOT.rglob('*.csproj'):
    projects += 1
    try: tree=ET.parse(path)
    except ET.ParseError as exc:
        errors.append(f'{path.relative_to(ROOT)}: XML: {exc}'); continue
    includes=[]
    for item in tree.findall('.//Compile'):
        include=item.get('Include')
        if not include or '$(' in include: continue
        resolved=str(path.parent / include.replace('\\','/'))
        matches=glob.glob(resolved,recursive=True)
        if not matches: errors.append(f'{path.relative_to(ROOT)}: missing Compile {include}')
        includes.extend(str(Path(m).resolve()) for m in matches)
    if len(includes)!=len(set(includes)):
        errors.append(f'{path.relative_to(ROOT)}: duplicate explicit Compile include')
    names={Path(i).name for i in includes}
    if 'AiAssistantModels.cs' in names:
        for required in ('AiAgentSettings.cs','AiCommandPermissions.cs','AiAttachmentModels.cs'):
            if required not in names: errors.append(f'{path.relative_to(ROOT)}: needs {required}')
    if 'AiAssistantService.cs' in names and 'AiAttachmentService.cs' not in names:
        errors.append(f'{path.relative_to(ROOT)}: needs AiAttachmentService.cs')

events={'Click','Checked','Unchecked','SelectionChanged','Loaded','Unloaded','PreviewKeyDown','KeyDown','TextChanged','MouseDown','MouseUp','MouseMove','MouseEnter','MouseLeave','SizeChanged','ScrollChanged','ValueChanged','IsVisibleChanged','GotFocus','LostFocus','DragEnter','DragOver','DragLeave','Drop','MouseLeftButtonDown','ContextMenuOpening'}
for path in ROOT.rglob('*.xaml'):
    xamls+=1
    try: tree=ET.parse(path)
    except ET.ParseError as exc:
        errors.append(f'{path.relative_to(ROOT)}: XML: {exc}'); continue
    if path.name not in {'AiAssistantPanel.xaml','AiAssistantSettingsPanel.xaml','SettingsPage.xaml',
                          'MainWindow.xaml','FeatureSearchBox.xaml','ServerNetworkGuideWindow.xaml',
                          'Web2BrowserTool.xaml','PerformanceOverlayWindow.xaml'}: continue
    scopes=[set()]
    def names(node, scope):
        global named_controls
        if node.tag.rsplit('}',1)[-1] in {'DataTemplate','ControlTemplate','ItemsPanelTemplate'}:
            scope=set()
        name=node.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name')
        if name:
            named_controls += 1
            if name in scope: errors.append(f'{path.relative_to(ROOT)}: duplicate x:Name in namescope: {name}')
            scope.add(name)
        for child in node: names(child,scope)
    names(tree.getroot(),scopes[0])
    stem=path.stem
    code='\n'.join(p.read_text() for p in path.parent.glob(stem+'*.cs'))
    for node in tree.iter():
        for attr, handler in node.attrib.items():
            if attr not in events or handler.startswith('{'): continue
            event_handlers += 1
            if not re.search(r'\b'+re.escape(handler)+r'\s*\(',code):
                errors.append(f'{path.relative_to(ROOT)}: missing event handler {handler}')

# Check call contracts for newly introduced platform-independent services.
for service in ('AiAttachmentService','AiCommandPermissionService','AiAgentAnimationService'):
    definition=ROOT/'Services'/(service+'.cs')
    if not definition.exists():
        errors.append('Missing new service: '+service); continue
    source=definition.read_text()
    defined=set(re.findall(r'\b(?:public|internal)\s+(?:static\s+)?(?:[\w<>?,]+\s+)+(\w+)\s*\(',source))
    defined.update(re.findall(r'\bpublic\s+(?:const|static readonly)\s+\w+\s+(\w+)',source))
    defined.update(re.findall(r'\bpublic\s+static\s+event\s+[^;]+?\s+(\w+)\s*;',source))
    for file in list((ROOT/'Views').glob('*.cs'))+list((ROOT/'Services').glob('*.cs')):
        for method in set(re.findall(r'\b'+service+r'\.(\w+)\b',file.read_text())):
            contracts+=1
            if method not in defined: errors.append(f'{file.relative_to(ROOT)}: missing service contract {service}.{method}')
http_stub=(ROOT/'Tests/AiHttpRegression/Stubs.cs').read_text()
http_service=(ROOT/'Services/AiAssistantService.cs').read_text()
if 'AiComputerControlService.BuildPrompt' in http_service and not re.search(r'class\s+AiComputerControlService[\s\S]*?BuildPrompt\s*\(',http_stub):
    errors.append('AiHttpRegression: needs platform computer prompt stub')
contracts+=1

report={'kind':'static validation; no build or runtime verification', 'projects':projects,'xaml_files':xamls,'target_event_handlers':event_handlers,'target_x_names':named_controls,'new_service_contracts':contracts,'errors':errors,'warnings':warnings}
print(json.dumps(report,ensure_ascii=False,indent=2))
raise SystemExit(bool(errors))
