# Third-party components

TeXeed uses the following separately licensed components. TeX distributions are external tools and are not bundled.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT | https://github.com/icsharpcode/AvalonEdit |
| Markdig | 0.41.3 | BSD-2-Clause | https://github.com/xoofx/markdig |
| WpfMath / XamlMath.Shared | 2.1.0 | MIT; embedded fonts: Knuth License and OFL-1.1 | https://github.com/ForNeVeR/xaml-math |
| GitHub Light Default palette (adapted from GitHub Theme) | 6.3.5 | MIT | https://github.com/primer/github-vscode-theme |
| PDFiumCore | 155.0.8057 | Apache-2.0 | https://github.com/Dtronix/PDFiumCore |
| PDFium native binaries | resolved by PDFiumCore lock file | BSD-style and bundled third-party notices | https://github.com/bblanchon/pdfium-binaries |
| .NET / WPF | 10 | MIT and third-party notices | https://github.com/dotnet/wpf |
| Unicode East Asian Width data | 17.0.0 | Unicode License V3 | https://www.unicode.org/Public/17.0.0/ucd/EastAsianWidth.txt |
| vscode-icons (selected explorer icons) | 13.0.0 | MIT | https://github.com/vscode-icons/vscode-icons |

Exact transitive versions are recorded in `packages.lock.json`. Redistribution notices from the native PDFium package and license texts are included in the application's `licenses` directory when published using this repository's packaging script.

The two tutorial formula SVGs in `src/TeXeed.App/Assets/Tutorial` are generated LaTeX document output containing vector glyph paths. They contain no font files, scripts, or external resources. Regenerate them with `scripts/build-tutorial-math.py` using an external TeX installation and dvisvgm; those development tools are not bundled. Selected vscode-icons explorer SVGs are retained in `src/TeXeed.App/Assets/Explorer` and converted to native WPF drawings by `scripts/build-explorer-icons.py`; their MIT license is included in `licenses/vscode-icons-MIT.txt`.
