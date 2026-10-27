"""Check image and table markup without typesetting the bundled examples."""
import ast
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


if __name__ == '__main__':
    unittest.main()
