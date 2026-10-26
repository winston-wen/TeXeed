"""Regenerate the two bundled tutorial SVGs (requires latex and dvisvgm)."""
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
work = root / "artifacts" / "tutorial-math"
assets = root / "src" / "TeXeed.App" / "Assets" / "Tutorial"
work.mkdir(parents=True, exist_ok=True)
assets.mkdir(parents=True, exist_ok=True)
formulas = {"energy": r"E = mc^2", "sum": r"\sum_{k=1}^{n} k = \frac{n(n+1)}{2}"}
document = [r"\documentclass{article}", r"\usepackage{amsmath,amssymb}", r"\pagestyle{empty}", r"\begin{document}"]
for index, formula in enumerate(formulas, 1):
    document += [r"\begingroup", r"\setbox0=\hbox{$\displaystyle " + formulas[formula] + "$}",
                 rf"\typeout{{TEXEED-METRICS-{index}: \the\wd0,\the\ht0,\the\dp0}}", r"\shipout\box0", r"\endgroup"]
document += [r"\end{document}"]
(work / "formulas.tex").write_text("\n".join(document), encoding="utf-8")
log = subprocess.run(["latex", "-no-shell-escape", "-interaction=nonstopmode", "-halt-on-error", "formulas.tex"], cwd=work, capture_output=True, text=True, check=True).stdout
metrics = re.findall(r"TEXEED-METRICS-\d+:\s*([0-9.]+)pt,\s*([0-9.]+)pt,\s*([0-9.]+)pt", log)
assert len(metrics) == len(formulas)
subprocess.run(["dvisvgm", "--no-fonts", "--no-specials", "--exact-bbox", "--bbox=min", "--page=1-", "--cache=none", "--output=formula-%1p.svg", "formulas.dvi"], cwd=work, capture_output=True, check=True)
ET.register_namespace("", "http://www.w3.org/2000/svg")
ET.register_namespace("xlink", "http://www.w3.org/1999/xlink")
for index, (name, metric) in enumerate(zip(formulas, metrics), 1):
    tree = ET.parse(work / f"formula-{index}.svg")
    for key, value in zip(("width", "height", "depth"), metric):
        tree.getroot().set("data-texeed-" + key, value)
    tree.write(assets / f"{name}.svg", encoding="utf-8", xml_declaration=True)
print("Generated bundled tutorial SVGs:", ", ".join(formulas))
