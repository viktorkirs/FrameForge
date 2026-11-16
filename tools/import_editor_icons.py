"""Convert the selected user-supplied SVGs into native WPF DrawingImages (no runtime SVG dependency)."""
import argparse
import re
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

ICONS = {
    'Crop': 'crop-svgrepo-com.svg', 'Split': 'cut-film-svgrepo-com.svg',
    'Save': 'save-item-1411-svgrepo-com.svg', 'Move': 'arrow-all-331-svgrepo-com.svg',
    'Folder': 'folder-1795-svgrepo-com.svg', 'Export': 'file-arrow-up-1614-svgrepo-com.svg',
    'Video': 'video-recorder-svgrepo-com.svg', 'Audio': 'music-994-svgrepo-com.svg',
    'Voice': 'waveform-svgrepo-com.svg', 'Delete': 'delete-1487-svgrepo-com.svg',
    'Track': 'collection-989-svgrepo-com.svg', 'Done': 'done-1477-svgrepo-com.svg',
    'Play': 'play-1003-svgrepo-com.svg', 'Undo': 'arrow-corner-278-svgrepo-com.svg',
    'Range': 'video-frame-cut-2-svgrepo-com.svg', 'Editor': 'video-frame-2-svgrepo-com.svg',
    'CopyTrack': 'video-roll-svgrepo-com.svg',
}
NS = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
X = 'http://schemas.microsoft.com/winfx/2006/xaml'
ET.register_namespace('', NS)
ET.register_namespace('x', X)
def el(name, attrs=None): return ET.Element(f'{{{NS}}}{name}', attrs or {})
def num(text): return [float(x) for x in re.findall(r'[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?', text)]
def product(a, b): return [[sum(a[r][k] * b[k][c] for k in range(3)) for c in range(3)] for r in range(3)]
def matrix(text):
    result = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]
    for name, values in re.findall(r'(\w+)\(([^)]+)\)', text):
        v = num(values)
        if name == 'translate': m = [[1, 0, v[0]], [0, 1, v[1] if len(v) > 1 else 0], [0, 0, 1]]
        elif name == 'scale': m = [[v[0], 0, 0], [0, v[-1], 0], [0, 0, 1]]
        elif name == 'matrix': m = [[v[0], v[2], v[4]], [v[1], v[3], v[5]], [0, 0, 1]]
        else: raise ValueError('Unsupported SVG transform: ' + name)
        result = product(result, m)
    return ','.join(f'{x:g}' for x in (result[0][0], result[1][0], result[0][1], result[1][1], result[0][2], result[1][2]))

def convert(node, inherited, classes):
    tag = node.tag.rsplit('}', 1)[-1]
    style = dict(inherited)
    style.update(classes.get(node.get('class'), {}))
    style.update(dict(re.findall(r'([\w-]+)\s*:\s*([^;]+)', node.get('style', ''))))
    style.update({key: value for key, value in node.attrib.items() if key in ('fill', 'stroke', 'stroke-width')})
    if tag in ('svg', 'g'):
        result = el('DrawingGroup')
        if node.get('transform'): result.set('Transform', matrix(node.get('transform')))
        for child in node:
            value = convert(child, style, classes)
            if value is not None: result.append(value)
        return result
    geometry = None
    if tag == 'path': geometry = el('PathGeometry', {'Figures': node.get('d'), 'FillRule': 'EvenOdd' if style.get('fill-rule') == 'evenodd' else 'Nonzero'})
    elif tag in ('polyline', 'polygon'):
        p = num(node.get('points'))
        data = 'M ' + ' L '.join(f'{p[i]:g},{p[i + 1]:g}' for i in range(0, len(p), 2)) + (' Z' if tag == 'polygon' else '')
        geometry = el('PathGeometry', {'Figures': data})
    elif tag == 'line': geometry = el('PathGeometry', {'Figures': f"M {node.get('x1', '0')},{node.get('y1', '0')} L {node.get('x2', '0')},{node.get('y2', '0')}"})
    elif tag in ('circle', 'ellipse'):
        geometry = el('EllipseGeometry', {'Center': f"{node.get('cx', '0')},{node.get('cy', '0')}", 'RadiusX': node.get('r', node.get('rx', '0')), 'RadiusY': node.get('r', node.get('ry', '0'))})
    elif tag == 'rect':
        geometry = el('RectangleGeometry', {'Rect': ','.join(node.get(k, '0') for k in ('x', 'y', 'width', 'height')), 'RadiusX': node.get('rx', '0'), 'RadiusY': node.get('ry', node.get('rx', '0'))})
    if geometry is None: return None
    result = el('GeometryDrawing')
    if style.get('fill', 'black') != 'none' and tag not in ('line', 'polyline'): result.set('Brush', '#FFE7EBF3')
    if style.get('stroke', 'none') != 'none':
        pen = el('GeometryDrawing.Pen'); pen.append(el('Pen', {'Brush': '#FFE7EBF3', 'Thickness': str(num(style.get('stroke-width', '1'))[0]), 'StartLineCap': 'Round', 'EndLineCap': 'Round', 'LineJoin': 'Round'})); result.append(pen)
    prop = el('GeometryDrawing.Geometry'); prop.append(geometry); result.append(prop)
    if node.get('transform'):
        group = el('DrawingGroup', {'Transform': matrix(node.get('transform'))}); group.append(result); return group
    return result

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('source', type=Path); args = parser.parse_args()
    assets = Path(__file__).resolve().parents[1] / 'src/FrameForge.UI/Assets'
    originals = assets / 'EditorIcons'; originals.mkdir(exist_ok=True)
    resources = el('ResourceDictionary')
    for name, filename in ICONS.items():
        source = args.source / filename
        if not source.exists(): source = args.source.parent / filename
        shutil.copy2(source, originals / filename)
        svg = ET.parse(source).getroot(); css = ''.join(n.text or '' for n in svg.iter() if n.tag.endswith('}style'))
        classes = {name: dict(re.findall(r'([\w-]+)\s*:\s*([^;]+)', body)) for name, body in re.findall(r'\.([\w-]+)\s*\{([^}]+)\}', css)}
        x, y, w, h = num(svg.get('viewBox'))
        image = el('DrawingImage', {f'{{{X}}}Key': 'EditorIcon' + name}); prop = el('DrawingImage.Drawing'); group = el('DrawingGroup')
        group.append(el('GeometryDrawing', {'Brush': '#00FFFFFF', 'Geometry': 'M0,0 L24,0 24,24 0,24 Z'}))
        normalized = el('DrawingGroup', {'Transform': f'{24 / w:g},0,0,{24 / h:g},{-x * 24 / w:g},{-y * 24 / h:g}'})
        normalized.append(convert(svg, {'fill': 'black'}, classes)); group.append(normalized); prop.append(group); image.append(prop); resources.append(image)
    for name, data in {'Pause': 'M6,3 L10,3 10,21 6,21 Z M14,3 L18,3 18,21 14,21 Z', 'Stop': 'M4,4 L20,4 20,20 4,20 Z'}.items():
        image = el('DrawingImage', {f'{{{X}}}Key': 'EditorIcon' + name}); prop = el('DrawingImage.Drawing'); prop.append(el('GeometryDrawing', {'Brush': '#FFE7EBF3', 'Geometry': data})); image.append(prop); resources.append(image)
    ET.indent(resources, space='  ')
    ET.ElementTree(resources).write(assets / 'EditorIcons.xaml', encoding='utf-8', xml_declaration=True)
    print(f'Imported {len(ICONS)} SVGs as WPF vectors.')

if __name__ == '__main__': main()
