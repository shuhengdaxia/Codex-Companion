import base64
import hashlib
import json
import os
import re
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSC = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")


class MockState(object):
    def __init__(self):
        self.targets = []
        self.dom = {}
        self.ws_connections = 0
        self.expressions = []
        self.methods = []
        self.lock = threading.Lock()

    def reset(self, targets):
        with self.lock:
            self.targets = targets
            self.dom = {}
            for target in targets:
                target_id = target.get("id", "")
                if target.get("url", "").startswith("app://-/"):
                    self.dom[target_id] = {
                        "probe": True,
                        "new_main": True,
                        "conversation": target_id.endswith("-b") or "conversation" in target_id,
                        "composer": True,
                        "active": False,
                        "style": False,
                        "style_owner": "",
                        "theme_id": "",
                        "mode": "",
                        "scope": "",
                        "css": "",
                        "page": None,
                        "saved": {
                            "data-codex-theme": "native-theme",
                            "data-codex-theme-mode": "light",
                            "data-codexthemes-theme": "native-theme",
                            "data-codexthemes-background-scope": "home"
                        },
                        "attrs": {
                            "data-codex-theme": "native-theme",
                            "data-codex-theme-mode": "light",
                            "data-codexthemes-theme": "native-theme",
                            "data-codexthemes-background-scope": "home"
                        },
                        "foreign_class": True,
                        "qq_style": False,
                        "qq_class": False
                    }
            self.ws_connections = 0
            self.expressions = []
            self.methods = []

    def snapshot(self):
        with self.lock:
            return {
                "ws_connections": self.ws_connections,
                "expressions": list(self.expressions),
                "methods": list(self.methods),
                "dom": {key: dict(value) for key, value in self.dom.items()},
            }


STATE = MockState()


class CdpMockHandler(BaseHTTPRequestHandler):
    server_version = "SkinMock/1"

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        if self.path == "/json/list":
            body = json.dumps(STATE.targets).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.headers.get("Upgrade", "").lower() == "websocket" and self.path.startswith("/devtools/page/"):
            self.accept_websocket()
            self.serve_websocket()
            return
        self.send_error(404)

    def accept_websocket(self):
        key = self.headers.get("Sec-WebSocket-Key", "")
        accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()).decode("ascii")
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        with STATE.lock:
            STATE.ws_connections += 1

    def serve_websocket(self):
        if self.path == "/devtools/page/slow":
            time.sleep(6)
            return
        while True:
            payload = self.read_frame()
            if payload is None:
                return
            try:
                message = json.loads(payload.decode("utf-8"))
            except Exception:
                return
            method = message.get("method", "")
            params = message.get("params") or {}
            with STATE.lock:
                STATE.methods.append(method)
                if method == "Runtime.evaluate":
                    STATE.expressions.append(params.get("expression", ""))
            result = {}
            if method == "Runtime.evaluate":
                expression = params.get("expression", "")
                if "querySelector" in expression:
                    value = True
                elif "getElementById" in expression:
                    value = True
                elif "classList.remove" in expression:
                    value = True
                else:
                    value = True
                result = {"result": {"type": "boolean", "value": value}}
            response = {"id": message.get("id"), "result": result}
            self.write_text(json.dumps(response))

    def read_exact(self, count):
        data = b""
        while len(data) < count:
            chunk = self.connection.recv(count - len(data))
            if not chunk:
                return None
            data += chunk
        return data

    def read_frame(self):
        head = self.read_exact(2)
        if not head:
            return None
        b1, b2 = head[0], head[1]
        opcode = b1 & 0x0F
        if opcode == 8:
            return None
        length = b2 & 0x7F
        if length == 126:
            raw = self.read_exact(2)
            if raw is None:
                return None
            length = struct.unpack("!H", raw)[0]
        elif length == 127:
            raw = self.read_exact(8)
            if raw is None:
                return None
            length = struct.unpack("!Q", raw)[0]
        mask = None
        if b2 & 0x80:
            mask = self.read_exact(4)
            if mask is None:
                return None
        payload = self.read_exact(length)
        if payload is None:
            return None
        if mask:
            payload = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        return payload

    def write_text(self, text):
        payload = text.encode("utf-8")
        header = bytearray([0x81])
        if len(payload) < 126:
            header.append(len(payload))
        elif len(payload) <= 65535:
            header.append(126)
            header.extend(struct.pack("!H", len(payload)))
        else:
            header.append(127)
            header.extend(struct.pack("!Q", len(payload)))
        self.connection.sendall(bytes(header) + payload)


def compile_harness(work):
    source = os.path.join(work, "SkinHarness.cs")
    with open(source, "w") as f:
        f.write(r'''
using System;
using System.Reflection;

class SkinHarness {
    static int Main(string[] args) {
        try {
            int port = Int32.Parse(args[0]);
            if (args.Length > 1 && args[1] == "runtime") {
                bool restore = args.Length > 2 && args[2] == "restore";
                bool large = args.Length > 2 && args[2] == "large";
                object runtimeResult;
                if (restore) {
                    MethodInfo runtimeMethod = typeof(ThemeRuntime).GetMethod("RestoreRunning", new Type[] { typeof(int) });
                    runtimeResult = runtimeMethod.Invoke(null, new object[] { port });
                } else {
                    MethodInfo runtimeMethod = typeof(ThemeRuntime).GetMethod("TryApplyToRunning", new Type[] { typeof(int), typeof(string), typeof(string), typeof(string), typeof(string) });
                    string css = large ? "body{--theme-large:" + new string('a', 5000000) + ";}" : "body{--theme-mock:1;}";
                    runtimeResult = runtimeMethod.Invoke(null, new object[] { port, large ? "theme-large" : "theme-mock", css, "dark", "workspace" });
                }
                bool success = (bool)runtimeResult.GetType().GetProperty("Success").GetValue(runtimeResult, null);
                string message = (string)runtimeResult.GetType().GetProperty("Message").GetValue(runtimeResult, null);
                Console.WriteLine("RUNTIME=" + success + ";MESSAGE=" + message);
                return 0;
            }
            bool remove = args.Length > 1 && args[1] == "remove";
            MethodInfo method = typeof(SkinBridge).GetMethod("ScanAndInjectOnce", BindingFlags.Static | BindingFlags.NonPublic);
            object result = method.Invoke(null, new object[] { port, "body{--skin-mock:1;}", remove });
            Console.WriteLine("RESULT=" + result);
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex.ToString());
            return 3;
        }
    }
}
''')
    exe = os.path.join(work, "SkinHarness.exe")
    source_root = os.path.join(ROOT, "src")
    project_sources = [
        os.path.join(source_root, name)
        for name in sorted(os.listdir(source_root))
        if name.endswith(".cs") and name not in ("Program.cs", "UpdaterProgram.cs")
    ]
    command = [
        CSC, "/nologo", "/target:exe", "/optimize+", "/platform:anycpu", "/langversion:5",
        "/r:System.Net.Http.dll", "/r:System.Web.Extensions.dll", "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Security.dll",
        "/r:" + os.path.join(os.path.dirname(CSC), "WPF", "UIAutomationClient.dll"),
        "/r:" + os.path.join(os.path.dirname(CSC), "WPF", "UIAutomationTypes.dll"),
        "/r:" + os.path.join(os.path.dirname(CSC), "WPF", "WindowsBase.dll"),
        "/out:" + exe,
    ] + project_sources + [source]
    subprocess.check_call(command)
    return exe


def run_harness(exe, port, remove=False, expect_failure=False):
    args = [exe, str(port)]
    if remove:
        args.append("remove")
    completed = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
    stdout = completed.stdout.decode("utf-8", "replace")
    stderr = completed.stderr.decode("utf-8", "replace")
    if expect_failure:
        if completed.returncode == 0:
            raise AssertionError("harness unexpectedly succeeded\nSTDOUT:\n%s\nSTDERR:\n%s" % (stdout, stderr))
        return completed.returncode, stdout, stderr
    if completed.returncode != 0:
        raise RuntimeError("harness failed\nSTDOUT:\n%s\nSTDERR:\n%s" % (stdout, stderr))
    return stdout.strip()


def run_runtime(exe, port, restore=False, large=False):
    args = [exe, str(port), "runtime"]
    if restore:
        args.append("restore")
    elif large:
        args.append("large")
    completed = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
    stdout = completed.stdout.decode("utf-8", "replace")
    stderr = completed.stderr.decode("utf-8", "replace")
    if completed.returncode != 0:
        raise RuntimeError("runtime harness failed\nSTDOUT:\n%s\nSTDERR:\n%s" % (stdout, stderr))
    return stdout.strip()


def assert_true(value, message):
    if not value:
        raise AssertionError(message)


def main():
    if not os.path.exists(CSC):
        raise RuntimeError("csc.exe not found: " + CSC)
    server = ThreadingHTTPServer(("127.0.0.1", 0), CdpMockHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever)
    thread.daemon = True
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="skin-mock-") as work:
            exe = compile_harness(work)

            unused = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            unused.bind(("127.0.0.1", 0))
            unused_port = unused.getsockname()[1]
            unused.close()
            output = run_runtime(exe, unused_port)
            assert_true(output.startswith("RUNTIME=False;MESSAGE=未检测到调试端口"),
                        "unreachable CDP port should be reported as unavailable, got " + output)

            STATE.reset([
                {"id": "foreign", "type": "page", "url": "app://other-product/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/foreign" % port},
                {"id": "renderer", "type": "webview", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/renderer" % port},
            ])
            output = run_runtime(exe, port)
            snap = STATE.snapshot()
            assert_true(output.startswith("RUNTIME=False;"), "foreign app or renderer target must not be treated as Codex, got " + output)
            assert_true(snap["ws_connections"] == 0, "foreign app or renderer target must not open a websocket")

            STATE.reset([
                {"id": "remote", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://example.com:%d/devtools/page/remote" % port},
                {"id": "web", "type": "page", "url": "https://example.com/", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/web" % port},
            ])
            output = run_harness(exe, port)
            snap = STATE.snapshot()
            assert_true(output == "RESULT=False", "rejected targets should return false, got " + output)
            assert_true(snap["ws_connections"] == 0, "rejected targets should not open websocket")

            STATE.reset([
                {"id": "slow", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/slow" % port},
            ])
            started = time.time()
            failed_code, failed_stdout, failed_stderr = run_harness(exe, port, expect_failure=True)
            elapsed = time.time() - started
            assert_true(elapsed < 8, "slow endpoint did not honor bounded CDP timeout")
            assert_true("OperationCanceledException" in failed_stderr or "WebSocketException" in failed_stderr or "IOException" in failed_stderr,
                        "slow endpoint failed for an unexpected reason: " + failed_stderr)

            STATE.reset([
                {"id": "good", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good" % port},
            ])
            output = run_harness(exe, port)
            snap = STATE.snapshot()
            assert_true(output == "RESULT=True", "valid Codex target should inject, got " + output)
            assert_true(snap["ws_connections"] == 1, "valid target should open one websocket")
            assert_true(any("querySelector" in x for x in snap["expressions"]), "probe evaluate was not sent")
            assert_true(any("midweb-qq-skin-style" in x and "classList.add('midweb-qq-skin')" in x for x in snap["expressions"]), "install script did not add only midweb class")
            assert_true(not any("codex-qq-skin" in x or "data-dream-shell" in x for x in snap["expressions"]), "install script touched third-party class/data attributes")
            assert_true(any("getElementById" in x for x in snap["expressions"]), "style verification evaluate was not sent")

            STATE.reset([
                {"id": "good", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good" % port},
            ])
            output = run_harness(exe, port, True)
            snap = STATE.snapshot()
            assert_true(output == "RESULT=True", "remove should return true, got " + output)
            remove_scripts = [x for x in snap["expressions"] if "classList.remove" in x]
            assert_true(remove_scripts, "remove script was not evaluated")
            assert_true(any("classList.remove('midweb-qq-skin')" in x for x in remove_scripts), "remove did not remove midweb class")
            assert_true(not any("codex-qq-skin" in x for x in remove_scripts), "remove touched third-party class")

            STATE.reset([
                {"id": "good-a", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good-a" % port},
                {"id": "good-b", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good-b" % port},
            ])
            output = run_runtime(exe, port)
            snap = STATE.snapshot()
            assert_true(output.startswith("RUNTIME=True;"), "runtime apply should succeed, got " + output)
            assert_true(snap["ws_connections"] == 2, "runtime apply must visit every Codex target")
            assert_true(sum("midweb-codexthemes-runtime-style" in x for x in snap["expressions"]) >= 2, "runtime style was not installed on every target")
            assert_true(any("data-codex-theme" in x and "MutationObserver" in x for x in snap["expressions"]), "runtime markers/SPA observer were not installed")
            assert_true(any("backgroundScope" in x and "workspace" in x and "data-codex-theme-mode" in x for x in snap["expressions"]), "runtime color mode/background scope contract was not preserved")

            STATE.reset([
                {"id": "large", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/large" % port},
            ])
            output = run_runtime(exe, port, large=True)
            snap = STATE.snapshot()
            assert_true(output.startswith("RUNTIME=True;"), "large runtime payload should serialize and inject, got " + output)
            assert_true(any(len(x) > 4000000 for x in snap["expressions"]), "large runtime payload did not reach CDP")

            STATE.reset([
                {"id": "good-a", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good-a" % port},
                {"id": "good-b", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good-b" % port},
            ])
            output = run_runtime(exe, port, True)
            snap = STATE.snapshot()
            assert_true(output.startswith("RUNTIME=True;"), "runtime restore should succeed, got " + output)
            assert_true(snap["ws_connections"] == 2, "runtime restore must visit every Codex target")
            assert_true(any("data-midweb-theme-active" in x and "delete globalThis" in x for x in snap["expressions"]), "runtime restore did not clear owned markers")

            STATE.reset([
                {"id": "slow", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/slow" % port},
                {"id": "good", "type": "page", "url": "app://-/index.html", "webSocketDebuggerUrl": "ws://127.0.0.1:%d/devtools/page/good" % port},
            ])
            output = run_runtime(exe, port)
            assert_true(output.startswith("RUNTIME=False;"), "partial runtime apply must fail, got " + output)
            assert_true("只应用到" in output, "partial runtime failure did not report all-target verification: " + output)
        print("skin mock regression passed")
        return 0
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    sys.exit(main())
