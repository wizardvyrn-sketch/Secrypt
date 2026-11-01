"""docs/manual/index.html の画像を埋め込んだ 1 ファイル版（manual-standalone.html）を作る。"""
import base64, pathlib, re
root = pathlib.Path(__file__).resolve().parent.parent / "docs" / "manual"
html = (root / "index.html").read_text(encoding="utf-8")
def embed(m):
    data = base64.b64encode((root / m.group(1)).read_bytes()).decode()
    return f'src="data:image/png;base64,{data}"'
out = re.sub(r'src="(images/[^"]+\.png)"', embed, html)
(root / "manual-standalone.html").write_text(out, encoding="utf-8")
print("wrote", root / "manual-standalone.html", len(out) // 1024, "KB")
