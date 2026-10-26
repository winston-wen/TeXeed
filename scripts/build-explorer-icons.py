"""Convert the checked-in vscode-icons subset to frozen native WPF drawings."""
import pathlib
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
source = root / 'src/TeXeed.App/Assets/Explorer'
ns = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
x = 'http://schemas.microsoft.com/winfx/2006/xaml'
po = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation/options'
ET.register_namespace('', ns)
ET.register_namespace('x', x)
ET.register_namespace('po', po)
dictionary = ET.Element('{' + ns + '}ResourceDictionary')
for file in sorted(source.glob('*.svg')):
    image = ET.SubElement(dictionary, 'DrawingImage', {'{' + x + '}Key': file.stem, '{' + po + '}Freeze': 'True'})
    wrapper = ET.SubElement(image, 'DrawingImage.Drawing')
    group = ET.SubElement(wrapper, 'DrawingGroup')
    ET.SubElement(group, 'GeometryDrawing', Brush='Transparent', Geometry='M0,0 H32 V32 H0 Z')
    for element in ET.parse(file).iter():
        tag = element.tag.split('}')[-1]
        if tag in ('svg', 'title'):
            continue
        assert 'transform' not in element.attrib, file
        style = dict(pair.split(':', 1) for pair in element.get('style', '').split(';') if ':' in pair)
        def attr(name, fallback):
            return element.get(name, style.get(name, fallback))
        if tag == 'path':
            data = element.get('d')
        elif tag in ('polygon', 'polyline'):
            data = 'M' + element.get('points') + (' Z' if tag == 'polygon' else '')
        elif tag == 'rect':
            left, top, width, height = [float(element.get(n, '0')) for n in ('x', 'y', 'width', 'height')]
            data = f'M{left},{top} h{width} v{height} h{-width} Z'
        else:
            raise ValueError((file, tag))
        drawing = ET.SubElement(group, 'GeometryDrawing', Geometry=('F0 ' if attr('fill-rule', '') == 'evenodd' else 'F1 ') + data)
        fill = attr('fill', '#000000')
        if fill != 'none':
            drawing.set('Brush', fill)
        stroke = attr('stroke', 'none')
        if stroke != 'none':
            prop = ET.SubElement(drawing, 'GeometryDrawing.Pen')
            ET.SubElement(prop, 'Pen', Brush=stroke, Thickness=attr('stroke-width', '1'))
ET.indent(dictionary)
ET.ElementTree(dictionary).write(root / 'src/TeXeed.App/ExplorerIcons.xaml', encoding='utf-8', xml_declaration=True)
print('Converted', len(list(source.glob('*.svg'))), 'vscode-icons SVGs to native WPF drawings.')
