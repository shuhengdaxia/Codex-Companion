"""Isolated browser regression for the two reviewed Dream Skin packages.

This test only reads the already prepared .codex-theme bundles. It never opens
an archive or executes archive supplied JavaScript.
"""

import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from datetime import datetime

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from theme_gallery_browser import compile_preparation_harness, find_browser, free_port


IDS = ("theme-1786780354717", "gandum", "gpt")


def wait_for_page(port, fixture_url):
    deadline = time.time() + 20
    url = "http://127.0.0.1:%d/json/list" % port
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                for page in json.loads(response.read().decode("utf-8")):
                    if page.get("webSocketDebuggerUrl") and (
                        page.get("url", "") == fixture_url
                        or page.get("url", "").endswith("/fixture.html")
                    ):
                        return page["webSocketDebuggerUrl"]
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("headless browser did not expose the Dream fixture")


def write_fixture(path):
    path.write_text(
        """<!doctype html><html class="foreign-shell" data-codex-theme="native-theme"
 data-codex-theme-mode="light" data-codexthemes-theme="native-theme"
 data-codexthemes-background-scope="workspace" data-dream-shell="native-shell"
 data-dream-art-wide="native-wide" data-dream-art-safe="native-safe"
 data-dream-task-mode="native-mode" data-ds-route="native-route">
<head><style>html,body{margin:0;width:100%;height:100%}body{font-family:Arial}</style></head>
<body><aside class="app-shell-left-panel"><nav><a aria-current="page">Home</a></nav></aside>
<main class="native-main-class _MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2">
 <div role="main" data-testid="home-icon">
  <div data-feature="game-source"><button>Project</button></div>
  <div class="group/home-suggestions"><button><span><span>Suggestion</span></span><span>Open</span></button></div>
  <div class="composer-surface-chrome"><button>Send</button></div>
 </div>
</main></body></html>""",
        encoding="utf-8",
    )


def write_node_driver(path):
    path.write_text(
        r'''const fs = require("fs");
const ws = new WebSocket(process.argv[2]);
const manifest = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
const outputDir = process.argv[4];
const outputFile = process.argv[5];
let nextId = 1;
const pending = new Map();
let blockedHttp = 0;

function call(method, params) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params: params || {} }));
  });
}
function evaluate(expression) {
  return call("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })
    .then(message => {
      if (message.result && message.result.exceptionDetails) {
        throw new Error(JSON.stringify(message.result.exceptionDetails));
      }
      return message.result && message.result.result ? message.result.result.value : undefined;
    });
}
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }
function timed(promise, ms, label) {
  let timer;
  const timeout = new Promise((resolve, reject) => {
    timer = setTimeout(() => reject(new Error(label + " timeout")), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
function read(pathName) { return fs.readFileSync(pathName, "utf8"); }
function expressionCall(pathName) { return evaluate(read(pathName)); }

const fixture = `(() => {
  const main = document.querySelector("main");
  if (main) main.remove();
  const next = document.createElement("main");
  next.className = "native-main-class _MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2";
  next.innerHTML = '<div role="main" data-testid="home-icon"><div data-feature="game-source"><button>Project</button></div><div class="group/home-suggestions"><button><span><span>Suggestion</span></span><span>Open</span></button></div><div class="composer-surface-chrome"><button>Send</button></div></div>';
  document.body.appendChild(next);
  return true;
})()`;
const conversation = `(() => {
  const main = document.querySelector("main");
  if (!main) return false;
  main.innerHTML = '<div role="main"><div data-thread-user-message-navigation-item-id="thread-1"></div><article data-message-author-role="user">Conversation</article><div class="composer-surface-chrome"><button>Send</button></div></div>';
  return true;
})()`;
const system = `(() => {
  const main = document.querySelector("main");
  if (main) main.remove();
  const settings = document.createElement("section");
  settings.className = "native-settings-page";
  settings.innerHTML = "<h1>Settings</h1>";
  document.body.appendChild(settings);
  return true;
})()`;
const systemWithMain = `(() => {
  const main = document.querySelector("main");
  if (!main) return false;
  main.innerHTML = '<div role="main" class="native-settings-content"><h1>Settings</h1></div>';
  return true;
})()`;
const addForeignDecor = `(() => {
  const chrome = document.createElement("div");
  chrome.id = "codex-dream-skin-chrome";
  chrome.setAttribute("data-foreign-owner", "native");
  chrome.innerHTML = '<b>foreign chrome</b>';
  const pola = document.createElement("div");
  pola.id = "ds-polaroid-draggable";
  pola.setAttribute("data-foreign-owner", "native");
  pola.innerHTML = '<span>foreign polaroid</span>';
  document.body.append(chrome, pola);
  return true;
})()`;
const foreignSnapshot = `(() => ["codex-dream-skin-chrome", "ds-polaroid-draggable"].map(id => {
  const node = document.getElementById(id);
  return node ? {id, html:node.innerHTML, attrs:Array.from(node.attributes).map(a => [a.name,a.value])} : null;
}))()`;
const clearForeignDecor = `(() => { document.getElementById("codex-dream-skin-chrome")?.remove(); document.getElementById("ds-polaroid-draggable")?.remove(); return true; })()`;

function extract(value, sources, owner) {
  if (!value || value === "none") return;
  let offset = 0;
  while (true) {
    const data = value.indexOf("data:image/", offset);
    const blob = value.indexOf("blob:", offset);
    const start = data < 0 ? blob : blob < 0 ? data : Math.min(data, blob);
    if (start < 0) return;
    const open = value.lastIndexOf("url(", start);
    const lead = value.slice(open + 4).trim();
    const quote = lead.charCodeAt(0);
    const endChar = quote === 34 ? '"' : quote === 39 ? "'" : ")";
    const tail = value.slice(start);
    const end = tail.indexOf(endChar);
    const source = tail.slice(0, end < 0 ? tail.length : end);
    if (!source) return;
    let hash = 2166136261;
    for (let i = 0; i < source.length; i++) hash = Math.imul(hash ^ source.charCodeAt(i), 16777619);
    const key = source.slice(0, source.indexOf(";") + 1) + source.length + ":" + (hash >>> 0).toString(16);
    if (!sources.has(key)) sources.set(key, { key, source, owner });
    offset = start + source.length;
  }
}
function decode(source) {
  return new Promise(resolve => {
    const image = new Image();
    let done = false;
    const finish = (loaded, width, height) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      resolve({ loaded, width: width || image.naturalWidth || 0, height: height || image.naturalHeight || 0 });
    };
    const timer = setTimeout(() => finish(false, 0, 0), 12000);
    image.onload = () => finish(true, 0, 0);
    image.onerror = () => fetch(source).then(r => r.blob()).then(b => createImageBitmap(b))
      .then(bitmap => finish(true, bitmap.width, bitmap.height)).catch(() => finish(false, 0, 0));
    image.src = source;
  });
}
async function inspect(label) {
  const labelValue = JSON.stringify(label);
  const value = await evaluate(`(async () => {
    const root = document.documentElement, main = document.querySelector("main");
    const chrome = document.getElementById("codex-dream-skin-chrome");
    const pola = document.getElementById("ds-polaroid-draggable");
    const sources = new Map();
    const extract = (value, sources, owner) => {
      if (!value || value === "none") return;
      let offset = 0;
      while (true) {
        const data = value.indexOf("data:image/", offset), blob = value.indexOf("blob:", offset);
        const start = data < 0 ? blob : blob < 0 ? data : Math.min(data, blob);
        if (start < 0) return;
        const open = value.lastIndexOf("url(", start), lead = value.slice(open + 4).trim();
        const quote = lead.charCodeAt(0), endChar = quote === 34 ? '"' : quote === 39 ? "'" : ")";
        const tail = value.slice(start), end = tail.indexOf(endChar), source = tail.slice(0, end < 0 ? tail.length : end);
        if (!source) return;
        let hash = 2166136261;
        for (let i = 0; i < source.length; i++) hash = Math.imul(hash ^ source.charCodeAt(i), 16777619);
        const key = source.slice(0, source.indexOf(";") + 1) + source.length + ":" + (hash >>> 0).toString(16);
        if (!sources.has(key)) sources.set(key, { key, source, owner });
        offset = start + source.length;
      }
    };
    const decode = source => new Promise(resolve => {
      const image = new Image(); let done = false;
      const finish = (loaded, width, height) => { if (done) return; done = true; clearTimeout(timer); resolve({loaded, width:width || image.naturalWidth || 0, height:height || image.naturalHeight || 0}); };
      const timer = setTimeout(() => finish(false, 0, 0), 12000);
      image.onload = () => finish(true, 0, 0);
      image.onerror = () => fetch(source).then(r => r.blob()).then(b => createImageBitmap(b)).then(bitmap => finish(true, bitmap.width, bitmap.height)).catch(() => finish(false, 0, 0));
      image.src = source;
    });
    const nodes = Array.from(document.querySelectorAll("*"));
    const scan = (node, owner) => {
      for (const style of [getComputedStyle(node), getComputedStyle(node, "::before"), getComputedStyle(node, "::after")]) {
        for (const prop of ["backgroundImage", "maskImage", "webkitMaskImage", "content"]) extract(style[prop], sources, owner + ":" + prop);
      }
    };
    for (const node of nodes) scan(node, node.tagName.toLowerCase());
    const images = [];
    for (const item of sources.values()) {
      const result = await decode(item.source);
      images.push({ key:item.key, owner:item.owner, length:item.source.length, loaded:result.loaded, width:result.width, height:result.height });
    }
    const attrs = ["data-midweb-dream-theme","data-dream-shell","data-dream-art-wide","data-dream-art-safe","data-dream-task-mode","data-dream-art-ready","data-dream-theme-variant","data-ds-route"];
    const rootAttrs = Object.fromEntries(attrs.map(name => [name, root.getAttribute(name)]));
    return { label:${labelValue}, rootClass:root.className, rootAttrs, mainClass:main && main.className,
      mainHome:Boolean(main && main.querySelector('[role="main"].dream-skin-home')),
      homeShell:Boolean(main && main.classList.contains("dream-skin-home-shell")),
      style:Boolean(document.getElementById("midweb-codexthemes-runtime-style") || document.getElementById("codex-dream-skin-style")),
      chrome:Boolean(chrome), chromeHome:Boolean(chrome && chrome.classList.contains("dream-skin-home-shell")),
      chromeText:chrome && chrome.querySelector(".dream-skin-brand b") && chrome.querySelector(".dream-skin-brand b").textContent,
      polaroid:pola ? {display:getComputedStyle(pola).display,left:pola.style.left,top:pola.style.top,transform:pola.style.transform,photo:Boolean(pola.querySelector(".ds-pola-photo"))} : null,
      imageCount:images.length, images, nativeMain:Boolean(main && main.classList.contains("native-main-class")),
      ownedDreamNodes:document.querySelectorAll('[data-midweb-dream-owned="owned"],[data-midweb-dream-part="owned"],[data-midweb-dream-class="owned"]').length};
  })()`);
  return value;
}
async function gesture() {
  const result = await evaluate(`(() => {
    const el = document.getElementById("ds-polaroid-draggable");
    if (!el) return { exists:false };
    const before = { left:el.style.left, top:el.style.top, transform:el.style.transform };
    const r = el.getBoundingClientRect();
    el.dispatchEvent(new PointerEvent("pointerdown", { bubbles:true, clientX:r.left+20, clientY:r.top+20, pointerId:1, buttons:1 }));
    window.dispatchEvent(new PointerEvent("pointermove", { bubbles:true, clientX:r.left+55, clientY:r.top+45, pointerId:1, buttons:1 }));
    window.dispatchEvent(new PointerEvent("pointerup", { bubbles:true, clientX:r.left+55, clientY:r.top+45, pointerId:1 }));
    const dragged = { left:el.style.left, top:el.style.top, transform:el.style.transform };
    const h = el.querySelector(".ds-pola-handle"), hr = h.getBoundingClientRect(), c = el.getBoundingClientRect();
    h.dispatchEvent(new PointerEvent("pointerdown", { bubbles:true, clientX:hr.left+2, clientY:hr.top+2, pointerId:2, buttons:1 }));
    window.dispatchEvent(new PointerEvent("pointermove", { bubbles:true, clientX:c.right+40, clientY:c.bottom+5, pointerId:2, buttons:1 }));
    window.dispatchEvent(new PointerEvent("pointerup", { bubbles:true, clientX:c.right+40, clientY:c.bottom+5, pointerId:2 }));
    const resized = { left:el.style.left, top:el.style.top, transform:el.style.transform };
    const readTransform = value => { const m = /rotate\\(([-\\d.]+)deg\\)\\s*scale\\(([-\\d.]+)\\)/.exec(value || ""); return m ? { rotation:Number(m[1]), scale:Number(m[2]) } : null; };
    const a = readTransform(dragged.transform), b = readTransform(resized.transform);
    return { exists:true, before, dragged, resized, transformChanged:Boolean(a && b && (a.rotation!==b.rotation || a.scale!==b.scale)), scaleChanged:Boolean(a && b && a.scale!==b.scale), rotationChanged:Boolean(a && b && a.rotation!==b.rotation) };
  })()`);
  return result;
}

async function run() {
  const result = { state:"failed", blockedHttp:0, themes:[] };
  const native = await inspect("native");
  result.native = native;
  const originalMainClass = native.mainClass;
  try {
    const dreamItems = manifest.items.filter(x => x.state === "prepared" && x.packageId !== "gpt");
    const ordinary = manifest.items.find(x => x.state === "prepared" && x.packageId === "gpt");
    for (const item of dreamItems) {
      const theme = { id:item.id, packageId:item.packageId, state:"failed" };
      const install = await expressionCall(item.installPath);
      await sleep(350);
      theme.installed = install === true;
      theme.home = await inspect(item.packageId + ":home");
      theme.verified = (await expressionCall(item.verifyPath)) === true;
      theme.homeImagesValid = theme.home.images.length > 0 && theme.home.images.every(x => x.loaded && x.width > 0 && x.height > 0);
      theme.chrome = theme.home.chrome === true && Boolean(theme.home.chromeText);
      theme.polaroid = theme.home.polaroid;
      if (item.packageId === "custom-1784518930245") theme.gesture = await gesture();

      await evaluate(conversation); await sleep(180);
      theme.conversation = await inspect(item.packageId + ":conversation");
      await evaluate(system); await sleep(180);
      theme.system = await inspect(item.packageId + ":system");
      await evaluate(fixture); await sleep(250);
      theme.homeAfterSpa = await inspect(item.packageId + ":home-after-spa");
      await evaluate(systemWithMain); await sleep(180);
      theme.systemWithMain = await inspect(item.packageId + ":system-with-main");
      await evaluate(fixture); await sleep(250);
      if (item.packageId === "custom-1784518930245") theme.polaroidAfterHome = theme.homeAfterSpa.polaroid;
      const expectedChrome = true;
      const expectedPolaroid = item.packageId === "custom-1784518930245";
      theme.state = theme.installed && theme.verified && theme.homeImagesValid && theme.chrome === expectedChrome &&
        (!expectedPolaroid || (theme.polaroid && theme.polaroid.photo)) &&
        theme.home.rootAttrs["data-midweb-dream-theme"] === item.id &&
        theme.home.rootAttrs["data-ds-route"] === "app" && theme.home.mainHome && theme.home.homeShell &&
        theme.conversation.rootAttrs["data-ds-route"] === "app" && !theme.conversation.mainHome &&
        theme.system.rootAttrs["data-ds-route"] === "settings" && !theme.system.chromeHome &&
        theme.homeAfterSpa.rootAttrs["data-ds-route"] === "app" && theme.homeAfterSpa.mainHome &&
        theme.systemWithMain.rootAttrs["data-ds-route"] === "settings" && theme.systemWithMain.mainClass !== null &&
        !theme.systemWithMain.homeShell && !theme.systemWithMain.chromeHome &&
        (item.packageId !== "custom-1784518930245" || (theme.gesture && theme.gesture.exists && theme.gesture.transformChanged && (theme.gesture.scaleChanged || theme.gesture.rotationChanged)))
        ? "passed" : "failed";
      if (item.packageId === "custom-bocchi-gotoh" && ordinary) {
        theme.directSwitchInstalled = (await expressionCall(ordinary.installPath)) === true;
        await sleep(220);
        theme.directSwitch = await inspect(item.packageId + ":after-gpt-switch");
        theme.directSwitchVerified = (await expressionCall(ordinary.verifyPath)) === true;
        theme.removed = (await expressionCall(ordinary.removePath)) === true;
      } else {
        theme.removed = (await expressionCall(item.removePath)) === true;
      }
      await sleep(180);
      theme.afterRestore = await inspect(item.packageId + ":restore");
      theme.listenerClean = await evaluate(`(() => { const n=document.createElement("div"); document.body.appendChild(n); return true; })()`)
        .then(() => sleep(180)).then(() => evaluate(`(() => Boolean(!document.getElementById("codex-dream-skin-chrome") && !document.getElementById("ds-polaroid-draggable")))()`));
      theme.restoreClean = theme.removed && !theme.afterRestore.style && !theme.afterRestore.rootClass.includes("codex-dream-skin") &&
        theme.afterRestore.mainClass === originalMainClass &&
        theme.afterRestore.rootAttrs["data-dream-shell"] === "native-shell" &&
        theme.afterRestore.rootAttrs["data-dream-art-wide"] === "native-wide" &&
        theme.afterRestore.rootAttrs["data-dream-art-safe"] === "native-safe" &&
        theme.afterRestore.rootAttrs["data-dream-task-mode"] === "native-mode" &&
        theme.afterRestore.rootAttrs["data-ds-route"] === "native-route" &&
        theme.afterRestore.rootAttrs["data-midweb-dream-theme"] === null &&
        theme.afterRestore.ownedDreamNodes === 0 && theme.listenerClean;
      if (theme.directSwitch) {
        theme.directSwitchClean = theme.directSwitch.rootAttrs["data-midweb-dream-theme"] === null &&
          !theme.directSwitch.rootClass.includes("codex-dream-skin") && !theme.directSwitch.chrome && !theme.directSwitch.polaroid;
        theme.restoreClean = theme.restoreClean && theme.directSwitchInstalled && theme.directSwitchVerified && theme.directSwitchClean;
      }
      await evaluate(addForeignDecor);
      const foreignBefore = await evaluate(foreignSnapshot);
      const foreignInstalled = (await expressionCall(item.installPath)) === true;
      await sleep(220);
      const foreignDuring = await evaluate(foreignSnapshot);
      const foreignRemoved = (await expressionCall(item.removePath)) === true;
      await sleep(180);
      const foreignAfter = await evaluate(foreignSnapshot);
      theme.foreignPreserved = foreignInstalled && foreignRemoved && JSON.stringify(foreignBefore) === JSON.stringify(foreignDuring) && JSON.stringify(foreignBefore) === JSON.stringify(foreignAfter);
      await evaluate(clearForeignDecor);
      theme.restoreClean = theme.restoreClean && theme.foreignPreserved;
      if (theme.state === "passed" && !theme.restoreClean) theme.state = "failed";
      result.themes.push(theme);
    }
    result.blockedHttp = blockedHttp;
    result.state = result.themes.length === 2 && result.themes.every(x => x.state === "passed") && blockedHttp === 0 ? "passed" : "failed";
  } catch (error) { result.error = String(error); }
  fs.writeFileSync(outputFile, JSON.stringify(result, null, 2));
  if (result.state !== "passed") process.exitCode = 1;
  ws.close();
}
ws.addEventListener("open", async () => {
  try {
    await call("Network.enable");
    await call("Fetch.enable", { patterns:[{urlPattern:"http://*/*",requestStage:"Request"},{urlPattern:"https://*/*",requestStage:"Request"}] });
    await call("Page.enable");
    await run();
  } catch (error) { fs.writeFileSync(outputFile, JSON.stringify({state:"failed",error:String(error)}, null, 2)); process.exitCode=1; ws.close(); }
});
ws.addEventListener("message", event => {
  const message = JSON.parse(event.data);
  if (message.method === "Fetch.requestPaused") {
    blockedHttp += 1;
    ws.send(JSON.stringify({id:nextId++,method:"Fetch.failRequest",params:{requestId:message.params.requestId,errorReason:"BlockedByClient"}}));
    return;
  }
  const item = pending.get(message.id);
  if (!item) return;
  pending.delete(message.id);
  if (message.error) item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message);
});
''',
        encoding="utf-8",
    )


def main():
    browser = find_browser()
    output = ROOT / "test-output" / "dream-theme-browser" / ("run-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix="midweb-dream-browser-", ignore_cleanup_errors=True) as work_name:
        work = pathlib.Path(work_name)
        harness = compile_preparation_harness(str(work))
        prepared = work / "prepared"
        env = os.environ.copy()
        env["MIDWEB_THEME_IDS"] = ",".join(IDS)
        subprocess.check_call([harness, str(ROOT / "assets" / "theme-gallery"), str(prepared)], env=env)
        manifest = json.loads((prepared / "preparation.json").read_text(encoding="utf-8"))
        if manifest.get("preparedCount") != 3:
            raise RuntimeError("Dream package preparation did not produce both packages: " + json.dumps(manifest, ensure_ascii=False))
        fixture = work / "fixture.html"
        write_fixture(fixture)
        node = work / "dream-driver.js"
        write_node_driver(node)
        port = free_port()
        profile = work / "browser-profile"
        fixture_url = "file:///" + str(fixture).replace("\\", "/")
        browser_process = subprocess.Popen([
            browser, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + str(port),
            "--user-data-dir=" + str(profile), fixture_url,
        ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            ws_url = wait_for_page(port, fixture_url)
            result_path = output / "dream-results.json"
            completed = subprocess.run(["node", str(node), ws_url, str(prepared / "preparation.json"), str(output), str(result_path)],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            if result_path.is_file():
                result = json.loads(result_path.read_text(encoding="utf-8"))
                print(json.dumps({"state": result.get("state"), "blockedHttp": result.get("blockedHttp"),
                                  "themes": [(x.get("packageId"), x.get("state")) for x in result.get("themes", [])]}, ensure_ascii=False))
            else:
                result = {"state": "failed", "error": completed.stderr.decode("utf-8", "replace")}
            if completed.returncode != 0 or result.get("state") != "passed":
                raise RuntimeError(json.dumps(result, ensure_ascii=False))
        finally:
            if browser_process.poll() is None:
                subprocess.run(["taskkill", "/PID", str(browser_process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                try:
                    browser_process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    browser_process.kill()
                    browser_process.wait(timeout=5)
        shutil.copy2(prepared / "preparation.json", output / "preparation.json")
    print("Dream theme browser regression passed")


if __name__ == "__main__":
    main()
