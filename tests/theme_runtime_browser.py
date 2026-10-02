import glob
import json
import os
import socket
import subprocess
import tempfile
import time
import urllib.request


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSC = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")
REFERENCES = ["/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Net.Http.dll",
              "/r:System.Web.Extensions.dll", "/r:System.Security.dll"]


def free_port():
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


def find_browser():
    candidates = [
        os.environ.get("MIDWEB_BROWSER", ""),
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ]
    for candidate in candidates:
        if candidate and os.path.isfile(candidate):
            return candidate
    raise RuntimeError("未找到 Edge/Chrome；可用 MIDWEB_BROWSER 指定 headless 浏览器路径。")


def find_gpt_package():
    for path in glob.glob(os.path.join(ROOT, "assets", "theme-gallery", "*.codex-theme")):
        try:
            with open(path, "r", encoding="utf-8") as stream:
                package = json.load(stream)
            if package.get("manifest", {}).get("id") == "gpt":
                return path, package
        except (OSError, ValueError):
            continue
    raise RuntimeError("离线 gpt 主题包不存在。")


def build_probe_css(package):
    art = package.get("art", {})
    data = "data:" + art["mimeType"] + ";base64," + art["base64"]
    return ("main.main-surface[data-codexthemes-page=\"home\"]::after{content:\"\";"
            "position:absolute;inset:0;background-image:url(\"" + data + "\") !important;}")


def compile_expression_harness(work):
    source = os.path.join(work, "ExpressionHarness.cs")
    with open(source, "w", encoding="utf-8") as stream:
        stream.write(r'''
using System;
using System.IO;
using System.Reflection;
using System.Text;

internal static class ExpressionHarness
{
    private static int Main(string[] args)
    {
        try
        {
            byte[] packageBytes = File.ReadAllBytes(args[0]);
            CodexThemePackage package = GalleryCatalog.ParsePackage(packageBytes, "gpt");
            string css = package.AppliedCss + "\n" + File.ReadAllText(args[1], Encoding.UTF8);
            Type type = typeof(ThemeRuntime);
            string install = (string)type.GetMethod("BuildInstallExpression", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { "gpt", css, "dark", "home" });
            string verify = (string)type.GetMethod("BuildVerifyExpression", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { "gpt", "dark", "home" });
            string remove = (string)type.GetMethod("BuildRemoveExpression", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
            string removeVerify = (string)type.GetMethod("BuildRemoveVerifyExpression", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
            File.WriteAllText(args[2], install, Encoding.UTF8);
            File.WriteAllText(args[3], verify, Encoding.UTF8);
            File.WriteAllText(args[4], remove, Encoding.UTF8);
            File.WriteAllText(args[5], removeVerify, Encoding.UTF8);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
''')
    executable = os.path.join(work, "ExpressionHarness.exe")
    sources = [os.path.join(ROOT, "src", name) for name in os.listdir(os.path.join(ROOT, "src"))
               if name.endswith(".cs") and name not in ("Program.cs", "MainForm.cs", "CredentialProgram.cs")]
    command = [CSC, "/nologo", "/target:exe", "/optimize+", "/platform:anycpu", "/langversion:5",
               "/out:" + executable] + REFERENCES + sources + [source]
    subprocess.check_call(command)
    return executable


def write_node_driver(path):
    with open(path, "w", encoding="utf-8") as stream:
        stream.write(r'''
const fs = require("fs");
const ws = new WebSocket(process.argv[2]);
const install = fs.readFileSync(process.argv[3], "utf8");
const verify = fs.readFileSync(process.argv[4], "utf8");
const remove = fs.readFileSync(process.argv[5], "utf8");
const removeVerify = fs.readFileSync(process.argv[6], "utf8");
const output = process.argv[7];
let nextId = 1;
const pending = new Map();

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

function snapshot() {
  return evaluate(`(() => {
    const root = document.documentElement;
    const main = document.querySelector("main");
    const style = document.getElementById("midweb-codexthemes-runtime-style");
    const computed = main ? getComputedStyle(main) : null;
    return {
      page: main && main.getAttribute("data-codexthemes-page"),
      ownedPage: main && main.getAttribute("data-midweb-theme-page"),
      background: computed && computed.backgroundColor,
      foreground: computed && computed.color,
      styleOwner: style && style.getAttribute("data-owner"),
      theme: root.getAttribute("data-codex-theme"),
      mode: root.getAttribute("data-codex-theme-mode"),
      scope: root.getAttribute("data-codexthemes-background-scope"),
      foreignClass: root.classList.contains("foreign-shell"),
      darkClass: root.classList.contains("dark")
    };
  })()`);
}

function artSnapshot() {
  return evaluate(`(async () => {
    const main = document.querySelector("main");
    const root = document.documentElement;
    const direct = main ? getComputedStyle(main, "::before").backgroundImage : "";
    const variable = main ? getComputedStyle(main, "::after").backgroundImage : "";
    const customProperty = getComputedStyle(root).getPropertyValue("--ct-art").trim();
    const extract = value => {
      if (!value) return null;
      const dataStart = value.indexOf("data:image/");
      const blobStart = value.indexOf("blob:");
      const start = dataStart < 0 ? blobStart : blobStart < 0 ? dataStart : Math.min(dataStart, blobStart);
      if (start < 0) return null;
      const source = value.slice(start);
      const open = value.lastIndexOf("url(", start);
      const lead = value.slice(open + 4).trim();
      const quote = lead.charCodeAt(0);
      const endChar = quote === 34 ? String.fromCharCode(34) : quote === 39 ? String.fromCharCode(39) : ")";
      const end = source.indexOf(endChar);
      return source.slice(0, end < 0 ? source.length : end);
    };
    const decode = value => new Promise(resolve => {
      const source = extract(value);
      if (!source) { resolve({ loaded: false, width: 0, height: 0, sourceLength: 0, sourcePrefix: "" }); return; }
      const image = new Image();
      let finished = false;
      const finish = loaded => {
        if (finished) return;
        finished = true;
        resolve({ loaded, width: image.naturalWidth || 0, height: image.naturalHeight || 0,
          sourceLength: source.length, sourcePrefix: source.slice(0, 30) });
      };
      image.onload = () => finish(true);
      image.onerror = () => {
        fetch(source).then(response => response.blob()).then(blob => createImageBitmap(blob))
          .then(bitmap => { if (!finished) { finished = true; resolve({ loaded: true, width: bitmap.width, height: bitmap.height, sourceLength: source.length, sourcePrefix: source.slice(0, 30) }); } })
          .catch(() => finish(false));
      };
      image.src = source;
      setTimeout(() => finish(false), 15000);
    });
    return {
      directNonNone: direct !== "none" && direct.length > 20,
      variableNonNone: variable !== "none" && variable.length > 20,
      directLength: direct.length,
      variableLength: variable.length,
      customPropertyLength: customProperty.length,
      directImage: await decode(direct),
      variableImage: await decode(variable)
    };
  })()`);
}

function runtimeObjectUrls() {
  return evaluate(`(() => {
    const state = globalThis.__midwebThemeRuntime;
    return state && Array.isArray(state.objectUrls) ? state.objectUrls.slice() : [];
  })()`);
}

function revokedUrls(urls) {
  return evaluate(`(async () => {
    const urls = ${JSON.stringify(urls)};
    if (!urls.length) return false;
    const results = await Promise.all(urls.map(url => fetch(url).then(() => false).catch(() => true)));
    return results.every(Boolean);
  })()`);
}

ws.addEventListener("open", async () => {
  try {
    const before = await snapshot();
    const installed = await evaluate(install);
    const initial = await snapshot();
    const artInitial = await artSnapshot();
    const verified = await evaluate(verify);
    const initialObjectUrls = await runtimeObjectUrls();
    const reapplied = await evaluate(install);
    const replacementObjectUrls = await runtimeObjectUrls();
    const previousObjectUrlsRevoked = await revokedUrls(initialObjectUrls);
    const reverified = await evaluate(verify);
    await evaluate(`(() => {
      const oldMain = document.querySelector("main");
      if (oldMain) oldMain.remove();
      const next = document.createElement("main");
      next.className = "_MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2";
      next.innerHTML = "<div data-thread-user-message-navigation-item-id='thread-1'></div>";
      document.body.appendChild(next);
      return true;
    })()`);
    await new Promise(resolve => setTimeout(resolve, 120));
    const spa = await snapshot();
    const restored = await evaluate(remove);
    const replacementObjectUrlsRevoked = await revokedUrls(replacementObjectUrls);
    const removeVerified = await evaluate(removeVerify);
    const afterRestore = await snapshot();
    const result = { before, installed, initial, artInitial, verified, initialObjectUrls,
      reapplied, replacementObjectUrls, previousObjectUrlsRevoked, reverified, spa,
      restored, replacementObjectUrlsRevoked, removeVerified, afterRestore };
    fs.writeFileSync(output, JSON.stringify(result));
    if (!installed || !verified || !reapplied || !reverified || !restored || !removeVerified ||
        !initialObjectUrls.length || !replacementObjectUrls.length || !previousObjectUrlsRevoked || !replacementObjectUrlsRevoked ||
        initial.page !== "home" || spa.page !== "conversation" ||
        initial.background !== "rgb(16, 24, 39)" || initial.foreground !== "rgb(245, 242, 234)" ||
        initial.styleOwner !== "midweb" || initial.darkClass !== true || !artInitial.directNonNone || !artInitial.variableNonNone ||
        !artInitial.directImage.loaded || !artInitial.variableImage.loaded ||
        artInitial.directImage.width <= 0 || artInitial.directImage.height <= 0 || artInitial.variableImage.width <= 0 || artInitial.variableImage.height <= 0 ||
        afterRestore.styleOwner || afterRestore.theme !== "native-theme" ||
        afterRestore.mode !== "light" || afterRestore.scope !== "home" || afterRestore.foreignClass !== true || afterRestore.darkClass !== false ||
        afterRestore.ownedPage) process.exitCode = 1;
    ws.close();
  } catch (error) {
    fs.writeFileSync(output, JSON.stringify({ error: String(error) }));
    process.exitCode = 1;
    ws.close();
  }
});

ws.addEventListener("message", event => {
  const message = JSON.parse(event.data);
  const item = pending.get(message.id);
  if (!item) return;
  pending.delete(message.id);
  if (message.error) item.reject(new Error(JSON.stringify(message.error)));
  else item.resolve(message);
});
''')


def wait_for_page(port, fixture_url):
    deadline = time.time() + 15
    url = "http://127.0.0.1:%d/json/list" % port
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                pages = json.loads(response.read().decode("utf-8"))
                for page in pages:
                    page_url = page.get("url", "")
                    if page.get("webSocketDebuggerUrl") and (page_url == fixture_url or page_url.endswith("/fixture.html")):
                        return page["webSocketDebuggerUrl"]
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("headless 浏览器未在限定时间内开放 CDP 页面。")


def main():
    browser = find_browser()
    package_path, package = find_gpt_package()
    with tempfile.TemporaryDirectory(prefix="midweb-theme-browser-", ignore_cleanup_errors=True) as work:
        fixture = os.path.join(work, "fixture.html")
        with open(fixture, "w", encoding="utf-8") as stream:
            stream.write("""<!doctype html><html class=\"foreign-shell\" data-codex-theme=\"native-theme\" data-codex-theme-mode=\"light\" data-codexthemes-theme=\"native-theme\" data-codexthemes-background-scope=\"home\"><head></head><body><aside class=\"app-shell-left-panel\"></aside><main class=\"_MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2\"><div data-composer-navigation-target></div></main></body></html>""")
        css = os.path.join(work, "fixture.css")
        with open(css, "w", encoding="utf-8") as stream:
            stream.write(build_probe_css(package))
        install = os.path.join(work, "install.js")
        verify = os.path.join(work, "verify.js")
        remove = os.path.join(work, "remove.js")
        remove_verify = os.path.join(work, "remove-verify.js")
        harness = compile_expression_harness(work)
        subprocess.check_call([harness, package_path, css, install, verify, remove, remove_verify])
        node = os.path.join(work, "driver.js")
        write_node_driver(node)
        port = free_port()
        profile = os.path.join(work, "browser-profile")
        browser_process = subprocess.Popen([
            browser, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + str(port),
            "--user-data-dir=" + profile, "file:///" + fixture.replace("\\", "/")
        ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            fixture_url = "file:///" + fixture.replace("\\", "/")
            ws_url = wait_for_page(port, fixture_url)
            output = os.path.join(work, "browser-result.json")
            completed = subprocess.run(["node", node, ws_url, install, verify, remove, remove_verify, output],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
            if completed.returncode != 0:
                details = open(output, encoding="utf-8").read() if os.path.isfile(output) else completed.stderr.decode("utf-8", "replace")
                raise RuntimeError("浏览器主题 fixture 回归失败：" + details)
            print(open(output, encoding="utf-8").read())
        finally:
            if browser_process.poll() is None:
                subprocess.run(["taskkill", "/PID", str(browser_process.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                try:
                    browser_process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    browser_process.kill()
                    browser_process.wait(timeout=5)
    print("theme runtime browser regression passed")


if __name__ == "__main__":
    main()
