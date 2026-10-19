// Languages. The app is written in English; this layer swaps the words on the page
// for the chosen language as they appear, so no screen needs its own translated copy.
//
// A language is one file in lang/: a map from the English text to its translation.
// A key with {0}, {1}… stands for any text in those places ("{0} GB free"), so lines
// that carry numbers or names are translated too. Text that has no entry stays in
// English. Model names, file names and commands are never touched.
window.I18n = (() => {
  "use strict";

  const ATTRS = ["placeholder", "title", "aria-label"];
  const SKIP = new Set(["SCRIPT", "STYLE", "CODE", "PRE", "TEXTAREA"]);

  let code = "en";
  let exact = null;                 // Map: English -> translation
  let patterns = [];                // [{ re, out }]
  const texts = new WeakMap();      // text node -> { en, out }
  const attrs = new WeakMap();      // element -> { name: { en, out } }
  const missing = new Set();        // English seen with no translation, kept only when asked (I18n.collect)
  let collecting = false;

  const squeeze = (s) => s.trim().replace(/\s+/g, " ");

  function compile(map) {
    exact = new Map();
    patterns = [];
    for (const [en, tr] of Object.entries(map)) {
      if (!/\{\d\}/.test(en)) {
        exact.set(en, tr);
        continue;
      }
      const order = [];
      const source = en.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\\\{(\d)\\\}/g, (_, n) => {
        order.push(Number(n));
        return "(.+?)";
      });
      patterns.push({ re: new RegExp("^" + source + "$", "s"), order, out: tr });
    }
    // the most specific first: a longer key must win over a shorter one that also fits
    patterns.sort((a, b) => b.re.source.length - a.re.source.length);
  }

  // a list inside a sentence: "quality and speed"
  function list(text) {
    const and = exact.get("and");
    if (!and || !text.includes(" and ")) return text;
    return text.split(" and ").map((x) => exact.get(x) ?? x).join(` ${and} `);
  }

  // a whole string: its own entry, or the entry whose pattern it fits. What fills a
  // pattern's places is looked up too ("Fits · reading pace · 7–10 tok/s est.").
  function whole(core, depth) {
    const hit = exact.get(core);
    if (hit !== undefined) return hit;
    for (const p of patterns) {
      const m = p.re.exec(core);
      if (!m) continue;
      const parts = [];
      p.order.forEach((n, i) => {
        const piece = m[i + 1];
        parts[n] = depth < 2 ? whole(piece, depth + 1) ?? list(piece) : piece;
      });
      return p.out.replace(/\{(\d)\}/g, (_, n) => parts[Number(n)] ?? "");
    }
    return undefined;
  }

  // one piece of text, or null when there is nothing to change
  function translate(text) {
    if (!exact || !text) return null;
    const core = squeeze(text);
    if (!core || !/\p{L}/u.test(core)) return null;
    let out = whole(core, 0);
    if (out === undefined) {
      // a paragraph put together from sentences: each sentence on its own
      const sentences = core.split(/(?<=[.!?])\s+(?=[A-Z"“])/);
      if (sentences.length > 1) {
        let any = false;
        const done = sentences.map((one) => {
          const t = whole(one, 0);
          if (t !== undefined) any = true;
          return t ?? one;
        });
        if (any) out = done.join(" ");
      }
    }
    if (out === undefined) {
      if (collecting && core.length < 400) missing.add(core);
      return null;
    }
    return text.match(/^\s*/)[0] + out + text.match(/\s*$/)[0];
  }

  function skipped(node) {
    for (let e = node.nodeType === 1 ? node : node.parentElement; e; e = e.parentElement) {
      if (SKIP.has(e.tagName) || e.hasAttribute("data-no-i18n")) return true;
    }
    return false;
  }

  function doText(node) {
    if (skipped(node)) return;
    const known = texts.get(node);
    // what is on the page now is either what this layer wrote, or new English from the app
    const en = known && node.data === known.out ? known.en : node.data;
    const out = translate(en) ?? en;
    texts.set(node, { en, out });
    if (node.data !== out) node.data = out;
  }

  function doAttrs(el) {
    if (skipped(el)) return;
    let known = attrs.get(el);
    for (const name of ATTRS) {
      if (!el.hasAttribute(name)) continue;
      const now = el.getAttribute(name);
      const rec = known && known[name];
      const en = rec && now === rec.out ? rec.en : now;
      const out = translate(en) ?? en;
      if (!known) attrs.set(el, (known = {}));
      known[name] = { en, out };
      if (now !== out) el.setAttribute(name, out);
    }
  }

  function walk(root) {
    if (root.nodeType === 3) return doText(root);
    if (root.nodeType !== 1) return;
    doAttrs(root);
    const all = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
    for (let n = all.nextNode(); n; n = all.nextNode()) {
      if (n.nodeType === 3) doText(n);
      else doAttrs(n);
    }
  }

  const watcher = new MutationObserver((list) => {
    if (!exact && code === "en") return;
    for (const m of list) {
      if (m.type === "characterData") doText(m.target);
      else if (m.type === "attributes") doAttrs(m.target);
      else m.addedNodes.forEach(walk);
    }
  });

  function watch() {
    watcher.observe(document.documentElement, {
      subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ATTRS,
    });
  }

  /** Switches the page to a language. English needs no file. */
  async function set(next) {
    let map = null;
    if (next !== "en") {
      try {
        const doc = await (await fetch(`lang/${next}.json`)).json();
        map = doc.strings || {};
      } catch {
        next = "en";   // a language without a file falls back to English
      }
    }
    code = next;
    if (map) compile(map);
    else { exact = null; patterns = []; }
    missing.clear();
    document.documentElement.lang = code;
    if (document.body) walk(document.body);
    return code;
  }

  // a message the browser draws itself (confirm): whole, or line by line
  function plain(text) {
    if (!exact) return text;
    return translate(text) ?? String(text).split("\n").map((line) => translate(line) ?? line).join("\n");
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", watch);
  else watch();

  const ask = window.confirm.bind(window);
  window.confirm = (message) => ask(plain(message));

  return {
    set,
    t: plain,
    get code() { return code; },
    // for whoever writes a language file: the English on screen that has no entry yet
    collect(on) { collecting = on; if (!on) missing.clear(); },
    get missing() { return [...missing]; },
  };
})();
