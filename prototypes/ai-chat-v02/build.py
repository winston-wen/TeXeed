"""Build the offline prototype and its typeset sample formulas."""
import hashlib
import html
import json
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET

HERE = pathlib.Path(__file__).resolve().parent
WORK = HERE.parents[1] / 'artifacts' / 'chat-prototype-v02-math'
WORK.mkdir(parents=True, exist_ok=True)
vendor = HERE / 'vendor'
for package in json.loads((vendor / 'vendor-lock.json').read_text(encoding='utf-8')).values():
    for filename, checksum in package['files'].items():
        if hashlib.sha256((vendor / filename).read_bytes()).hexdigest() != checksum:
            raise RuntimeError(f'Vendor integrity check failed: {filename}')
data = json.loads((HERE / 'transcripts.json').read_text(encoding='utf-8'))
data['prompts'] = json.loads((HERE / 'prompts.json').read_text(encoding='utf-8'))
pattern = re.compile(r'\\\[(.*?)\\\]|\\\((.*?)\\\)|\$\$(.*?)\$\$|\$([^$\n]+)\$', re.S)
formulas = {}
all_turns = data['turns'] + [turn for conversation in data['sampleConversations'] for turn in conversation['turns']]
for scenario in all_turns + [dict(question='', answer=data['playback']['answer'])]:
    for source in [scenario['question'], scenario['answer']]:
        for m in pattern.finditer(source):
            tex = next(g for g in m.groups() if g is not None).strip()
            key = (tex, m.group(1) is not None or m.group(3) is not None)
            if key not in formulas:
                formulas[key] = len(formulas) + 1

document = [r'\documentclass{article}', r'\usepackage{amsmath,amssymb,xeCJK}',
            r'\setCJKmainfont{Microsoft YaHei}', r'\tracinglostchars=2', r'\begin{document}']
for (tex, display), i in formulas.items():
    style = r'\displaystyle' if display else r'\textstyle'
    document += [r'\begingroup', r'\setbox0=\hbox{$' + style + '\n' + tex + '\n$}',
                 r'\typeout{METRIC-' + str(i) + r': \the\wd0,\the\ht0,\the\dp0}',
                 r'\shipout\box0', r'\endgroup']
document += [r'\end{document}']
(WORK / 'formula.tex').write_text('\n'.join(document), encoding='utf-8')
result = subprocess.run(['xelatex', '-no-pdf', '-no-shell-escape', '-interaction=nonstopmode',
                         '-halt-on-error', 'formula.tex'], cwd=WORK, capture_output=True, timeout=90)
log = result.stdout.decode('utf-8', errors='replace')
if result.returncode or 'Missing character:' in log:
    raise RuntimeError(log[-6000:])
metrics = {int(m[0]): tuple(float(x) for x in m[1:]) for m in re.findall(
    r'METRIC-(\d+):\s*([\d.]+)pt,\s*([\d.]+)pt,\s*([\d.]+)pt', log)}
subprocess.run(['dvisvgm', '--no-fonts', '--no-specials', '--exact-bbox', '--bbox=min',
                '--page=1-', '--cache=none', '--output=formula-%1p.svg', 'formula.xdv'],
               cwd=WORK, check=True, capture_output=True, timeout=90)
serial = 0
def math_markup(tex, display):
    global serial
    serial += 1
    i = formulas[(tex, display)]
    root = ET.parse(WORK / f'formula-{i}.svg').getroot()
    ET.register_namespace('', 'http://www.w3.org/2000/svg')
    ET.register_namespace('xlink', 'http://www.w3.org/1999/xlink')
    width, height, depth = metrics[i]
    root.set('width', f'{width / 10:.5f}em')
    root.set('height', f'{(height + depth) / 10:.5f}em')
    root.set('style', f'vertical-align:-{depth / 10:.5f}em;fill:currentColor')
    root.set('aria-hidden', 'true')
    svg = ET.tostring(root, encoding='unicode')
    svg = re.sub(r'\bid="([^"]+)"', lambda m: f'id="m{serial}-{m[1]}"', svg)
    svg = re.sub(r'href="#([^"]+)"', lambda m: f'href="#m{serial}-{m[1]}"', svg)
    escaped = html.escape(tex, quote=True)
    return f'<span class="math" data-tex="{escaped}" data-display="{str(display).lower()}" role="math" aria-label="{escaped}" tabindex="0">{svg}</span>'

def render(source, prefix):
    tokens = []
    def take(m):
        tex = next(g for g in m.groups() if g is not None).strip()
        display = m.group(1) is not None or m.group(3) is not None
        tokens.append((math_markup(tex, display), display))
        return ('\n\n' if display else '') + f'@@M{len(tokens)-1}@@' + ('\n\n' if display else '')
    source = pattern.sub(take, source)
    def inline(text):
        text = html.escape(text)
        text = re.sub(r'`([^`]+)`', r'<code>\1</code>', text)
        text = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', text)
        return re.sub(r'@@M(\d+)@@', lambda m: tokens[int(m[1])][0], text)
    blocks = []
    equation = 0
    for chunk in re.split(r'\n\s*\n', source.strip()):
        lines = chunk.splitlines()
        mt = re.fullmatch(r'@@M(\d+)@@', chunk.strip())
        if mt and tokens[int(mt[1])][1]:
            equation += 1
            content = f'<div class="equation">{tokens[int(mt[1])][0]}<span class="equation-number">({equation})</span></div>'
        elif chunk.strip() == '---':
            content = '<hr>'
        elif lines[0].startswith('#'):
            level = min(4, len(lines[0]) - len(lines[0].lstrip('#')) + 1)
            content = f'<h{level}>{inline(lines[0].lstrip("# "))}</h{level}>'
        elif lines[0].startswith('|'):
            rows = [line.strip('|').split('|') for line in lines if not re.match(r'^\|[\s:|\-]+\|$', line)]
            content = '<div class="table-wrap"><table><thead><tr>' + ''.join('<th>'+inline(c.strip())+'</th>' for c in rows[0]) + '</tr></thead><tbody>'
            content += ''.join('<tr>'+''.join('<td>'+inline(c.strip())+'</td>' for c in row)+'</tr>' for row in rows[1:]) + '</tbody></table></div>'
        elif all(re.match(r'^\d+\. ', line) for line in lines):
            content = '<ol>' + ''.join('<li>'+inline(re.sub(r'^\d+\. ', '', line))+'</li>' for line in lines)+'</ol>'
        elif all(line.startswith('- ') for line in lines):
            content = '<ul>' + ''.join('<li>'+inline(line[2:])+'</li>' for line in lines)+'</ul>'
        elif lines[0].startswith('>'):
            content = '<blockquote>'+inline(' '.join(line.lstrip('> ') for line in lines))+'</blockquote>'
        else:
            content = '<p>'+inline(' '.join(lines))+'</p>'
        block_id = f'{prefix}-p{len(blocks)}'
        blocks.append(f'<section class="passage" id="{block_id}" data-block="{block_id}"><div class="passage-body">{content}</div><div class="margin-notes"></div></section>')
    return '\n'.join(blocks)

for turn in all_turns:
    turn['answerHtml'] = render(turn['answer'], turn['id'])
data['playback']['answerHtml'] = render(data['playback']['answer'], 'playback')

encoded = json.dumps(data, ensure_ascii=False).replace('</', '<\\/')
template = (HERE / 'template.html').read_text(encoding='utf-8')
(HERE / 'index.html').write_text(template.replace('/*__APP__*/', (HERE / 'app.js').read_text(encoding='utf-8').replace('/*__TRANSCRIPTS__*/', encoded)), encoding='utf-8')
print(f'Built index.html: {len(formulas)} unique TeX formulas, {serial} instances, {(HERE / "index.html").stat().st_size:,} bytes.')
