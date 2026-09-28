# Third-party components

TeXeed uses the following separately licensed components. TeX distributions are external tools and are not bundled.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT | https://github.com/icsharpcode/AvalonEdit |
| PDFiumCore | 155.0.8057 | Apache-2.0 | https://github.com/Dtronix/PDFiumCore |
| PDFium native binaries | resolved by PDFiumCore lock file | BSD-style and bundled third-party notices | https://github.com/bblanchon/pdfium-binaries |
| .NET / WPF | 10 | MIT and third-party notices | https://github.com/dotnet/wpf |
| Unicode East Asian Width data | 17.0.0 | Unicode License V3 | https://www.unicode.org/Public/17.0.0/ucd/EastAsianWidth.txt |

Exact transitive versions are recorded in `packages.lock.json`. Redistribution notices from the native PDFium package and license texts are included in the application's `licenses` directory when published using this repository's packaging script.
