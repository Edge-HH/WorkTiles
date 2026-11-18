import html, re, shutil, zipfile
from pathlib import Path
src = Path(r"C:\Users\SYSTE\Documents\WorkTiles Bugs及改进.docx")
needles = [
    "控制窗处在右上或右下时缩放控件没了",
    "删除磁贴时会刷新一下",
    "首次启动时那个新建和导入按钮",
]
suffix = " 【已完成】"
with zipfile.ZipFile(src) as z:
    xml = z.read("word/document.xml").decode("utf-8")
    others = {name: z.read(name) for name in z.namelist() if name != "word/document.xml"}
changed = 0
def text_of(p):
    return html.unescape("".join(re.findall(r"<w:t[^>]*>(.*?)</w:t>", p)))
def replacer(match):
    global changed
    p = match.group(0)
    text = text_of(p)
    if suffix in text or not any(n in text for n in needles):
        return p
    changed += 1
    run = '<w:r><w:rPr><w:b/><w:color w:val="1B7F3A"/></w:rPr><w:t xml:space="preserve">' + suffix + "</w:t></w:r>"
    return p.replace("</w:p>", run + "</w:p>", 1)
xml = re.sub(r"<w:p[ >].*?</w:p>", replacer, xml, flags=re.S)
if changed != len(needles):
    raise SystemExit(f"marked {changed} of {len(needles)}")
tmp = src.with_suffix(".docx.tmp")
with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED) as z:
    for name, data in others.items():
        z.writestr(name, data)
    z.writestr("word/document.xml", xml.encode("utf-8"))
shutil.move(tmp, src)
print("marked", changed)
