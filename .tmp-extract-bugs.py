import zipfile, re, html, os

docx = r"C:\Users\SYSTE\Documents\WorkTiles Bugs及改进.docx"
z = zipfile.ZipFile(docx)
rels = z.read("word/_rels/document.xml.rels").decode("utf-8")
print("RELS:")
print(rels)
xml = z.read("word/document.xml").decode("utf-8")
paras = re.findall(r"<w:p[ >].*?</w:p>", xml, flags=re.S)
print("PARA COUNT", len(paras))
outdir = r"D:\Files\Codes\Projects\WorkTiles\WorkTiles\.tmp-bug-images"
os.makedirs(outdir, exist_ok=True)
for n in z.namelist():
    if n.startswith("word/media/"):
        data = z.read(n)
        path = os.path.join(outdir, os.path.basename(n))
        with open(path, "wb") as f:
            f.write(data)
        print("saved", path, len(data))
for i, p in enumerate(paras):
    texts = re.findall(r"<w:t[^>]*>(.*?)</w:t>", p)
    text = html.unescape("".join(texts)).strip()
    blips = re.findall(r'r:embed="(rId\d+)"', p)
    if text or blips:
        print(f"--- p{i} imgs={blips}")
        if text:
            print(text[:500])
