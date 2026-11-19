"""Check image and table markup without typesetting the bundled examples."""
import ast
import json
import runpy
import tempfile
from unittest.mock import patch
import html
import pathlib
import re
import unittest

source = pathlib.Path(__file__).with_name('build.py').read_text(encoding='utf-8')
function = next(node for node in ast.parse(source).body if isinstance(node, ast.FunctionDef) and node.name == 'render')
namespace = {'html': html, 're': re, 'pattern': re.compile(r'(?!)')}
exec(compile(ast.Module(body=[function], type_ignores=[]), 'build.py', 'exec'), namespace)
render = namespace['render']


class BuildMarkdownTests(unittest.TestCase):
    def test_reading_numbers_and_code_skip_tex(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp) / 'prototypes' / 'test'
            root.mkdir(parents=True)
            (root / 'build.py').write_text(source, encoding='utf-8')
            (root / 'vendor').mkdir()
            (root / 'vendor/vendor-lock.json').write_text('{}')
            (root / 'prompts.json').write_text('{}')
            (root / 'visual-preview.json').write_text('{"conversations":[]}')
            for filename in ('template.html', 'reader-autoscroll.js', 'app.js'):
                (root / filename).write_text('')
            answer = '正文 $ ０６４ $。\n\n`$x$`\n\n```tex\n$x^2$\n```\n\n~~~md\n$r$\n~~~'
            fixture = {'turns': [{'id': 'test', 'question': '$512$', 'answer': answer}],
                       'sampleConversations': [], 'playback': {'answer': ''}}
            (root / 'transcripts.json').write_text(json.dumps(fixture), encoding='utf-8')
            with patch('subprocess.run', side_effect=AssertionError('Unexpected formula compilation')):
                result = runpy.run_path(str(root / 'build.py'))
            markup = result['data']['turns'][0]['answerHtml']
            self.assertIn('<span data-inline-number="０６４">０６４</span>', markup)
            for literal in ('$x$', '$x^2$', '$r$'):
                self.assertIn(literal, markup)
            self.assertNotIn('<svg', markup)

    def test_images(self):
        for url in ('assets/flight-comparison.svg', 'https://example.test/image.png'):
            result = render(f'![A & B]({url})', 'demo')
            self.assertIn(f'src="{url}"', result)
            self.assertIn('alt="A &amp; B"', result)
            self.assertIn('class="zoomable-image"', result)

    def test_code_and_unsafe_sources(self):
        self.assertNotIn('<img ', render('`![source](assets/image.svg)`', 'code'))
        self.assertNotIn('<img ', render('![source](javascript:run)', 'unsafe'))
        self.assertIn('@@INLINE0@@', render('Literal @@INLINE0@@ and ![source](assets/image.svg)', 'literal'))

    def test_table_scroll_container(self):
        result = render('| A | B |\n|---|---|\n| One | Two |', 'table')
        self.assertIn('class="table-wrap" tabindex="0"', result)
        self.assertIn('</table></div>', result)

    def test_original_heading_levels(self):
        for level in range(1, 7):
            result = render('#' * level + ' Heading', 'heading')
            self.assertIn(f'<h{level}>Heading</h{level}>', result)
            self.assertIn('data-block="heading-p0"', result)
        self.assertIn('<h3>Heading</h3>', render('### Heading ###', 'closing'))
        for text in ('####### Plain text', '#Not a heading'):
            self.assertNotRegex(render(text, 'plain'), r'<h[1-6]>')


if __name__ == '__main__':
    unittest.main()
