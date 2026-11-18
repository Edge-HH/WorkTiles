"""Append a completion mark to specific paragraphs in the bug document."""
import html
import re
import shutil
import sys
import zipfile
from pathlib import Path

src = Path(r"C:\Users\SYSTE\Documents\WorkTiles Bugs及改进.docx")
marks = sys.argv[1:]
if not marks:
    raise SystemExit("usage: mark-bugs.py <paragraph text> ...")

suffix = " 【已完成】"
with zipfile.ZipFile(src) as z:
    xml = z.read("word/document.xml").decode("utf-8")
    others = {name: z.read(name) for name in z.namelist() if name != "word/document.xml"}

def paragraph_text(p: str) -> str:
    return html.unescape("".join(re.findall(r"<w:t[^>]*>(.*?)</w:t>", p)))

changed = 0
def replacer(match: re.Match) -> str:
    global changed
    p = match.group(0)
    text = paragraph_text(p)
    if text in marks and suffix not in text:
        changed += 1
        run = (
            '<w:r><w:rPr><w:b/><w:color w:val="1B7F3A"/></w:rPr>'
            f"<w:t xml:space=\"preserve\">{suffix}</w:t></w:r>"
        )
        return p.replace("</w:p>", run + "</w:p>", 1)
    return p

xml = re.sub(r"<w:p[ >].*?</w:p>", replacer, xml, flags=re.S)
if changed != len(marks):
    raise SystemExit(f"marked {changed} of {len(marks)}")

tmp = src.with_suffix(".docx.tmp")
with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED) as z:
    for name, data in others.items():
        z.writestr(name, data)
    z.writestr("word/document.xml", xml.encode("utf-8"))
shutil.move(tmp, src)
print(f"marked {changed}")
