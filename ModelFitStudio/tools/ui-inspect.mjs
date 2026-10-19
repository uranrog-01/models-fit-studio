// Drive the app's WebView2 over the DevTools protocol: evaluate JS, take screenshots.
// usage: node cdp.mjs [--eval "js"] [--shot out.png] [--wait ms] [--clip selector] ...
// steps run in the order given
import fs from "node:fs";

const port = process.env.CDP_PORT || "9333";
const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const page = list.find((t) => t.type === "page" && t.url.includes("modelfit.local")) || list.find((t) => t.type === "page");
if (!page) { console.error("no page target"); process.exit(2); }
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let seq = 0;
const pending = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
};
const send = (method, params = {}) => new Promise((res) => {
  const id = ++seq;
  pending.set(id, res);
  ws.send(JSON.stringify({ id, method, params }));
});
const evaluate = async (expression) => {
  const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) return "EXCEPTION: " + (r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text);
  return r.result?.result?.value;
};

const args = process.argv.slice(2);
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === "--eval") {
    const v = await evaluate(args[++i]);
    if (v !== undefined) console.log(typeof v === "string" ? v : JSON.stringify(v, null, 1));
  } else if (a === "--file") {
    const v = await evaluate(fs.readFileSync(args[++i], "utf8"));
    if (v !== undefined) console.log(typeof v === "string" ? v : JSON.stringify(v, null, 1));
  } else if (a === "--wait") {
    await new Promise((r) => setTimeout(r, Number(args[++i])));
  } else if (a === "--shot") {
    const out = args[++i];
    const r = await send("Page.captureScreenshot", { format: "png" });
    fs.writeFileSync(out, Buffer.from(r.result.data, "base64"));
    console.log("saved " + out);
  } else if (a === "--size") {
    const [w, h] = args[++i].split("x").map(Number);
    await send("Emulation.setDeviceMetricsOverride", { width: w, height: h, deviceScaleFactor: 1, mobile: false });
  } else if (a === "--nosize") {
    await send("Emulation.clearDeviceMetricsOverride");
  }
}
ws.close();
