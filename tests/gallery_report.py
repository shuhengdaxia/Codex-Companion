"""Render the browser matrix results as an offline, reviewable report."""
import html
import json
import pathlib
import sys


def main():
    run = pathlib.Path(sys.argv[1]).resolve()
    manifest = json.loads((run / "preparation.json").read_text(encoding="utf-8-sig"))
    results = json.loads((run / "browser-results.json").read_text(encoding="utf-8-sig"))
    if not isinstance(results, list):
        raise ValueError("Browser matrix did not finish with a result list")
    by_id = {row["id"]: row for row in results}
    escape = lambda value: html.escape(str(value or ""), quote=True)
    cards = []
    for item in manifest["items"]:
        result = by_id.get(item["id"], {})
        state = result.get("state", item.get("state", "not-tested"))
        notes = [item.get("error"), item.get("offlinePackageError"), result.get("error")]
        if item["id"] == "argentina-vs-spain-final":
            notes.append("主图要求首页存在建议卡片 section.group/home-suggestions；本次在该条件下通过，不代表所有首页布局都会显示。")
        if result.get("artReview") == "review":
            notes.append("基础首页未检测到图片层，需核对主题选择器或显示条件。")
        screenshot = result.get("screenshot")
        preview = ""
        if screenshot:
            relative = pathlib.Path(screenshot).relative_to(run).as_posix()
            preview = '<a href="{0}"><img loading="lazy" src="{0}" alt="{1}"></a>'.format(
                escape(relative), escape(item["name"]))
        home = result.get("home", {})
        details = "主面板 {} · 文字 {} · 图片层 {}".format(
            home.get("mainBackground", "未测试"), home.get("mainColor", "未测试"),
            home.get("dataImageCount", "未测试"))
        cards.append('<article>{}<h2>{}</h2><p><b>{}</b> · {}</p><code>{}</code><p>{}</p><p>{}</p></article>'.format(
            preview, escape(item["name"]), escape(state), escape(item.get("mode")),
            escape(item["id"]), escape(details), escape("；".join(str(n) for n in notes if n))))
    counts = {}
    for result in results:
        state = result.get("state", "unknown")
        counts[state] = counts.get(state, 0) + 1
    document = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>主题库全量测试报告</title>
<style>body{font:16px/1.6 system-ui,sans-serif;margin:24px;background:#f2f4f8;color:#172033}
h1{margin-bottom:8px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:18px}
article{background:white;border:1px solid #d6dce5;border-radius:10px;padding:14px;overflow:hidden}
img{display:block;width:100%;aspect-ratio:1.6;object-fit:contain;background:#e7eaf0}h2{font-size:18px}
code{overflow-wrap:anywhere}p{margin:8px 0}.note{max-width:1000px;margin-bottom:24px}</style>
<h1>主题库全量测试报告</h1><div class="note"><p>目录 {catalog} 项；浏览器实测 {tested} 项；{counts}。</p>
<p>截图来自隔离 Edge 中的代表性界面测试，不是对每个主题在完整 Codex 桌面应用中的逐页验收。
测试包含主题解析、计算样式、图片解码、首页/对话切换和恢复；没有轮流更改用户当前 Codex 的主题。
unavailable 表示没有可用包或包缺资源，不表示测试通过。点击图片可查看原尺寸截图。</p></div>
<main>{cards}</main></html>'''
    document = (document.replace("{catalog}", str(manifest["themeCount"]))
                .replace("{tested}", str(len(results)))
                .replace("{counts}", escape(json.dumps(counts, ensure_ascii=False)))
                .replace("{cards}", "\n".join(cards)))
    output = run / "report.html"
    output.write_text(document, encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
