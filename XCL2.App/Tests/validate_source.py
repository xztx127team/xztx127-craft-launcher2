#!/usr/bin/env python3
"""Static source checks; not a C# compiler or a Windows runtime test.
Install tree-sitter and tree-sitter-c-sharp, then run this file.
Optional --baseline ZIP compares parser limitations against the original source package.
"""
from pathlib import Path
import argparse
import collections
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile
import tree_sitter
import tree_sitter_c_sharp

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', type=Path)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
cs_parser = tree_sitter.Parser(tree_sitter.Language(tree_sitter_c_sharp.language()))
baseline = zipfile.ZipFile(args.baseline) if args.baseline else None
baseline_names = set(baseline.namelist()) if baseline else set()
errors, warnings, hashes = [], [], []
counts = collections.Counter()
types = collections.defaultdict(list)
methods = collections.defaultdict(set)

def walk(n):
    yield n
    for child in n.children:
        yield from walk(child)

def parse_errors(data):
    tree = cs_parser.parse(data)
    result = []
    def visit(n):
        if n.type == 'ERROR' or n.is_missing:
            result.append((n.type, data[n.start_byte:n.end_byte].decode('utf-8', errors='replace')))
        else:
            for c in n.children: visit(c)
    visit(tree.root_node)
    return tree, result

def text(node, data):
    return data[node.start_byte:node.end_byte].decode('utf-8') if node else ''

for path in sorted(root.rglob('*.cs')):
    if any(x in path.relative_to(root).parts for x in ('bin', 'obj')): continue
    data = path.read_bytes()
    rel = path.relative_to(root).as_posix()
    tree, current_errors = parse_errors(data)
    counts['csharp_files'] += 1
    old_name = 'XCL2.App/' + rel
    old = baseline.read(old_name) if old_name in baseline_names else None
    if old != data:
        counts['changed_or_new_csharp_files'] += 1
        hashes.append({'path': old_name, 'sha256': hashlib.sha256(data).hexdigest()})
    old_errors = parse_errors(old)[1] if old else []
    if current_errors:
        target = warnings if old_errors == current_errors else errors
        target.append({'path': rel, 'kind': 'unchanged_parser_limitation' if target is warnings else 'csharp_parse',
                       'count': len(current_errors), 'snippets': [s[:100] for _, s in current_errors]})
    if rel.startswith(('Tests/', 'WinUI.Preview/', 'tools/')): continue
    if rel == 'Views/InstalledVersionComponentsDialog.cs': continue
    ns_node = next((n for n in walk(tree.root_node) if n.type in ('file_scoped_namespace_declaration','namespace_declaration')), None)
    ns = text(ns_node.child_by_field_name('name'), data) if ns_node else ''
    for node in walk(tree.root_node):
        if node.type not in ('class_declaration','struct_declaration','interface_declaration','record_declaration'): continue
        generic = node.child_by_field_name('type_parameters')
        if generic is None:
            generic = next((n for n in node.children if n.type == 'type_parameter_list'), None)
        arity = sum(c.type == 'type_parameter' for c in generic.children) if generic else 0
        names = [text(node.child_by_field_name('name'), data) + ('`' + str(arity) if arity else '')]
        parent = node.parent
        while parent:
            if parent.type in ('class_declaration','struct_declaration','record_declaration'):
                names.insert(0, text(parent.child_by_field_name('name'), data))
            parent = parent.parent
        key = ns + '.' + '.'.join(names)
        header = data[node.start_byte:(node.child_by_field_name('body') or node).start_byte].decode('utf-8')
        types[key].append({'path': rel, 'partial': bool(re.search(r'\bpartial\b', header))})
        body = node.child_by_field_name('body')
        for child in body.children if body else []:
            if child.type == 'method_declaration':
                methods[key].add(text(child.child_by_field_name('name'), data))

for name, declarations in types.items():
    if len(declarations) > 1 and not all(d['partial'] for d in declarations):
        errors.append({'kind': 'non_partial_duplicate_type', 'name': name, 'declarations': declarations})

events = set('Click Checked Unchecked SelectionChanged TextChanged ValueChanged Loaded Unloaded Closing Closed KeyDown KeyUp PreviewKeyDown PreviewKeyUp MouseDown MouseUp PreviewMouseDown PreviewMouseUp MouseMove MouseEnter MouseLeave LostFocus GotFocus SizeChanged ScrollChanged MouseDoubleClick Drop DragOver PreviewMouseWheel MouseWheel SelectedDateChanged IsVisibleChanged SourceInitialized StateChanged Activated Deactivated ContentRendered RequestNavigate TouchDown TouchMove TouchUp PreviewTouchDown PreviewTouchMove PreviewTouchUp DragEnter DragLeave'.split())
for path in sorted(root.rglob('*')):
    if path.suffix not in ('.xaml', '.csproj', '.props', '.targets', '.resx'): continue
    if any(x in path.relative_to(root).parts for x in ('bin','obj')): continue
    rel = path.relative_to(root).as_posix()
    try: doc = ET.parse(path)
    except ET.ParseError as e:
        errors.append({'path':rel,'kind':'xml_parse','detail':str(e)});continue
    counts['xml_files'] += 1
    if path.suffix == '.csproj':
        for item in doc.iter('Compile'):
            value = item.get('Include','')
            if not value or any(x in value for x in ('*','$',';')): continue
            counts['compile_links'] += 1
            if not (path.parent / value.replace('\\','/')).is_file():
                errors.append({'path':rel,'kind':'missing_compile_link','value':value})
    if path.suffix != '.xaml' or rel.startswith('WinUI.Preview/'): continue
    cls = doc.getroot().get('{http://schemas.microsoft.com/winfx/2006/xaml}Class')
    if not cls:continue
    named = []
    for elem in doc.iter():
        name = elem.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name')
        if name:named.append(name)
        for key,value in elem.attrib.items():
            if key.split('.')[-1] not in events or value.startswith('{'):continue
            counts['xaml_events'] += 1
            if value not in methods.get(cls,set()):
                errors.append({'path':rel,'kind':'missing_xaml_handler','class':cls,'event':key,'handler':value})

report = {'status':'static_checks_passed' if not errors else 'static_check_failed', 'counts':dict(counts),
          'errors':errors,'baseline_parser_warnings':warnings,'source_hashes':hashes,
          'limitations':['Does not compile or type-check C#.', 'Does not run regression executables or WPF.',
                         'Unchanged baseline tree-sitter errors require separate compiler verification.']}
if args.output:
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'status':report['status'],'counts':dict(counts),'errors':errors,'baseline_warning_files':len(warnings)}, ensure_ascii=False, indent=2))
raise SystemExit(bool(errors))
