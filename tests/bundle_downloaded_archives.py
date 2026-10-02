"""Rebuild the four reviewed archive bundles; never execute archive code."""
import base64
import hashlib
import json
import pathlib
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "assets" / "theme-gallery"
IDS = ("miku", "trump-maga-presidential", "theme-1786780354717", "gandum")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def image_record(name, data):
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        mime = "image/png"
    elif data.startswith(b"\xff\xd8\xff"):
        mime = "image/jpeg"
    else:
        raise ValueError("Unsupported archive image: " + name)
    return {"filename": pathlib.PurePosixPath(name).name, "mimeType": mime,
            "base64": base64.b64encode(data).decode("ascii")}


def dream_css(css, config):
    light = config["appearance"] == "light"
    palette = dict(background="#f3f5f6" if light else "#111318",
                   panel="#fafbfb" if light else "#191c22",
                   panelAlt="#e9edef" if light else "#20242b",
                   text="#22272a" if light else "#edf0f1",
                   muted="#687176" if light else "#a3aaae")
    palette.update(config["colors"])
    variables = {}
    for field, token in {"background": "bg", "panel": "panel", "panelAlt": "panel-2",
                         "text": "text", "muted": "muted", "accent": "green",
                         "accentAlt": "lime", "secondary": "cyan", "highlight": "purple",
                         "line": "line"}.items():
        variables["--ds-" + token] = palette[field]
    for field, token in {"background": "bg", "panel": "panel", "panelAlt": "panel-2",
                         "text": "text", "muted": "muted", "accent": "accent",
                         "accentAlt": "accent-alt", "secondary": "secondary",
                         "highlight": "highlight"}.items():
        value = palette[field].lstrip("#")
        assert len(value) == 6
        variables["--ds-" + token + "-rgb"] = " ".join(str(int(value[i:i+2], 16)) for i in (0, 2, 4))
    for key in ("name", "tagline", "projectPrefix", "projectLabel"):
        token = {"projectPrefix": "project-prefix", "projectLabel": "project-label"}.get(key, key)
        variables["--dream-skin-" + token] = json.dumps(config[key], ensure_ascii=False)
    variables["--dream-skin-art"] = 'url("' + config["image"] + '")'
    variables["--dream-art-focus-x"] = str(config["art"]["focusX"] * 100) + "%"
    variables["--dream-art-focus-y"] = str(config["art"]["focusY"] * 100) + "%"
    variables["--dream-art-position"] = "var(--dream-art-focus-x) var(--dream-art-focus-y)"
    for token, value in {"main-surface-primary": "bg", "bg-secondary": "panel",
                         "bg-tertiary": "panel-2", "foreground": "text",
                         "text-secondary": "muted", "text-tertiary": "muted",
                         "icon-foreground": "muted"}.items():
        variables["--color-token-" + token] = "var(--ds-" + value + ")"
    css += "\n/* Reviewed archive configuration and modern Codex surface bridge. */\n"
    css += "html.codex-dream-skin[data-midweb-dream-theme] {\n"
    css += "\n".join("  " + k + ": " + v + " !important;" for k, v in variables.items()) + "\n}\n"
    css += """
html.codex-dream-skin[data-midweb-dream-theme] main[data-codexthemes-page="system"]::before {
  content: none !important;
}
"""
    return css


def convert(archive_dir):
    status = read(BUNDLE / "bundle-status.json")
    inventory = read(BUNDLE / "bundle-inventory.json")
    bindings = read(BUNDLE / "package-bindings.json")
    provenance = []
    for gallery_id in IDS:
        archive = archive_dir / (gallery_id + ".zip")
        with zipfile.ZipFile(archive) as z:
            assert sum(i.file_size for i in z.infolist()) < 100 * 1024 * 1024
            assert z.testzip() is None
            if gallery_id in IDS[:2]:
                names = [n for n in z.namelist() if n.endswith("/theme.json") and not n.startswith("__MACOSX/")]
                assert len(names) == 1
                prefix = names[0].rsplit("/", 1)[0] + "/"
                manifest = json.loads(z.read(names[0]))
                for key in ("css", "art"):
                    path = pathlib.PurePosixPath(manifest[key])
                    assert not path.is_absolute() and ".." not in path.parts and "\\" not in str(path)
                css = z.read(prefix + manifest["css"]).decode("utf-8-sig")
                css = css.replace('[data-codexthemes-theme="' + manifest["id"] + '"]',
                                  '[data-codexthemes-theme="' + gallery_id + '"]')
                art = image_record(manifest["art"], z.read(prefix + manifest["art"]))
                # These archives assume a host stacking context; current Codex
                # needs one explicitly so art neither disappears nor covers text.
                scope = ':root[data-codexthemes-theme="' + gallery_id + '"] main.main-surface'
                css += "\n/* Modern host artwork stacking compatibility. */\n"
                css += scope + " { isolation: isolate; }\n"
                css += scope + "::before, " + scope + "::after { z-index: -1 !important; }\n"
            else:
                gundam = gallery_id == "gandum"
                prefix = "Codex-skin-gundam/" if gundam else ""
                config = json.loads(z.read(prefix + ("theme/theme.json" if gundam else "theme.json")))
                if gundam:
                    css = z.read(prefix + "dream-skin.css").decode("utf-8-sig")
                else:
                    with zipfile.ZipFile(archive_dir / "gandum.zip") as engine:
                        css = engine.read("Codex-skin-gundam/layers/_engine-base.css").decode("utf-8-sig")
                    css += "\n" + z.read("theme.css").decode("utf-8-sig")
                css = dream_css(css, config)
                art = image_record(config["image"], z.read(prefix + ("theme/" if gundam else "") + config["image"]))
                manifest = {"schemaVersion": 1, "id": config["id"], "displayName": config["name"],
                            "mode": config["appearance"], "css": "theme.css", "art": config["image"],
                            "design": {"backgroundScope": "workspace"}}
            package = {"format": "codex-theme", "schemaVersion": 1, "manifest": manifest, "css": css, "art": art}
            data = (json.dumps(package, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
            assert len(data) < 30 * 1024 * 1024
            url = "https://codexthemes.ai/api/themes/" + gallery_id + "/download"
            filename = "package-" + digest(url.encode()) + ".codex-theme"
            (BUNDLE / filename).write_bytes(data)
            record = dict(galleryId=gallery_id, sourceUrl=url, file=filename, sha256=digest(data),
                          bytes=len(data), status="ready", manifestId=manifest["id"], error=None)
            status["packages"] = [x for x in status["packages"] if x["galleryId"] != gallery_id] + [record]
            inventory["resources"] = [x for x in inventory["resources"] if not (x["id"] == gallery_id and x["kind"] == "package")]
            inventory["resources"].append(dict(id=gallery_id, kind="package", file=filename, sha256=digest(data), bytes=len(data), sourceUrl=url))
            bindings["bindings"] = [x for x in bindings["bindings"] if x["galleryId"] != gallery_id]
            if manifest["id"] != gallery_id:
                bindings["bindings"].append(dict(galleryId=gallery_id, packageId=manifest["id"], downloadUrl=url, sha256=digest(data)))
            for path in BUNDLE.glob("catalog-*-50.json"):
                catalog = read(path)
                for row in catalog["themes"]:
                    if row["id"] == gallery_id:
                        row.update(installable=True, downloadUrl=url, mode=manifest["mode"],
                                   guidance="Bundled from the downloaded archive; see archive-provenance.json.",
                                   offlinePackage={k: v for k, v in record.items() if k not in ("galleryId", "sourceUrl")})
                        write(path, catalog)
            provenance.append(dict(id=gallery_id, archive=archive.name, archiveSha256=digest(archive.read_bytes()),
                                   packageFile=filename, packageSha256=digest(data),
                                   adaptation="Original CSS/art with catalog selector and modern host stacking context" if gallery_id in IDS[:2] else "Dream Skin CSS/art with reviewed host-owned DOM adapter",
                                   scriptsExecuted=False))
    inventory["packageCount"] = sum(x["kind"] == "package" for x in inventory["resources"])
    write(BUNDLE / "bundle-status.json", status)
    write(BUNDLE / "bundle-inventory.json", inventory)
    write(BUNDLE / "package-bindings.json", bindings)
    write(BUNDLE / "archive-provenance.json", provenance)
    with zipfile.ZipFile(archive_dir / "gandum.zip") as z:
        for name in ("LICENSE", "NOTICE.md"):
            (BUNDLE / ("dream-skin-" + name)).write_bytes(z.read("Codex-skin-gundam/" + name))
    print("Bundled:", ", ".join(IDS))


if __name__ == "__main__":
    convert(pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "theme-imports")
