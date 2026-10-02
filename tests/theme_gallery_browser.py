import json
import os
import socket
import subprocess
import tempfile
import time
import urllib.request
from datetime import datetime


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


def compile_preparation_harness(work):
    source = os.path.join(work, "GalleryBrowserPreparation.cs")
    with open(source, "w", encoding="utf-8") as stream:
        stream.write(r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class OfflineTransport : IGalleryTransport
{
    public Task<GalleryHttpResponse> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        throw new WebException("offline transport");
    }
}

internal static class GalleryBrowserPreparation
{
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    static string Text(object value) { return value == null ? null : Convert.ToString(value); }

    static Dictionary<string, object> Record(GalleryTheme theme)
    {
        return new Dictionary<string, object> {
            { "id", theme.Id }, { "name", theme.Name }, { "kind", theme.Kind },
            { "installable", theme.Installable }, { "canInstall", theme.CanInstall },
            { "mode", theme.Mode }, { "downloadUrl", theme.DownloadUrl },
            { "offlinePackageStatus", theme.OfflinePackageStatus },
            { "offlinePackageError", theme.OfflinePackageError },
            { "offlinePackageBytes", theme.OfflinePackageBytes }
        };
    }

    static string Mode(CodexThemePackage package, GalleryTheme theme)
    {
        return String.IsNullOrWhiteSpace(package.Mode)
            ? (String.IsNullOrWhiteSpace(theme.Mode) ? "mixed" : theme.Mode)
            : package.Mode;
    }

    static void MainWork(string[] args)
    {
        string offline = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var catalog = new GalleryCatalog(Path.Combine(output, "cache"), new OfflineTransport(), offline);
        IList<GalleryTheme> themes = catalog.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        string selected = Environment.GetEnvironmentVariable("MIDWEB_THEME_IDS");
        if (!String.IsNullOrWhiteSpace(selected))
        {
            var ids = new HashSet<string>(selected.Split(',').Select(x => x.Trim()), StringComparer.Ordinal);
            themes = themes.Where(x => ids.Contains(x.Id)).ToList();
            if (themes.Count != ids.Count) throw new InvalidOperationException("Requested theme ID missing from catalog.");
        }
        Type runtime = typeof(ThemeRuntime);
        MethodInfo installMethod = runtime.GetMethod("BuildInstallExpression", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo verifyMethod = runtime.GetMethod("BuildVerifyExpression", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo removeMethod = runtime.GetMethod("BuildRemoveExpression", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo removeVerifyMethod = runtime.GetMethod("BuildRemoveVerifyExpression", BindingFlags.Static | BindingFlags.NonPublic);
        var records = new List<Dictionary<string, object>>();
        for (int index = 0; index < themes.Count; index++)
        {
            GalleryTheme theme = themes[index];
            Dictionary<string, object> item = Record(theme);
            item["catalogIndex"] = index;
            try
            {
                if (!theme.CanInstall)
                {
                    item["state"] = "unavailable";
                    records.Add(item);
                    continue;
                }
                CodexThemePackage package = catalog.DownloadAsync(theme, CancellationToken.None).GetAwaiter().GetResult();
                string baseName = index.ToString("D3") + "-" + theme.Id;
                string cssPath = Path.Combine(output, baseName + ".css");
                string installPath = Path.Combine(output, baseName + ".install.js");
                string verifyPath = Path.Combine(output, baseName + ".verify.js");
                string removePath = Path.Combine(output, baseName + ".remove.js");
                string removeVerifyPath = Path.Combine(output, baseName + ".remove-verify.js");
                string mode = Mode(package, theme);
                File.WriteAllText(cssPath, package.AppliedCss, Utf8);
                File.WriteAllText(installPath, (string)installMethod.Invoke(null, new object[] { theme.Id, package.AppliedCss, mode, "home" }), Utf8);
                File.WriteAllText(verifyPath, (string)verifyMethod.Invoke(null, new object[] { theme.Id, mode, "home" }), Utf8);
                File.WriteAllText(removePath, (string)removeMethod.Invoke(null, null), Utf8);
                File.WriteAllText(removeVerifyPath, (string)removeVerifyMethod.Invoke(null, null), Utf8);
                item["state"] = "prepared";
                item["packageId"] = package.Id;
                item["runtimeId"] = theme.Id;
                item["modeApplied"] = mode;
                item["cssBytes"] = Utf8.GetByteCount(package.AppliedCss);
                item["artBytes"] = package.ArtBytes == null ? 0 : package.ArtBytes.Length;
                item["artFilename"] = package.ArtFilename;
                item["cssPath"] = cssPath;
                item["installPath"] = installPath;
                item["verifyPath"] = verifyPath;
                item["removePath"] = removePath;
                item["removeVerifyPath"] = removeVerifyPath;
            }
            catch (Exception error)
            {
                Exception cause = error;
                while (cause.InnerException != null) cause = cause.InnerException;
                item["state"] = "prepare-failed";
                item["errorType"] = cause.GetType().FullName;
                item["error"] = cause.Message;
            }
            records.Add(item);
        }
        var root = new Dictionary<string, object> {
            { "offlineDirectory", offline }, { "themeCount", themes.Count },
            { "preparedCount", records.Count(x => Text(x["state"]) == "prepared") },
            { "items", records }
        };
        var serializer = new JavaScriptSerializer { MaxJsonLength = SkinBridge.MaxCdpMessageBytes };
        File.WriteAllText(Path.Combine(output, "preparation.json"), serializer.Serialize(root), Utf8);
    }

    static int Main(string[] args)
    {
        try { MainWork(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
''')
    executable = os.path.join(work, "GalleryBrowserPreparation.exe")
    sources = [os.path.join(ROOT, "src", name) for name in os.listdir(os.path.join(ROOT, "src"))
               if name.endswith(".cs") and name not in ("Program.cs", "MainForm.cs", "CredentialProgram.cs")]
    command = [CSC, "/nologo", "/target:exe", "/optimize+", "/platform:anycpu",
               "/langversion:5", "/out:" + executable] + REFERENCES + sources + [source]
    subprocess.check_call(command)
    return executable


def write_node_driver(path):
    with open(path, "w", encoding="utf-8") as stream:
        stream.write(r'''
const fs = require("fs");
const path = require("path");
const ws = new WebSocket(process.argv[2]);
const manifest = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
const outputDir = process.argv[4];
let nextId = 1;
const pending = new Map();
let nativeBaseline = null;
let previousSwitch = null;

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
    timer = setTimeout(() => reject(new Error(label + " timeout " + ms + "ms")), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
function read(pathName) { return fs.readFileSync(pathName, "utf8"); }
function expressionCall(pathName) { return evaluate(read(pathName)); }

function capture(file) {
  return call("Page.captureScreenshot", { format: "png", captureBeyondViewport: true })
    .then(result => fs.writeFileSync(file, Buffer.from(result.result.data, "base64")));
}

function fixtureMain(conversation) {
  const html = conversation
    ? "<div data-thread-user-message-navigation-item-id='thread-1'></div><div class='composer-surface-chrome'></div>"
    : "<div data-composer-navigation-target></div><h1 data-codexthemes-surface='home-heading'>Home</h1><section class='group/home-suggestions'><div class='suggestion-card'>Suggestion</div></section><div class='composer-surface-chrome'><button>Send</button></div>";
  return evaluate("(() => { const oldMain=document.querySelector('main'); if(oldMain) oldMain.remove(); const next=document.createElement('main'); next.className='_MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2'; next.innerHTML=" + JSON.stringify(html) + "; document.body.appendChild(next); return true; })()");
}

function inspect() {
  const expression = [
    "(async () => {",
    "const root=document.documentElement, main=document.querySelector('main'), side=document.querySelector('aside.app-shell-left-panel'), composer=document.querySelector('.composer-surface-chrome');",
    "const elements=Array.from(document.querySelectorAll('*')), sources=new Map();",
    "const add=(value,owner)=>{if(!value||value==='none')return;let offset=0;while(true){const dataStart=value.indexOf('data:image/',offset),blobStart=value.indexOf('blob:',offset);const start=dataStart<0?blobStart:blobStart<0?dataStart:Math.min(dataStart,blobStart);if(start<0)break;const tail=value.slice(start),open=value.lastIndexOf('url(',start),lead=value.slice(open+4).trim(),quote=lead.charCodeAt(0),endChar=quote===34?String.fromCharCode(34):quote===39?String.fromCharCode(39):')',end=tail.indexOf(endChar),source=tail.slice(0,end<0?tail.length:end);if(!source)break;let hash=2166136261;for(let i=0;i<source.length;i++)hash=Math.imul(hash^source.charCodeAt(i),16777619);const separator=source.indexOf(';')+1;const key=source.slice(0,separator)+source.length+':' +(hash>>>0).toString(16);if(!sources.has(key))sources.set(key,{key,source,owner});offset=start+source.length;}};",
    "const scan=(node,owner)=>{const styles=[getComputedStyle(node),getComputedStyle(node,'::before'),getComputedStyle(node,'::after')];const props=['backgroundImage','maskImage','webkitMaskImage','listStyleImage','borderImageSource','content'];for(const style of styles)for(const prop of props)add(style[prop],owner+':'+prop);};",
    "for(const node of elements)scan(node,node.tagName.toLowerCase());",
    "const decode=value=>new Promise(resolve=>{const image=new Image();let finished=false,timer;const done=(loaded,width,height)=>{if(finished)return;finished=true;clearTimeout(timer);resolve({loaded,width:width||image.naturalWidth||0,height:height||image.naturalHeight||0});};image.onload=()=>done(true);image.onerror=()=>{fetch(value).then(response=>response.blob()).then(blob=>createImageBitmap(blob)).then(bitmap=>done(true,bitmap.width,bitmap.height)).catch(()=>done(false,0,0));};image.src=value;timer=setTimeout(()=>done(false,0,0),15000);});",
    "const dataImages=[];for(const value of sources.values()){const decoded=await decode(value.source);dataImages.push({key:value.key,owner:value.owner,length:value.source.length,loaded:decoded.loaded,width:decoded.width,height:decoded.height});}",
    "return {rootTheme:root.getAttribute('data-codex-theme'),rootMode:root.getAttribute('data-codex-theme-mode'),rootScope:root.getAttribute('data-codexthemes-background-scope'),darkClass:root.classList.contains('dark'),mainPage:main&&main.getAttribute('data-codexthemes-page'),mainOwnedPage:main&&main.getAttribute('data-midweb-theme-page'),mainHasSurfaceClass:Boolean(main&&main.classList.contains('main-surface')),styleOwner:(document.getElementById('midweb-codexthemes-runtime-style')||{}).getAttribute?document.getElementById('midweb-codexthemes-runtime-style').getAttribute('data-owner'):null,mainBackground:main?getComputedStyle(main).backgroundColor:null,mainColor:main?getComputedStyle(main).color:null,sidebarBackground:side?getComputedStyle(side).backgroundColor:null,composerBackground:composer?getComputedStyle(composer).backgroundColor:null,elementCount:elements.length,dataImageCount:dataImages.length,dataImages};",
    "})()"
  ].join("\n");
  return evaluate(expression);
}

async function runTheme(item) {
  const started = Date.now();
  const result = { id:item.id, catalogIndex:item.catalogIndex, artExpected:item.artBytes>0, state:"failed" };
  try {
    if (previousSwitch) {
      result.switchedFrom = previousSwitch.id;
      await timed(expressionCall(previousSwitch.installPath),60000,item.id+" switch predecessor");
      await sleep(80);
      result.switchVerified = (await inspect()).rootTheme === previousSwitch.runtimeId;
    }
    await evaluate("document.documentElement.classList.add('dark')");
    result.installed = (await timed(expressionCall(item.installPath),60000,item.id+" install")) === true;
    await sleep(100);
    result.verified = (await timed(expressionCall(item.verifyPath),30000,item.id+" verify")) === true;
    result.home = await timed(inspect(),30000,item.id+" home");
    result.artPresent = result.home.dataImageCount > 0;
    result.artReview = result.artExpected && !result.artPresent ? "review" : "ok";
    result.homeImagesValid = result.home.dataImages.every(image => image.loaded && image.width > 0 && image.height > 0);
    const expectedDark = String(item.modeApplied || item.mode || "mixed").toLowerCase() === "dark";
    const screenshot = path.join(outputDir,"screenshots",String(item.catalogIndex).padStart(3,"0")+"-"+item.id+".png");
    await capture(screenshot);
    result.screenshot = screenshot;
    await fixtureMain(true);
    await sleep(120);
    result.conversation = await timed(inspect(),30000,item.id+" conversation");
    result.conversationImagesValid = result.conversation.dataImages.every(image => image.loaded && image.width > 0 && image.height > 0);
    await fixtureMain(false);
    await sleep(120);
    result.homeAfterSpa = await timed(inspect(),30000,item.id+" home-after-spa");
    result.homeAfterSpaImagesValid = result.homeAfterSpa.dataImages.every(image => image.loaded && image.width > 0 && image.height > 0);
    result.removed = (await timed(expressionCall(item.removePath),30000,item.id+" remove")) === true;
    result.removeVerified = (await timed(expressionCall(item.removeVerifyPath),30000,item.id+" remove-verify")) === true;
    result.afterRestore = await timed(inspect(),30000,item.id+" restore");
    if (previousSwitch) {
      await timed(expressionCall(previousSwitch.removePath),30000,item.id+" switch cleanup");
      result.afterRestore = await timed(inspect(),30000,item.id+" switch restore");
    }
    result.state = result.installed && result.verified && result.home.mainPage==="home" &&
      result.conversation.mainPage==="conversation" && result.homeAfterSpa.mainPage==="home" &&
      result.homeImagesValid && result.conversationImagesValid && result.homeAfterSpaImagesValid &&
      (!previousSwitch || result.switchVerified) &&
      result.home.darkClass === expectedDark && result.afterRestore.darkClass === true &&
      result.removed && result.removeVerified && result.afterRestore.styleOwner===null &&
      result.afterRestore.rootTheme==="native-theme" && result.afterRestore.rootMode==="light" &&
      result.afterRestore.mainOwnedPage===null && result.afterRestore.mainHasSurfaceClass===false &&
      result.afterRestore.mainBackground===nativeBaseline.mainBackground &&
      result.afterRestore.mainColor===nativeBaseline.mainColor &&
      result.afterRestore.sidebarBackground===nativeBaseline.sidebarBackground &&
      result.afterRestore.composerBackground===nativeBaseline.composerBackground &&
      result.artReview==="ok" ? "passed" : "failed";
  } catch(error) {
    result.error = String(error);
    try { await timed(expressionCall(item.removePath),10000,item.id+" cleanup"); } catch(_) {}
    if (previousSwitch) {
      try { await timed(expressionCall(previousSwitch.removePath),10000,item.id+" predecessor cleanup"); } catch(_) {}
    }
  }
  result.elapsedMs = Date.now()-started;
  fs.writeFileSync(path.join(outputDir,"per-theme",String(item.catalogIndex).padStart(3,"0")+"-"+item.id+".json"),JSON.stringify(result,null,2));
  previousSwitch = item;
  return result;
}

ws.addEventListener("open", async () => {
  try {
    await call("Network.enable");
    await call("Fetch.enable", { patterns: [
      { urlPattern: "http://*/*", requestStage: "Request" },
      { urlPattern: "https://*/*", requestStage: "Request" }
    ] });
    await call("Page.enable");
    nativeBaseline = await inspect();
    const results=[];
    for(const item of manifest.items) {
      if(item.state!=="prepared") continue;
      const result=await runTheme(item);
      results.push(result);
      process.stdout.write(JSON.stringify({id:item.id,state:result.state,elapsedMs:result.elapsedMs})+"\\n");
    }
    fs.writeFileSync(path.join(outputDir,"browser-results.json"),JSON.stringify(results,null,2));
    ws.close();
  } catch(error) {
    fs.writeFileSync(path.join(outputDir,"browser-results.json"),JSON.stringify({error:String(error)},null,2));
    process.exitCode=1;
    ws.close();
  }
});
ws.addEventListener("message",event=>{
  const message=JSON.parse(event.data);
  if(message.method === "Fetch.requestPaused") {
    ws.send(JSON.stringify({id:nextId++,method:"Fetch.failRequest",params:{requestId:message.params.requestId,errorReason:"BlockedByClient"}}));
    return;
  }
  const item=pending.get(message.id);
  if(!item)return;
  pending.delete(message.id);
  if(message.error)item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message);
});
''')


def wait_for_page(port, fixture_url):
    deadline = time.time() + 20
    url = "http://127.0.0.1:%d/json/list" % port
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                for page in json.loads(response.read().decode("utf-8")):
                    if page.get("webSocketDebuggerUrl") and (
                        page.get("url", "") == fixture_url or page.get("url", "").endswith("/fixture.html")
                    ):
                        return page["webSocketDebuggerUrl"]
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("headless 浏览器未在限定时间内开放 fixture 页面。")


def main():
    browser = find_browser()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    output = os.path.join(ROOT, "test-output", "theme-gallery-browser", "run-" + stamp)
    os.makedirs(os.path.join(output, "per-theme"), exist_ok=True)
    os.makedirs(os.path.join(output, "screenshots"), exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="midweb-theme-gallery-browser-", ignore_cleanup_errors=True) as work:
        fixture = os.path.join(work, "fixture.html")
        with open(fixture, "w", encoding="utf-8") as stream:
            stream.write("""<!doctype html><html class="foreign-shell dark" data-codex-theme="native-theme" data-codex-theme-mode="light" data-codexthemes-theme="native-theme" data-codexthemes-background-scope="home"><head><style>html,body{margin:0;width:100%;height:100%;}body{display:flex;}aside{width:280px;flex:none;}main{position:relative;flex:1;min-height:820px;overflow:hidden;}.composer-surface-chrome{min-height:80px;}[data-codexthemes-surface="home-heading"]{font-size:24px;}button{min-height:32px;}</style></head><body><aside class="app-shell-left-panel"><div role="treeitem">Project</div></aside><main class="_MainContentSurface_a5et5_2 _MainContentLeftBorder_a5et5_2"><div data-composer-navigation-target></div><h1 data-codexthemes-surface="home-heading">Home</h1><section class="group/home-suggestions"><div class="suggestion-card">Suggestion</div></section><div class="composer-surface-chrome"><button>Send</button></div></main></body></html>""")
        harness = compile_preparation_harness(work)
        completed = subprocess.run([harness, os.path.join(ROOT, "assets", "theme-gallery"), output],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=180)
        if completed.returncode != 0:
            raise RuntimeError("离线主题包准备失败：" + completed.stderr)
        manifest_path = os.path.join(output, "preparation.json")
        manifest = json.load(open(manifest_path, encoding="utf-8"))
        with open(os.path.join(output, "catalog-status.json"), "w", encoding="utf-8") as stream:
            json.dump(manifest["items"], stream, ensure_ascii=False, indent=2)
        prepared = [item for item in manifest["items"] if item.get("state") == "prepared"]
        print("offline catalog:", manifest["themeCount"], "items;", len(prepared), "prepared")
        if os.environ.get("MIDWEB_THEME_IDS") and len(prepared) != manifest["themeCount"]:
            raise RuntimeError("Requested themes failed preparation; see preparation.json")
        node = os.path.join(work, "theme-gallery-browser.cjs")
        write_node_driver(node)
        port = free_port()
        profile = os.path.join(work, "browser-profile")
        browser_process = subprocess.Popen([
            browser, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--window-size=1440,900",
            "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + str(port),
            "--user-data-dir=" + profile, "file:///" + fixture.replace("\\", "/")
        ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            ws_url = wait_for_page(port, "file:///" + fixture.replace("\\", "/"))
            run = subprocess.run(["node", node, ws_url, manifest_path, output],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=3600)
            print(run.stdout, end="")
            if run.returncode != 0:
                raise RuntimeError("浏览器主题矩阵失败：" + run.stderr)
        finally:
            if browser_process.poll() is None:
                subprocess.run(["taskkill", "/PID", str(browser_process.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                try:
                    browser_process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    browser_process.kill()
                    browser_process.wait(timeout=5)
    results_path = os.path.join(output, "browser-results.json")
    results = json.load(open(results_path, encoding="utf-8"))
    passed = sum(1 for item in results if item.get("state") == "passed")
    failed = len(results) - passed
    report = {"runDirectory": output, "catalogCount": manifest["themeCount"],
              "preparedCount": len(prepared), "browserResults": results_path,
              "passed": passed, "failed": failed, "items": manifest["items"]}
    with open(os.path.join(output, "report.json"), "w", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    print("theme gallery browser matrix:", passed, "passed;", failed, "failed")
    print("report:", os.path.join(output, "report.json"))
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
