/* Models Fit Studio — UI logic. Talks to the .NET host over the WebView2 bridge. */
(() => {
  "use strict";

  // ---------- bridge ----------
  const pending = new Map();
  const pushers = Object.create(null);
  let seq = 0;

  window.chrome.webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m && m.type) {
      (pushers[m.type] || []).forEach((fn) => {
        try { fn(m.data); } catch (err) { log("push handler " + m.type + ": " + err.message); }
      });
      return;
    }
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.data);
    else p.reject(new Error(m.error || "request failed"));
  });

  const api = (cmd, args = {}) =>
    new Promise((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, cmd, args });
    });

  const on = (type, fn) => (pushers[type] || (pushers[type] = [])).push(fn);
  const log = (text) =>
    window.chrome.webview.postMessage({ id: 0, cmd: "log", args: { text } });

  const $ = (id) => document.getElementById(id);

  // ---------- the host ----------
  // The page also runs inside zer0ne code (Settings → Local models), which names itself on the
  // page. There, running a model loads it in zer0ne Engine and opens a chat with it in zer0ne
  // code instead of a chat page in the browser; the words for it say so.
  const inZer0ne = document.documentElement.dataset.host === "zer0ne";
  const RUN = inZer0ne
    ? {
        started: "The model is loaded in zer0ne Engine. The chat is open with it.",
        lede: "zer0ne Engine loads the model and a chat with it opens in zer0ne code. It is listed in Providers & models as zer0ne Engine.",
        loading: "Loading the model into zer0ne Engine. The chat opens with it when it is ready.",
        live: (len, where) => `This model is loaded in zer0ne Engine with a ${len} conversation, on the ${where}.`,
        checkLede: "The engine zer0ne code runs models with. Start loads the model and opens a chat with it.",
        chatStep: "Or press Start: zer0ne code loads the model and opens a chat with it.",
      }
    : {
        started: "The model is running. Its chat page opened in your browser.",
        lede: "Starts the model in its own window and opens a chat page in your browser. Close that window to stop it.",
        loading: "Loading the model into memory. Its chat page opens in your browser when it is ready.",
        live: (len, where) => `This model is running with a ${len} conversation, on the ${where}. Its window is open; closing it stops the model.`,
        checkLede: "The engine this app measures with. It opens a chat page in your browser.",
        chatStep: "Open http://127.0.0.1:8080 in your browser. Closing the terminal window stops the model.",
      };
  const el = (tag, cls, text) => {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined) n.textContent = text;
    return n;
  };

  // forward UI errors to the host log for headless verification
  window.addEventListener("error", (e) =>
    log(`JS ERROR: ${e.message} @ ${e.filename || "?"}:${e.lineno}`)
  );

  const state = {
    hw: null,
    notes: [],
    result: null,
    recent: [],
    scanning: false,
    selectedFile: null,
    installed: [],
    libRoot: "",
    downloads: new Map(),
    settings: null,
    tab: "check",
    runWith: "llama",
    running: [],      // models the app started in llama.cpp and that are still alive
    engine: null,     // which llama.cpp this PC uses, and whether NVIDIA's is there
    nvidia: null,     // { pct } while NVIDIA's engine is downloading
  };

  // ---------- helpers ----------
  const gb = (v, d = 1) => (v === null || v === undefined || Number.isNaN(v) ? "—" : `${Number(v).toFixed(d)} GB`);
  const num = (v, d = 1) => (v === null || v === undefined || Number.isNaN(v) ? "—" : Number(v).toFixed(d));
  // param counts arrive as raw counts, not gigabytes
  const bn = (v, d = 1) => {
    if (v === null || v === undefined || Number.isNaN(v) || v <= 0) return "—";
    const n = Number(v);
    return n >= 1e6 ? `${(n / 1e9).toFixed(d)}B` : `${n.toFixed(d)}B`;
  };
  // A file that only helps a model (vision projector, draft head, importance matrix,
  // vocabulary), the rule of Core/RepoFiles.IsHelper: a draft-named file is a helper only
  // when it is smaller than any build of the model could be. Battles saved before that
  // rule existed can still hold one.
  const isHelperFile = (path, sizeGb, params) => {
    const p = String(path || "").replace(/\\/g, "/");
    if (!/mmproj|(^|\/)imatrix[^/]*$|(^|[-_./])(mtp|draft|eagle)([-_./]|$)|(^|\/)ggml-vocab[-_.]/i.test(p)) return false;
    if (/mmproj|(^|\/)imatrix[^/]*$|(^|\/)ggml-vocab[-_.]/i.test(p)) return true;
    let b = Number(params) || 0;
    if (b >= 1e6) b /= 1e9;
    if (b <= 0) {
      const name = p.split("/").pop();
      const moe = name.match(/(\d+)x(\d+(?:\.\d+)?)b\b/i);
      const one = name.match(/(?:^|[-_.])(\d+(?:\.\d+)?)([bm])(?=$|[-_.])/i);
      b = moe ? moe[1] * moe[2] : one ? (one[2].toLowerCase() === "b" ? +one[1] : one[1] / 1000) : 0;
    }
    return (Number(sizeGb) || 0) < (b > 0 ? b * 0.1 : 3);
  };
  const ktoks = (v) => {
    if (v === null || v === undefined || Number.isNaN(v)) return "—";
    const n = Number(v);
    if (n <= 0) return "—";
    return n >= 1000 ? `${(n / 1000).toFixed(0)}K` : `${n.toFixed(0)}`;
  };
  const fbytes = (v) => {
    const n = Number(v);
    if (!n || Number.isNaN(n) || n <= 0) return "—";
    if (n >= 1073741824) return `${(n / 1073741824).toFixed(2)} GB`;
    if (n >= 1048576) return `${(n / 1048576).toFixed(0)} MB`;
    return `${(n / 1024).toFixed(0)} KB`;
  };
  // a speed is shown as a range ("7–10"); one number when both ends round the same
  const tpsRange = (low, high) => {
    if (!(high > 0)) return "—";
    const d = high < 3 ? 1 : 0;
    const a = String(Number(Number(low).toFixed(d)));
    const b = String(Number(Number(high).toFixed(d)));
    return a === b ? b : `${a}–${b}`;
  };
  const fmtDur = (sec) => {
    if (!(sec > 0) || !Number.isFinite(sec)) return "—";
    if (sec < 10) return `${sec.toFixed(1)} s`;
    if (sec < 90) return `${Math.round(sec)} s`;
    return `${(sec / 60).toFixed(sec < 600 ? 1 : 0)} min`;
  };
  const shortName = (s) => String(s || "").split("/").pop();
  const esc = (s) =>
    String(s ?? "").replace(/[&<>"']/g, (c) => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
    }[c]));
  const escapeHtml = esc;

  function toast(msg, err = false) {
    const t = $("toast");
    t.textContent = msg;
    t.className = "toast" + (err ? " err" : "");
    t.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => (t.hidden = true), err ? 5200 : 3200);
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      toast("Copied");
    } catch {
      const ta = document.createElement("textarea");
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand("copy");
      ta.remove();
      toast("Copied");
    }
  }

  const fitClass = (f) => {
    const s = (f || "").toLowerCase().replace(/\s+/g, "-");
    if (s === "perfect" || s === "good") return "good";
    if (s === "marginal" || s === "slower") return "marginal";
    return "tight";
  };

  // ---------- tabs ----------
  function switchTab(name) {
    state.tab = name;
    [...$("tabsBar").children].forEach((b) => b.classList.toggle("active", b.dataset.tab === name));
    ["check", "compare", "library", "agents", "settings"].forEach((n) =>
      $("pane-" + n).classList.toggle("active", n === name)
    );
    document.querySelector(".hero")?.toggleAttribute("hidden", name !== "check");
    const scroller = document.querySelector(".main");
    if (scroller) scroller.scrollTop = 0;
    if (name === "library") {
      refreshLibrary();
      refreshDownloads();
    }
    if (name === "settings") {
      loadSettingsTab();
    }
    if (name === "compare") {
      Compare.opened();
      AgentBest.opened();
    }
    if (name === "agents") Agents.opened();
    // the model list lives in the Models tab, below the scan results
    if (name === "check" && (!$("hfList") || !$("hfList").querySelector(".mcard"))) loadExplore();
  }

  // ---------- hardware panel ----------
  async function scanHardware(render = true) {
    const { hw, notes } = await api("scanHardware");
    state.hw = hw;
    state.notes = notes || [];
    if (render) renderHardware();
    refreshEngine();
    return hw;
  }

  // which engine runs models on this PC; drawn in the hardware card and in Settings
  async function refreshEngine() {
    try {
      state.engine = await api("engineStatus");
    } catch {
      state.engine = null;
    }
    renderHardware();
    autoNvidia();
    return state.engine;
  }

  // On a PC with an NVIDIA card the faster engine is fetched without being asked for,
  // once: the card and its driver are known, the download is shown and can be stopped.
  // Stopping it is remembered; Settings still offers it.
  let nvidiaTried = false;
  function autoNvidia() {
    const nv = state.engine?.nvidia;
    if (nvidiaTried || !nv || !nv.card || nv.build || !nv.cuda) return;
    nvidiaTried = true;
    try { if (localStorage.getItem("mfs_nvidia_skip") === "1") return; } catch {}
    installNvidia(true);
  }

  function renderHardware() {
    const hw = state.hw;
    if (!hw) return;
    const body = $("hwBody");
    body.innerHTML = "";

    const row = (k, v) => {
      const d = el("div", "hw-row");
      d.append(el("span", "hw-k", k), el("span", "hw-v", v));
      return d;
    };

    const ramBar = el("div", "hw-bar");
    const used = hw.totalRamGb > 0 ? 1 - hw.freeRamGb / hw.totalRamGb : 0;
    const fill = el("i");
    fill.style.width = `${Math.max(4, Math.min(100, used * 100)).toFixed(0)}%`;
    ramBar.append(fill);

    const gpuRow = el("div", "hw-row");
    const gpuVal = el("span", "hw-v");
    if (hw.hasGpu) {
      gpuVal.innerHTML = `<span class="hw-badge ok">GPU</span> ${esc(hw.gpuName)}`;
    } else {
      gpuVal.textContent = "None (CPU only)";
    }
    gpuRow.append(el("span", "hw-k", "GPU"), gpuVal);
    body.append(gpuRow);

    body.append(row("Memory budget", hw.budgetGb > 0 ? `${gb(hw.budgetGb, 1)} usable` : "system RAM only"));
    if (hw.vramCorrected) {
      const d = el("div", "hw-foot");
      d.innerHTML = `<span class="badge-fix">corrected</span> Windows reported ${gb(hw.detectedVramGb, 1)} (pools over 4 GB wrap). Using ${gb(hw.budgetGb, 1)} from ${hw.correctedFrom}.`;
      body.append(d);
    } else if (hw.poolGb) {
      const d = el("div", "hw-foot");
      d.innerHTML = `Shared pool ${gb(hw.poolGb, 1)} · ${gb(hw.poolFreeGb, 1)} free (${hw.backend})`;
      body.append(d);
    }
    body.append(row("RAM", `${gb(hw.freeRamGb, 1)} free of ${gb(hw.totalRamGb, 0)} total`));
    body.append(ramBar);

    body.append(row("CPU", `${hw.cpuName.replace(/^Intel\(R\) Core\(TM\) /, "Intel ").split("(")[0].trim()} · ${hw.cpuCores} cores`));
    const eng = state.engine;
    if (eng && eng.installed) {
      body.append(row("Engine", eng.nvidia?.active ? "NVIDIA (CUDA)" : hw.hasGpu ? "llama.cpp · any card" : "llama.cpp · processor"));
      if (state.nvidia) {
        const d = el("div", "hw-foot hw-nvidia");
        d.append(el("span", null, `Downloading the NVIDIA engine… ${state.nvidia.pct}%`));
        const stop = el("button", "btn-ghost sm", "Stop");
        stop.addEventListener("click", () => api("cancelNvidia"));
        d.append(stop);
        body.append(d);
      } else if (eng.nvidia?.card && !eng.nvidia.active && eng.nvidia.cuda) {
        const d = el("div", "hw-foot hw-nvidia");
        d.append(el("span", null, "NVIDIA card found. Its own engine is faster."));
        const get = el("button", "btn-secondary sm", "Get it");
        get.addEventListener("click", () => installNvidia());
        d.append(get);
        body.append(d);
      }
    }
    // the correction is already explained under "Memory budget": do not say it twice
    const notes = state.notes.filter((n) => !(hw.vramCorrected && /^VRAM detected/i.test(n)));
    if (notes.length) {
      const d = el("div", "hw-foot");
      d.textContent = notes.join(" ");
      body.append(d);
    }

    // Update collapsed mini summary bar
    const miniBadge = $("hwMiniBadge");
    const miniText = $("hwMiniText");
    if (miniBadge && miniText) {
      if (hw.hasGpu) {
        miniBadge.textContent = "GPU";
        const gName = (hw.gpuName || "GPU").replace(/^NVIDIA (GeForce )?/, "");
        miniText.textContent = `${gName} · ${gb(hw.budgetGb, 1)} budget · ${gb(hw.freeRamGb, 0)} RAM free`;
      } else {
        miniBadge.textContent = "CPU";
        const cName = (hw.cpuName || "CPU").replace(/^Intel\(R\) Core\(TM\) /, "Intel ").split("(")[0].trim();
        miniText.textContent = `${cName} · ${gb(hw.freeRamGb, 0)} RAM free`;
      }
    }

    snapshot("hardware");
    updateStatusbar();
  }

  // ---------- the model list: Hugging Face, sized against this PC ----------
  const fmtK = (v) =>
    !v ? "—"
      : v >= 1e6 ? (v / 1e6).toFixed(v >= 1e7 ? 0 : 1) + "M"
      : v >= 1000 ? (v / 1000).toFixed(v >= 100000 ? 0 : 1) + "k"
      : String(v);
  const fmtParams = (b) =>
    !b ? "—" : b < 1 ? `${(b * 1000).toFixed(0)}M` : `${b.toFixed(b < 10 ? 1 : 0)}B`;

  // "org/model" or a huggingface.co link is something to check; anything else is a search
  const looksLikeRepo = (v) => /huggingface\.co\//i.test(v) || /^[\w.-]+\/[\w.-]+$/.test(String(v).trim());
  const heroLabel = () => {
    const v = $("modelInput").value.trim();
    return !v || looksLikeRepo(v) ? "Check" : "Search";
  };

  // Enter or the button: check a link, search anything else
  function submitHero() {
    const v = $("modelInput").value.trim();
    if (looksLikeRepo(v)) return runScan();
    if (!v) {
      $("modelInput").focus();
      return;
    }
    // a name, not a link: that is a search, and the search lives in the list
    $("hfQuery").value = v;
    $("verdictWrap").hidden = true;
    loadExplore();
    $("exploreCard")?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  let exploreSeq = 0;
  async function loadExplore(refresh = false) {
    const thisSeq = ++exploreSeq;
    const q = ($("hfQuery")?.value || "").trim();
    state.exploreQ = q;
    const list = $("hfList");
    if (!list) return;
    list.innerHTML = `<div class="skeleton w-90"></div><div class="skeleton w-70"></div><div class="skeleton w-80"></div>`;
    $("hfHint").textContent = refresh ? "fetching live from huggingface.co…" : "searching huggingface.co…";
    const fitsOnly = $("hfFitsOnly") ? $("hfFitsOnly").checked : true;
    try {
      const { items, error } = await api("hfSearch", {
        q,
        sort: $("hfSort")?.value || "downloads",
        format: $("hfFormat")?.value || "gguf",
        fitsOnly,
        refresh,
      });
      if (thisSeq !== exploreSeq) return;
      list.innerHTML = "";
      if (error) {
        $("hfHint").textContent = "";
        list.append(el("div", "bench-hint", error));
        return;
      }
      const models = items || [];
      $("hfHint").textContent = models.length
        ? `${models.length} ${fitsOnly ? "that fit this PC" : "models"}${q ? ` for “${q}”` : ""}`
        : fitsOnly
        ? "nothing that fits matched — turn off “Fits this PC” to see everything"
        : "nothing matched — try a shorter query";
      models.forEach((r, i) => list.append(hfCard(r, i)));
      snapshot("explore");
    } catch (e) {
      if (thisSeq !== exploreSeq) return;
      $("hfHint").textContent = "";
      list.innerHTML = `<div class="bench-hint">${esc(e.message)}</div>`;
    }
  }

  function hfCard(r, i = 0) {
    const card = el("article", "mcard");
    card.style.setProperty("--i", Math.min(i, 12));

    // "small": the usual version is over budget, a smaller one of the same model fits.
    // "slow": it runs, with part of it outside the graphics memory.
    const badge = { good: "Fits", marginal: "Tight", small: "Smaller fits", slow: "Runs slower", tight: "Too big", est: "Size ?" }[r.estFit] || r.estFit;
    const top = el("div", "mcard-top");
    const fitBadge = el("span", "fit " + (r.estFit === "est" ? "est" : r.estFit === "small" || r.estFit === "slow" ? "marginal" : fitClass(r.estFit)));
    fitBadge.innerHTML = `<span class="fit-dot"></span>${esc(badge)}`;
    top.append(fitBadge);

    if (r.pipeline) top.append(el("span", "mtag", r.pipeline));
    card.append(top);

    card.append(el("h3", "mcard-name", shortName(r.repoId)));
    card.append(el("p", "mcard-repo", r.repoId));

    // how much of this PC's memory the build takes: a Q4-class one, or the smaller one that fits
    const budget = state.hw?.budgetGb || 0;
    const meter = el("div", "mcard-meter");
    if (r.memGb > 0 && budget > 0) {
      const bar = el("div", "mm-bar " + (r.estFit === "tight" ? "over" : r.estFit === "marginal" || r.estFit === "small" || r.estFit === "slow" ? "tight" : "ok"));
      const fill = el("i");
      fill.style.width = `${Math.max(4, Math.min(100, (r.memGb / budget) * 100)).toFixed(0)}%`;
      bar.append(fill);
      meter.append(bar, el("span", "mm-text", r.estFit === "tight"
        ? `${gb(r.memGb, 1)} — more than the ${gb(budget, 0)} this PC has`
        : r.estFit === "small"
        ? `A smaller version fits: ${gb(r.memGb, 1)} of ${gb(budget, 0)}`
        : r.estFit === "slow"
        ? `${gb(r.memGb, 1)} — over the ${gb(budget, 0)} budget: runs slower, partly from system memory`
        : `${gb(r.memGb, 1)} of ${gb(budget, 0)} memory`));
    } else {
      meter.append(el("span", "mm-text", "size unknown — run a check"));
    }
    card.append(meter);

    const stats = el("div", "mcard-stats");
    const stat = (v, l, cls) => {
      const d = el("div", cls || "");
      d.append(el("b", null, v), el("span", null, l));
      return d;
    };
    stats.append(stat(tpsRange(r.tpsLow, r.tpsHigh), "tok/s est", "lead"));
    stats.append(stat(fmtParams(r.paramsB), "params"));
    stats.append(stat(fmtK(r.downloads), "downloads"));
    card.append(stats);

    const actions = el("div", "mcard-actions");
    const check = el("button", "btn-check-pill");
    check.innerHTML =
      `<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/></svg><span>Check</span><span class="btn-arr">↗</span>`;
    check.title = `Scan ${r.repoId} against this machine`;
    check.addEventListener("click", () => {
      $("modelInput").value = r.repoId;
      switchTab("check");
      runScan(r.repoId);
    });
    actions.append(check);

    const open = el("button", "btn-hf-pill");
    open.innerHTML = `<span>HF page</span><svg viewBox="0 0 24 24" width="11" height="11" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>`;
    open.addEventListener("click", () => api("openUrl", { url: `https://huggingface.co/${r.repoId}` }));
    actions.append(open);
    card.append(actions);
    return card;
  }

  // ---------- scan ----------
  const SCAN_PHASES = [
    { title: "Detecting hardware…", sub: "Scanning system memory, CPU architecture, and GPU compute capabilities", pct: 18 },
    { title: "Resolving repository…", sub: "Connecting to Hugging Face repository and inspecting model branch", pct: 42 },
    { title: "Reading model config…", sub: "Parsing architecture, vocabulary size, MoE experts, and layer tensors", pct: 68 },
    { title: "Scoring fit and speed…", sub: "Calculating bandwidth bounds, VRAM allocation, and decode tokens/sec", pct: 88 },
    { title: "Ranking quant options…", sub: "Sorting GGUF quant levels (Q4_K_M, IQ4_XS, Q8) for highest fidelity", pct: 98 },
  ];

  let progressTimers = [];
  let pctInterval = null;
  let currentPct = 0;

  function showProgress(on) {
    const banner = $("scanBanner") || $("scanProgress");
    if (!banner) return;

    progressTimers.forEach(clearTimeout);
    progressTimers = [];
    if (pctInterval) {
      clearInterval(pctInterval);
      pctInterval = null;
    }

    if (!on) {
      const fill = $("scanBannerFill");
      const numEl = $("scanPctNum");
      if (fill) fill.style.width = "100%";
      if (numEl) numEl.textContent = "100";
      for (let i = 0; i < 5; i++) {
        const step = $("sbStep" + i);
        if (step) {
          step.classList.remove("active");
          step.classList.add("done");
        }
      }
      setTimeout(() => {
        banner.hidden = true;
        if (fill) fill.style.width = "0%";
        if (numEl) numEl.textContent = "0";
      }, 400);
      return;
    }

    banner.hidden = false;
    currentPct = 5;
    const fill = $("scanBannerFill");
    const numEl = $("scanPctNum");
    const titleEl = $("scanStepTitle");
    const subEl = $("scanStepSub");

    const updateStepUI = (idx) => {
      const p = SCAN_PHASES[idx];
      if (!p) return;
      if (titleEl) titleEl.textContent = p.title;
      if (subEl) subEl.textContent = p.sub;
      for (let j = 0; j < 5; j++) {
        const step = $("sbStep" + j);
        if (step) {
          step.classList.toggle("active", j === idx);
          step.classList.toggle("done", j < idx);
        }
      }
    };

    updateStepUI(0);
    if (fill) fill.style.width = "8%";
    if (numEl) numEl.textContent = "8";

    let targetPct = SCAN_PHASES[0].pct;
    pctInterval = setInterval(() => {
      if (currentPct < targetPct) {
        currentPct += 1;
        if (numEl) numEl.textContent = String(currentPct);
        if (fill) fill.style.width = `${currentPct}%`;
      }
    }, 45);

    SCAN_PHASES.forEach((phase, idx) => {
      if (idx === 0) return;
      const t = setTimeout(() => {
        updateStepUI(idx);
        targetPct = phase.pct;
      }, idx * 720);
      progressTimers.push(t);
    });
  }

  async function runScan(input, opts = {}) {
    const value = (input ?? $("modelInput").value).trim();
    if (!value) {
      toast("Paste a Hugging Face link first", true);
      $("modelInput").focus();
      return;
    }
    if (state.scanning) return;
    state.scanning = true;
    if (!opts.silent) {
      $("scanBtn").disabled = true;
      $("scanBtn").querySelector(".btn-label").textContent = "Checking…";
      switchTab("check");
    }
    showProgress(true);
    if ($("emptyState")) $("emptyState").hidden = true;

    try {
      if (!state.hw) await scanHardware(false);
      const result = await api("analyze", { input: value });
      state.result = result;
      if (state.recent[0] !== value) {
        state.recent.unshift(value);
        state.recent = state.recent.slice(0, 6);
        renderRecent();
      }
      if (result.error) renderError(result);
      else renderResult(result);
    } catch (e) {
      toast(e.message, true);
      if ($("emptyState")) $("emptyState").hidden = false;
    } finally {
      showProgress(false);
      state.scanning = false;
      $("scanBtn").disabled = false;
      $("scanBtn").querySelector(".btn-label").textContent = heroLabel();
    }
  }

  function renderRecent() {
    const box = $("recent");
    box.innerHTML = "";
    for (const r of state.recent) {
      const b = el("button", null, shortName(r));
      b.title = r;
      b.addEventListener("click", () => {
        $("modelInput").value = r;
        runScan(r);
      });
      box.append(b);
    }
  }

  function renderError(res) {
    $("verdictWrap").hidden = false;
    const v = $("verdictCard");
    v.className = "verdict no";
    $("gaugeFill").style.strokeDashoffset = 326.7;
    $("gaugePct").textContent = "—";
    const isGated = res.error && (res.error.includes("GATED_MODEL") || res.error.includes("HTTP 401") || res.error.includes("HTTP 403"));
    $("verdictStatus").textContent = isGated ? "Access Token Required" : "Could not analyse that";
    $("verdictSub").textContent = res.error || "Unknown error";
    $("verdictActions").hidden = true;

    if (isGated) {
      showGatedModal(res.repoId || $("modelInput")?.value?.trim());
    }

    const chips = $("verdictChips");
    chips.innerHTML = "";
    const suggestions = res.suggestions || [];
    if (suggestions.length) {
      chips.append(el("span", "vchip", "GGUF mirrors of the same weights:"));
      suggestions.slice(0, 6).forEach((s) => {
        const b = el("button", "vchip link", shortName(s));
        b.title = s;
        b.addEventListener("click", () => {
          $("modelInput").value = s;
          runScan(s);
        });
        chips.append(b);
      });
    }
    $("speedVal").textContent = "—";
    $("speedLab").textContent = "";
    $("speedNote").textContent = "";
    $("modelDetails").innerHTML = "";
    $("fileRows").innerHTML = "";
    $("fileNotes").innerHTML = "";
    $("commandsCard").hidden = true;
    $("modelKind").textContent = res.kind === "catalog" ? "catalog" : "hugging face";
  }

  // ---------- result ----------
  function renderResult(res) {
    $("verdictWrap").hidden = false;
    const hw = state.hw;
    const p = res.primary || res.quants[0];
    state.selectedFile = res.primary?.file ?? null;

    const verdict = p?.verdict || "no";
    const v = $("verdictCard");
    // past the budget it still runs: the same amber as a tight fit, with its own words
    v.className = "verdict " + (verdict === "slow" ? "tight" : verdict);

    const util = hw.budgetGb > 0 && p ? (p.memoryRequiredGb / hw.budgetGb) * 100 : 0;
    const C = 2 * Math.PI * 52;
    const dash = Math.max(0, Math.min(100, util)) / 100;
    $("gaugeFill").style.strokeDashoffset = (C * (1 - dash)).toFixed(1);
    // past 100% the ring is already full — the sentence below carries the real number
    $("gaugePct").textContent = util > 0 ? `${Math.min(util, 100).toFixed(0)}%` : "—";

    const ico = {
      runs: '<svg class="ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M20 6L9 17l-5-5"/></svg>',
      tight: '<svg class="ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M12 8v5"/><path d="M12 17h.01"/><circle cx="12" cy="12" r="9"/></svg>',
      no: '<svg class="ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M15 9l-6 6M9 9l6 6"/></svg>',
    }[verdict === "slow" ? "tight" : verdict];
    $("verdictStatus").innerHTML =
      ico +
      `<span>${
        verdict === "runs" ? "Runs on this machine" : verdict === "tight" ? "Runs — but tight" : verdict === "slow" ? "Runs, but slower" : "Won't fit"
      }</span>`;

    $("verdictSub").textContent = p
      ? verdict === "slow"
        ? `${res.primary?.file ?? ""} needs ${gb(p.memoryRequiredGb, 1)}, more than the ${gb(hw.budgetGb, 1)} budget. The rest runs from system memory, slower.`
        : `${res.primary?.file ?? ""} needs ${gb(p.memoryRequiredGb, 1)} of the ${gb(hw.budgetGb, 1)} this machine makes available.` +
        (p.recommended && res.pickReason ? " " + res.pickReason : p.reasons?.length ? " " + p.reasons[0] : "")
      : "No usable GGUF found in that repository.";

    $("speedVal").textContent = p ? (p.measured ? num(p.estTps, 1) : tpsRange(p.tpsLow, p.tpsHigh)) : "—";
    $("speedLab").textContent = p?.measured ? "measured speed" : "estimated speed";
    $("speedNote").textContent = p?.measured
      ? "Measured on this PC with llama.cpp."
      : (res.quants || []).some((q) => q.measured)
      ? "Estimated from the version of this model you measured here."
      : res.speedCalibrated
      ? "A range corrected by a real run on this PC."
      : hw.gpuClass === "integrated"
      ? "A range for a GPU that shares system memory. Measure a downloaded model to make it exact for this PC."
      : "A range for this kind of PC. Measure a downloaded model to make it exact for this PC.";

    const chips = $("verdictChips");
    chips.innerHTML = "";
    const add = (cls, html) => {
      const c = el("span", "vchip" + (cls ? " " + cls : ""));
      c.innerHTML = html;
      chips.append(c);
    };
    add("", `<b>${esc(res.role || "Chat")}</b>`);
    if (res.isMoe)
      add("moe", `<b>MoE</b> · ${res.paramsTotal ? bn(res.paramsActive || res.paramsTotal * 0.13, 1) + "/" + bn(res.paramsTotal, 0) + " active" : "active experts"}`);
    if (res.contextMax) add("", `context <b>${ktoks(res.contextMax)}</b> → <b>${ktoks(p?.usableContext || res.contextMax)}</b> usable`);
    if (res.ollamaName) add("", `Ollama: <b>${esc(res.ollamaName)}</b>`);
    if (res.license) add("", `licence <b>${esc(res.license)}</b>`);
    if (hw.freeRamGb > 0 && p && p.sizeGb > hw.freeRamGb * 0.9)
      add("warn", `only <b>${gb(hw.freeRamGb, 0)}</b> RAM free — close apps`);

    renderActions(res, p);
    renderDetails(res, p);
    renderFiles(res);
    renderCommands(res, p);
    Community.render(res);
    snapshot("result");
  }

  // the download lives in the verdict card, so the next step is always one click
  function renderActions(res, p) {
    // the Turbo plan for the recommended quant, while the user is still choosing
    const planBox = $("turboPlan");
    if (planBox) {
      const plan = res.turbo;
      if (!plan) {
        planBox.hidden = true;
        planBox.innerHTML = "";
      } else {
        // the speed and the conversation length are the ones above; a second estimate here would only contradict them
        const notes = (plan.notes || []).filter((n) => !/^Context capped|would run fully fast here/.test(n)).map((n) => `<li>${esc(n)}</li>`).join("");
        const ctx = p?.usableContext || plan.ctx;
        const est = ctx > 0 ? `<span class="tp-est">${ktoks(ctx)} context</span>` : "";
        planBox.innerHTML =
          `<span class="tp-badge tier-${esc(plan.tier || "gpu")}">${esc(plan.label || "Turbo Fit")}</span>` + est +
          (notes ? `<ul class="tp-notes">${notes}</ul>` : "");
        planBox.hidden = false;
      }
    }
    const box = $("verdictActions");
    const repo = res.repoId;
    const dl = $("dlPrimaryBtn");
    const sim = $("simPrimaryBtn");
    box.querySelectorAll(".hint").forEach((n) => n.remove());
    box.hidden = false;
    dl.hidden = false;
    const isRunsOnMachine = p?.verdict === "runs";
    if (sim) {
      sim.hidden = !isRunsOnMachine || !p || !repo;
      sim.onclick = () => openSpeedPreview(res, p);
    }
    const cmp = $("cmpAddBtn");
    if (cmp) {
      cmp.hidden = !p || !repo;
      cmp.onclick = () => {
        if (Compare.add(res, p)) switchTab("compare");
      };
    }

    if (!p || !repo) {
      dl.hidden = true;
      box.append(el("span", "hint", repo ? "No usable quantisation to download." : "This entry has no repository to download from."));
      return;
    }

    const done = hasInstalled(repo, p.file);
    dl.disabled = done;
    dl.querySelector("span").textContent = done ? "In your library" : "Download this model";
    dl.onclick = () => startDownload(repo, p.file, p.sizeGb);
  }

  function kv(k, v) {
    const d = el("div");
    d.append(el("div", "k", k), el("div", "v"));
    d.lastChild.innerHTML = v;
    return d;
  }

  function renderDetails(res, p) {
    const box = $("modelDetails");
    box.innerHTML = "";
    $("modelKind").textContent = res.kind === "catalog" ? "from catalog" : "from Hugging Face";

    box.append(kv("Model", `<b>${esc(res.modelName || res.repoId || res.input)}</b>`));
    box.append(kv("Provider", esc(res.provider || (res.repoId || "").split("/")[0] || "—")));
    box.append(kv("Role", esc(res.role || "—")));
    box.append(
      kv(
        "Parameters",
        res.isMoe
          ? `~${bn(res.paramsTotal, 0)} total · <small>≈${bn(res.paramsActive || res.paramsTotal * 0.13, 1)} active per token</small>`
          : res.paramsTotal
          ? `~${bn(res.paramsTotal, 0)}`
          : "—"
      )
    );
    box.append(kv("Architecture", esc(res.architecture || (res.isMoe ? "MoE" : "dense"))));
    box.append(
      kv(
        "Context",
        res.contextMax ? `${ktoks(res.contextMax)} max <small>→ ${ktoks(p?.usableContext || 0)} usable here</small>` : "—"
      )
    );
    box.append(
      kv(
        p?.verdict === "no" ? "Smallest in repo" : "Best file",
        p ? `<small style="font-family:var(--mono)">${esc(p.file)}</small>` : "—"
      )
    );
    box.append(kv("File size", p ? `${gb(p.sizeGb, 2)} <small>(${quantLabel(p.quant)})</small>` : "—"));
    box.append(
      kv("Memory needed", p ? `${gb(p.memoryRequiredGb, 1)} <small>of ${gb(state.hw.budgetGb, 0)} budget</small>` : "—")
    );
    box.append(kv("Fit", p ? `<span class="fit ${fitClass(p.fitLevel)}">${esc(p.fitLevel)}</span>` : "—"));
    box.append(kv("Run mode", p?.runMode ? `${esc(p.runMode)} · ${p.verdict === "runs" ? "optimal" : "constrained"}` : "—"));
    box.append(kv("Downloads", res.downloads ? `${fmtK(res.downloads)} / month` : "—"));
    box.append(kv("Base model", res.baseModel && res.baseModel !== res.repoId ? esc(shortName(res.baseModel)) : "—"));
    box.append(kv("Quality", p?.qualityTested > 0
      ? `${num(p.qualityTested, 0)}% <small>of the quality test, this version on this PC</small>`
      : res.quality > 0 ? `${num(res.quality, 0)} of 100 <small>estimate, not tested here</small>` : "—"));
  }

  function quantLabel(q) {
    return q ? String(q).replace(/^UD-/, "UD ") : "GGUF";
  }

  function renderFiles(res) {
    const tbody = $("fileRows");
    tbody.innerHTML = "";
    const ordered = res.quants.slice().sort((a, b) => a.sizeGb - b.sizeGb);
    for (const q of ordered) {
      const tr = el("tr");
      if (res.primary && q.file === res.primary.file) tr.className = "sel";

      const t1 = el("td", "q");
      t1.innerHTML = `<b>${esc(q.quant || "GGUF")}</b>`;
      tr.append(t1);
      tr.append(el("td", "num", gb(q.sizeGb, 2)));
      tr.append(el("td", "num", q.measured ? `${num(q.estTps, 1)} ✓` : tpsRange(q.tpsLow, q.tpsHigh)));
      const t4 = el("td");
      t4.append(el("span", "fit " + fitClass(q.fitLevel), q.fitLevel));
      tr.append(t4);

      // one download button per row — the point of the whole table
      const act = el("td", "act");
      const owned = res.repoId && hasInstalled(res.repoId, q.file);
      const btn = el("button", "row-dl" + (owned ? " owned" : ""));
      btn.innerHTML = owned
        ? `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M20 6L9 17l-5-5"/></svg>`
        : `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M12 4v11m0 0l-4-4m4 4l4-4"/><path d="M5 19h14"/></svg>`;
      btn.title = owned ? "Already downloaded" : `Download ${q.file}`;
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        if (!owned) startDownload(res.repoId, q.file, q.sizeGb);
      });
      act.append(btn);
      tr.append(act);

      tr.title = (q.reasons || []).join("\n");
      tr.addEventListener("click", () => {
        res.primary = q;
        renderResult(res);
        toast(`Selected ${q.quant} · ${gb(q.sizeGb, 2)}`);
      });
      tbody.append(tr);
    }

    const notes = $("fileNotes");
    notes.innerHTML = "";
    const uniq = new Set();
    for (const q of res.quants) for (const r of q.reasons || []) uniq.add(r);
    if (uniq.size) {
      const ul = el("ul");
      [...uniq].slice(0, 4).forEach((r) => ul.append(el("li", null, r)));
      notes.append(ul);
    }
    if (res.mmprojFiles.length) {
      const p = el("p");
      p.innerHTML = `Vision encoder present — add <code>--mmproj ${esc(res.mmprojFiles[0])}</code> when you run it.`;
      notes.append(p);
    }
    if (res.draftFiles?.length) {
      // measured on an Arc 140T: Gemma 4 E4B wrote 21 tok/s alone, 29 to 47 with its head
      const p = el("p");
      p.innerHTML = `Draft head present — download <code>${esc(res.draftFiles[0])}</code> too and add <code>--spec-type draft-mtp --spec-draft-model</code>: the model writes faster, most of all when it rewrites code.`;
      notes.append(p);
    }
  }

  // ---------- run it locally ----------
  // Three ways to run the checked version on this PC, each as short steps with a
  // command that works as written: the real path when the file is here, this PC's
  // own llama.cpp when the app has one.
  const Run = {
    res: null,
    q: null,
    engine: null,

    async show(res, p) {
      const card = $("commandsCard");
      if (!p || p.verdict === "no" || !res.repoId) {
        card.hidden = true;
        return;
      }
      this.res = res;
      this.q = p;
      card.hidden = false;
      const hf = $("openHfBtn");
      hf.hidden = !res.hfUrl;
      hf.onclick = res.hfUrl ? () => api("openUrl", { url: res.hfUrl }) : null;
      this.render();
      try {
        this.engine = await api("engineStatus");
      } catch {
        this.engine = null;
      }
      if (this.res === res) this.render();
    },

    file() {
      return state.installed.find(
        (f) => f.complete && f.repo.toLowerCase() === this.res.repoId.toLowerCase() && f.file === this.q.file
      );
    },

    ctx() {
      return Math.min(this.res.contextMax || 8192, 8192);
    },

    render() {
      const res = this.res;
      const q = this.q;
      if (!res || !q) return;
      [...$("runTabs").children].forEach((b) => b.classList.toggle("active", b.dataset.run === state.runWith));
      const here = this.file();
      const path = here?.path || q.file;
      const quote = (t) => `"${t}"`;
      const steps = [];
      const step = (title, text, cmd) => steps.push({ title, text, cmd });
      let lede = "";
      let canStart = false;

      if (state.runWith === "llama") {
        lede = RUN.checkLede;
        const server = this.engine?.serverPath ? quote(this.engine.serverPath) : "llama-server";
        if (!here) step("Download the file", `Use Download this model above: ${q.file} (${gb(q.sizeGb, 1)}).`);
        if (!this.engine?.installed)
          step("Get llama.cpp", "Settings → Speed measurement → Install llama.cpp. About 30 MB, chosen for this PC.");
        step("Start the model", "Run this in a terminal. Keep the window open while you use the model.",
          `${server} -m ${quote(path)} -c ${this.ctx()} -ngl 99 --jinja --port 8080` +
            (res.mmprojFiles?.length ? ` --mmproj ${quote(res.mmprojFiles[0])}` : "") +
            (res.draftFiles?.length ? ` --spec-type draft-mtp --spec-draft-model ${quote(res.draftFiles[0])}` : ""));
        step("Chat", RUN.chatStep);
        canStart = !!here && !!this.engine?.installed;
      } else if (state.runWith === "ollama") {
        lede = "One command. Ollama downloads its own copy of the model and keeps it ready.";
        const tag = (q.quant || "Q4_K_M").replace(/^UD-/, "");
        step("Install Ollama", "From ollama.com, once.");
        step("Run the model", "It downloads on the first run, then opens a chat in the terminal.",
          res.ollamaName ? `ollama run ${res.ollamaName}` : `ollama run hf.co/${res.repoId}:${tag}`);
      } else {
        lede = "A desktop app with a chat window. It can reuse the file you downloaded here.";
        step("Install LM Studio", "From lmstudio.ai, once.");
        if (here) step("Add this file", "Copies the file you already have into LM Studio.", `lms import ${quote(path)} --copy`);
        else step("Get the model", "In LM Studio, or from a terminal:", `lms get ${res.repoId}`);
        step("Chat", "Open LM Studio, pick the model at the top, and start typing.");
      }

      $("runLede").textContent = lede;
      const list = $("runSteps");
      list.innerHTML = "";
      steps.forEach((st, i) => {
        const li = el("li");
        li.append(el("span", "run-n", String(i + 1)));
        const body = el("div", "run-step");
        body.append(el("b", null, st.title), el("p", null, st.text));
        if (st.cmd) {
          const box = el("div", "run-cmd");
          box.append(el("code", null, st.cmd));
          const copy = el("button", "btn-ghost sm", "Copy");
          copy.addEventListener("click", () => copyText(st.cmd));
          box.append(copy);
          body.append(box);
        }
        li.append(body);
        list.append(li);
      });

      const start = $("runNowBtn");
      start.hidden = state.runWith !== "llama" || !canStart;
      $("runNote").textContent = state.runWith !== "llama" ? ""
        : canStart ? "Start it now does steps 1 and 2 for you."
        : !here ? "Download the file to start it from here." : "";
    },

    async start() {
      const btn = $("runNowBtn");
      btn.disabled = true;
      btn.textContent = "Loading the model…";
      try {
        const r = await api("runLocal", { repo: this.res.repoId, file: this.q.file, ctx: this.ctx() });
        if (r.ok) toast(RUN.started);
        else toast(r.error || "The model did not start", true);
      } catch (e) {
        toast(e.message, true);
      } finally {
        btn.disabled = false;
        btn.textContent = "Start it now";
      }
    },

    init() {
      $("runTabs")?.addEventListener("click", (e) => {
        const b = e.target.closest(".run-tab");
        if (!b) return;
        state.runWith = b.dataset.run;
        this.render();
      });
      $("runNowBtn")?.addEventListener("click", () => this.start());
    },
  };

  function renderCommands(res, p) {
    Run.show(res, p);
  }

  // ---------- downloads ----------
  const hasInstalled = (repo, file) =>
    state.installed.some(
      (f) => f.complete && f.repo.toLowerCase() === String(repo).toLowerCase() && f.file === file
    );

  async function startDownload(repo, file, sizeGb) {
    if (!repo) {
      toast("No repository for that file", true);
      return;
    }
    try {
      const r = await api("download", { repo, file, size: Math.round((sizeGb || 0) * 1073741824) });
      if (r.error) {
        if (r.error.includes("GATED_MODEL") || r.error.includes("401") || r.error.includes("403") || r.error.includes("Unauthorized") || r.error.includes("Forbidden")) {
          showGatedModal(repo, file);
        } else {
          toast(r.error, true);
        }
        return;
      }
      if (r.already) {
        toast("Already in your library");
        await refreshLibrary();
        return;
      }
      toast(`Downloading ${file}`);
      switchTab("library");
      refreshDownloads();
    } catch (e) {
      if (e.message && (e.message.includes("GATED_MODEL") || e.message.includes("401") || e.message.includes("403"))) {
        showGatedModal(repo, file);
      } else {
        toast(e.message, true);
      }
    }
  }

  async function refreshDownloads() {
    try {
      const { files } = await api("downloads");
      state.downloads.clear();
      (files || []).forEach((f) => state.downloads.set(f.key, f));
      renderDownloads();
    } catch (e) {
      log("downloads: " + e.message);
    }
  }

  on("download", (d) => {
    if (!d || !d.key) return;
    state.downloads.set(d.key, d);
    const card = $("dlCard");
    card.hidden = false;
    renderDownloads();
    if (d.state === "Done") {
      toast(`${d.file} downloaded`);
      refreshLibrary();
    }
    if (d.state === "Failed") {
      if (d.error && (d.error.includes("GATED_MODEL") || d.error.includes("401") || d.error.includes("403") || d.error.includes("Unauthorized") || d.error.includes("Forbidden"))) {
        showGatedModal(d.repo, d.file);
      } else {
        toast(`${d.file}: ${d.error}`, true);
      }
    }
    if (d.state === "Cancelled") toast(`${d.file} cancelled`);
  });

  // a discarded row (or dismissed finished one) leaves the list
  on("downloadRemoved", (p) => {
    if (!p || !p.key) return;
    state.downloads.delete(p.key);
    renderDownloads();
    if (!state.downloads.size) $("dlCard").hidden = true;
  });

  function renderDownloads() {
    const list = $("dlList");
    const card = $("dlCard");
    const items = [...state.downloads.values()];
    if (!items.length) {
      list.innerHTML = "";
      $("dlHint").textContent = "";
      card.hidden = true;
      return;
    }
    card.hidden = false;
    const active = items.filter((d) => d.state === "Downloading" || d.state === "Queued").length;
    const paused = items.filter(
      (d) => d.state === "Paused" || d.state === "Failed" || d.state === "Cancelled"
    ).length;
    $("dlHint").textContent = active
      ? `${active} downloading${paused ? ` · ${paused} paused` : ""}`
      : paused
      ? `${paused} paused — resumable`
      : `${items.length} finished`;
    const resumeAll = $("dlResumeAllBtn");
    if (resumeAll) {
      resumeAll.hidden = paused === 0;
      resumeAll.textContent = `Resume all (${paused})`;
    }
    list.innerHTML = "";
    for (const d of items) {
      const cls = d.state.toLowerCase();
      const row = el("div", "dl-row " + cls);
      const head = el("div", "dl-head");
      head.append(el("b", null, d.file));
      head.append(el("span", "dl-repo", d.repo));
      const right = el("span", "dl-right");
      const pct = d.total ? ` · ${((d.received / d.total) * 100).toFixed(0)}%` : "";
      if (d.state === "Downloading")
        right.textContent = `${(d.fraction * 100).toFixed(0)}%${d.mbPerSec > 0 ? ` · ${num(d.mbPerSec, 1)} MB/s` : ""}`;
      else if (d.state === "Queued") right.textContent = "queued";
      else if (d.state === "Paused") right.textContent = `paused${pct}`;
      else if (d.state === "Done") right.textContent = fbytes(d.received || d.total);
      else if (d.state === "Cancelled") right.textContent = `cancelled${pct}`;
      else right.textContent = "failed";
      head.append(right);
      row.append(head);

      const bar = el("div", "dl-bar");
      const fill = el("i");
      fill.className = cls;
      fill.style.width = `${Math.max(1, (d.fraction || 0) * 100).toFixed(1)}%`;
      bar.append(fill);
      row.append(bar);

      const foot = el("div", "dl-foot");
      const detail =
        d.state === "Downloading" && d.total
          ? `${fbytes(d.received)} of ${fbytes(d.total)}`
          : (d.state === "Paused" || d.state === "Cancelled" || d.state === "Failed") && d.total
          ? `${fbytes(d.received)} of ${fbytes(d.total)} saved`
          : d.note || d.error || "";
      foot.append(el("span", "hint" + (d.note ? " dl-note" : ""), d.note || detail));
      const actions = el("span", "dl-actions");
      const discard = async () => {
        if (!confirm(`Discard the downloaded part of ${d.file}? It cannot be resumed afterwards.`)) return;
        await api("discardDownload", { key: d.key });
      };
      if (d.state === "Downloading" || d.state === "Queued") {
        const p = el("button", "btn-secondary sm", "Pause");
        p.addEventListener("click", async () => {
          await api("pauseDownload", { key: d.key });
          toast("Pausing — the downloaded part is kept");
        });
        actions.append(p);
        const c = el("button", "btn-ghost", "Cancel");
        c.addEventListener("click", async () => {
          await api("cancelDownload", { key: d.key });
          toast("Cancelling…");
        });
        actions.append(c);
      } else if (d.state === "Paused" || d.state === "Failed" || d.state === "Cancelled") {
        const r = el("button", "btn-secondary sm", d.state === "Paused" ? "Resume" : "Retry");
        r.addEventListener("click", async () => {
          if (d.error && (d.error.includes("GATED_MODEL") || d.error.includes("401") || d.error.includes("403"))) {
            showGatedModal(d.repo, d.file);
            return;
          }
          await api("resumeDownload", { key: d.key });
          toast("Resuming from the saved part");
        });
        actions.append(r);
        const x = el("button", "btn-ghost", "Discard");
        x.addEventListener("click", discard);
        actions.append(x);
      } else if (d.state === "Done") {
        const x = el("button", "btn-ghost", "Dismiss");
        x.addEventListener("click", () => api("discardDownload", { key: d.key }));
        actions.append(x);
      }
      foot.append(actions);
      row.append(foot);
      list.append(row);
    }
  }

  // ---------- library ----------
  async function refreshLibrary() {
    try {
      const { root, freeGb, files } = await api("installed");
      state.installed = files || [];
      state.libRoot = root || "";
      state.freeGb = freeGb != null ? freeGb : null;
      renderLibrary();
      if (state.result && !state.result.error) renderActions(state.result, state.result.primary);
      return state.installed;
    } catch (e) {
      log("library: " + e.message);
      return [];
    }
  }

  on("installed", () => refreshLibrary());

  function renderLibrary() {
    const list = $("libList");
    list.innerHTML = "";
    const files = state.installed;
    $("libHint").textContent = files.length
      ? `${files.length} file${files.length > 1 ? "s" : ""} · ${fbytes(files.reduce((a, f) => a + (f.bytes || 0), 0))}`
      : "";
    LibBattle.render();

    if ($("libDirPath")) {
      $("libDirPath").textContent = state.libRoot || "Default folder";
      $("libDirPath").title = state.libRoot || "";
    }
    if ($("libDiskFree")) {
      $("libDiskFree").textContent = state.freeGb != null ? `${num(state.freeGb, 1)} GB free` : "— GB free";
    }
    if ($("sbStorageVal")) {
      $("sbStorageVal").textContent = state.freeGb != null ? `${num(state.freeGb, 1)} GB free` : "—";
    }

    if (!files.length) {
      const empty = el("div", "lib-empty");
      empty.innerHTML =
        `<b>Nothing downloaded yet.</b><span>Check a model on the Models tab, then use <i>Download this model</i> — files land in ` +
        `<code>${esc(state.libRoot || "%LOCALAPPDATA%\\ModelFitStudio\\Models")}</code>.</span>`;
      list.append(empty);
      return;
    }

    for (const f of files) {
      const row = el("div", "lib-row" + (f.complete ? "" : " partial"));
      const left = el("div", "lib-left");
      left.append(el("b", null, f.file));
      const repoLine = el("span", "lib-repo", f.repo);
      // a file zer0ne code downloaded: used from its folder, not copied
      if (f.shared) {
        const from = el("span", "lib-from", "From zer0ne code");
        from.title = "This file is in zer0ne code's models folder. It is used from there, not copied.";
        repoLine.append(" ", from);
      }
      // a file another app downloaded (LM Studio, Ollama, the Hugging Face cache…): used where it is
      if (f.source) {
        const from = el("span", "lib-from", `From ${f.source}`);
        from.title = `${f.source} downloaded this file. It is used from there, not copied, and this app never deletes it.`;
        repoLine.append(" ", from);
      }
      left.append(repoLine);
      row.append(left);

      const mid = el("div", "lib-mid");
      mid.append(el("span", "lib-size", fbytes(f.bytes)));
      mid.append(el("span", "hint", new Date(f.written).toLocaleDateString()));
      row.append(mid);

      const speed = el("div", "lib-speed");
      const running = bench.key === benchKey(f.repo, f.file);
      if (running) speed.append(el("span", "lib-speed-run", benchLabel()));
      else {
        if (f.helper) {
          const tag = el("span", "hint", "Helper file");
          tag.title = "A helper for another model (a draft or vision file), not a model to chat with.";
          speed.append(tag);
        }
        if (f.measuredTps > 0) speed.append(el("b", null, `${num(f.measuredTps, 1)} tok/s`));
        if (f.qualityScore > 0) speed.append(el(f.measuredTps > 0 ? "span" : "b", f.measuredTps > 0 ? "hint" : null, `quality ${num(f.qualityScore, 0)}%`));
        else if (f.measuredTps > 0) speed.append(el("span", "hint", "measured"));
      }
      row.append(speed);

      const actions = el("div", "lib-actions");
      const live = LibRun.live(f);
      if (live) row.classList.add("running");
      if (f.complete && /\.gguf$/i.test(f.file) && !f.helper) {
        actions.append(...LibRun.buttons(f));
        if (!live) actions.append(...benchButtons(f.repo, f.file, f.measuredTps > 0, f.qualityScore > 0));
      }
      const del = el("button", "btn-ghost", "Delete");
      del.disabled = !!live;
      if (live) del.title = "Stop the model first";
      del.addEventListener("click", async () => {
        if (!confirm(f.shared
          ? window.I18n.t("Delete {0} from disk? It is in zer0ne code's folder, so zer0ne code loses it too.").replace("{0}", f.file)
          : `Delete ${f.file} from disk?`)) return;
        const r = await api("deleteFile", { repo: f.repo, file: f.file });
        if (r.ok) toast("Deleted");
        else toast("Could not delete it. Something still has the file open.", true);
        refreshLibrary();
      });
      if (!f.source) actions.append(del);
      row.append(actions);
      list.append(row);
    }
  }

  // ---------- app status bar (persistent footer) ----------
  function updateStatusbar() {
    const gv = $("sbGpuVal");
    if (gv) {
      const hw = state.hw;
      gv.textContent = hw ? (hw.hasGpu ? hw.gpuName || "GPU" : "CPU only") : "—";
    }
  }

  // ---------- misc ----------
  function snapshot(tag) {
    try {
      const t = (id) => ($(id) ? $(id).innerText : "<missing>");
      log(
        `[${tag}] tab=${state.tab} status="${t("verdictStatus").replace(/\n/g, " | ")}" ` +
          `speed=${t("speedVal")} util=${t("gaugePct")} ` +
          `files=${$("fileRows") ? $("fileRows").children.length : 0} ` +
          `list=${$("hfList") ? $("hfList").querySelectorAll(".mcard").length : 0} ` +
          `lib=${state.installed.length} dl=${state.downloads.size} ` +
          `hw=(${state.hw ? state.hw.gpuName + " @" + state.hw.budgetGb + "GB" : "none"})`
      );
    } catch {}
  }

  // ---------- hardware panel slide collapse / expand ----------
  function initHwCollapse() {
    const card = $("hwCard");
    const head = $("hwCardHead");
    const toggleBtn = $("hwToggleBtn");
    const miniBar = $("hwMiniBar");
    if (!card || !head) return;

    const setCollapsed = (collapsed) => {
      card.classList.toggle("collapsed", collapsed);
      if (miniBar) miniBar.hidden = !collapsed;
      try {
        if (collapsed) localStorage.setItem("mfs_hw_collapsed", "1");
        else localStorage.removeItem("mfs_hw_collapsed");
      } catch {}
    };

    const toggle = () => {
      const isCollapsed = card.classList.contains("collapsed");
      setCollapsed(!isCollapsed);
    };

    // Restore saved collapse state
    try {
      if (localStorage.getItem("mfs_hw_collapsed") === "1") {
        setCollapsed(true);
      }
    } catch {}

    head.addEventListener("click", (e) => {
      if (e.target.closest("#rescanBtn")) return;
      toggle();
    });

    if (toggleBtn) {
      toggleBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        toggle();
      });
    }

    if (miniBar) {
      miniBar.addEventListener("click", () => {
        setCollapsed(false);
      });
    }
  }

  // ---------- GGUF Quantization Guide Modal ----------
  function initQuantGuideModal() {
    const modal = $("quantModal");
    const openBtn = $("openQuantGuideBtn");
    const closeBtn = $("closeQuantModalBtn");
    const footCloseBtn = $("closeQuantModalFootBtn");
    if (!modal) return;

    const openModal = (targetQuant = null) => {
      modal.hidden = false;
      document.body.style.overflow = "hidden";

      if (targetQuant) {
        setTimeout(() => {
          const targetRow = $(`row-${targetQuant}`) || $(`qcard-${targetQuant}`);
          if (targetRow) {
            targetRow.scrollIntoView({ behavior: "smooth", block: "center" });
            targetRow.classList.add("modal-highlight-pulse");
            setTimeout(() => targetRow.classList.remove("modal-highlight-pulse"), 2200);
          }
        }, 80);
      }
    };

    const closeModal = () => {
      modal.hidden = true;
      document.body.style.overflow = "";
    };

    // opened from a checked model: its recommended version is the row pointed out
    if (openBtn) openBtn.addEventListener("click", () => openModal(String(state.result?.primary?.quant || "").replace(/^UD-/, "")));
    if (closeBtn) closeBtn.addEventListener("click", closeModal);
    if (footCloseBtn) footCloseBtn.addEventListener("click", closeModal);

    modal.addEventListener("click", (e) => {
      if (e.target === modal) closeModal();
    });

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !modal.hidden) closeModal();
    });

  }

  // ---------- Settings ----------
  async function loadSettingsTab() {
    if ($("settingsLibDirInput")) {
      $("settingsLibDirInput").value = state.libRoot || "Loading…";
    }
    await Promise.allSettled([refreshStorageBreakdown(), loadSettings(), loadSpeedSettings()]);
  }

  async function loadSpeedSettings() {
    try {
      const e = await refreshEngine();
      if (!e) return;
      $("engineStatusText").textContent = !e.installed
        ? "llama.cpp is not on this PC. It is downloaded the first time you measure a model."
        : e.nvidia?.active ? `The NVIDIA engine ${e.build || ""} is active.`.replace("  ", " ")
        : e.source === "built in" ? `llama.cpp ${e.build || ""} is built in.`.replace("  ", " ")
        : `llama.cpp ${e.build || ""} is ready (${e.source}).`.replace("  ", " ");
      const nv = e.nvidia || {};
      $("nvidiaRow").hidden = !nv.card;
      $("nvidiaText").textContent = nv.text || "";
      $("nvidiaGetBtn").hidden = !!nv.build || !nv.cuda;
      $("nvidiaRemoveBtn").hidden = !nv.removable;
      $("speedSettingsHint").textContent = e.measurements
        ? `${e.measurements} model${e.measurements === 1 ? "" : "s"} measured${e.calibrated ? " · estimates corrected" : ""}`
        : "nothing measured yet";
      $("installEngineBtn").hidden = !!e.installed;
      $("removeEngineBtn").hidden = !e.removable;
      // a copy downloaded by an earlier version: the built-in one is used, so it only takes space
      $("removeEngineBtn").textContent = e.source === "built in" ? "Remove the old download" : "Remove llama.cpp";
      $("clearMeasurementsBtn").hidden = !e.measurements;
    } catch (err) {
      log("speed settings: " + err.message);
    }
  }

  async function loadSettings() {
    try {
      state.settings = await api("getSettings");
      applySettingsToUI(state.settings);
    } catch (e) {
      log("settings: " + e.message);
    }
  }

  async function refreshStorageBreakdown() {
    try {
      const bd = await api("storageBreakdown");
      if ($("scCompleteGb")) $("scCompleteGb").textContent = `${bd.totalModelGb.toFixed(1)} GB`;
      if ($("scCompleteCount")) $("scCompleteCount").textContent = String(bd.completedFileCount);
      if ($("scPartGb")) $("scPartGb").textContent = `${bd.incompletePartGb.toFixed(1)} GB`;
      if ($("scFreeGb")) $("scFreeGb").textContent = `${bd.freeDiskGb.toFixed(1)} GB`;
      if ($("storageSummaryHint")) {
        $("storageSummaryHint").textContent = `${bd.completedFileCount} model${bd.completedFileCount === 1 ? "" : "s"} (${bd.totalModelGb.toFixed(1)} GB)`;
      }
    } catch { }
  }

  function applySettingsToUI(s) {
    if (!s) return;
    if (s.theme && s.theme !== Theme.current) Theme.apply(s.theme, false);
    if ($("settingsMinimizeToTray") && s.minimizeToTrayOnClose !== undefined) {
      $("settingsMinimizeToTray").checked = !!s.minimizeToTrayOnClose;
    }
    if ($("settingsStartWithWindows") && s.startWithWindows !== undefined) {
      $("settingsStartWithWindows").checked = !!s.startWithWindows;
    }
    if ($("settingsHfTokenInput") && s.hfToken !== undefined) {
      $("settingsHfTokenInput").value = s.hfToken || "";
    }
  }

  function initSettingsEvents() {
    const changeDirBtn = $("settingsChangeDirBtn");
    if (changeDirBtn) {
      changeDirBtn.onclick = async () => {
        const res = await api("pickFolder");
        if (res && res.ok && res.dir) {
          state.libRoot = res.dir;
          if ($("settingsLibDirInput")) $("settingsLibDirInput").value = res.dir;
          refreshLibrary();
          toast("Storage folder updated");
        }
      };
    }

    const openDirBtn = $("settingsOpenDirBtn");
    if (openDirBtn) {
      openDirBtn.onclick = async () => {
        await api("openFolder");
      };
    }

    const purgeBtn = $("purgeIncompleteBtn");
    if (purgeBtn) {
      purgeBtn.onclick = async () => {
        try {
          purgeBtn.disabled = true;
          const res = await api("purgeIncomplete");
          await refreshStorageBreakdown();
          const msg = $("purgeStatusMsg");
          if (msg) {
            msg.textContent = res.count > 0 ? `Deleted ${res.count} unfinished download${res.count === 1 ? "" : "s"} (${res.reclaimedGb.toFixed(1)} GB freed)` : "Nothing to delete";
            setTimeout(() => { if (msg) msg.textContent = ""; }, 4000);
          }
          toast(res.count > 0 ? `Reclaimed ${res.reclaimedGb.toFixed(1)} GB!` : "Storage is already clean");
        } finally {
          purgeBtn.disabled = false;
        }
      };
    }

    const refreshStatsBtn = $("refreshStorageStatsBtn");
    if (refreshStatsBtn) {
      refreshStatsBtn.onclick = async () => {
        await refreshStorageBreakdown();
        toast("Disk stats refreshed");
      };
    }

    // the switches save themselves: nothing to remember to press
    ["settingsMinimizeToTray", "settingsStartWithWindows"].forEach((id) =>
      $(id)?.addEventListener("change", async () => {
        await api("saveSettings", {
          minimizeToTrayOnClose: $("settingsMinimizeToTray").checked,
          startWithWindows: $("settingsStartWithWindows").checked,
        });
        toast("Saved");
      })
    );

    $("clearMeasurementsBtn")?.addEventListener("click", async () => {
      if (!confirm("Forget every measured speed? Estimates go back to the general range for this kind of PC.")) return;
      await api("clearMeasurements");
      toast("Measured speeds forgotten");
      await Promise.allSettled([loadSpeedSettings(), refreshLibrary()]);
      loadExplore();
    });
    $("installEngineBtn")?.addEventListener("click", async () => {
      $("installEngineBtn").disabled = true;
      try {
        await installEngine();
      } finally {
        $("installEngineBtn").disabled = false;
        loadSpeedSettings();
      }
    });
    $("removeEngineBtn")?.addEventListener("click", async () => {
      if (!confirm("Remove the llama.cpp this app downloaded earlier? The built-in one stays and keeps working.")) return;
      await api("removeEngine");
      toast("llama.cpp removed");
      loadSpeedSettings();
    });

    const saveTokenBtn = $("settingsSaveTokenBtn");
    if (saveTokenBtn) {
      saveTokenBtn.onclick = async () => {
        const tok = $("settingsHfTokenInput")?.value?.trim() || "";
        await api("saveSettings", { hfToken: tok });
        if (state.settings) state.settings.hfToken = tok;
        const st = $("settingsHfTokenSaveStatus");
        if (st) {
          st.textContent = "Saved!";
          setTimeout(() => { if (st) st.textContent = ""; }, 3000);
        }
        toast(tok ? "Hugging Face token saved" : "Hugging Face token cleared");
      };
    }

    const toggleTokenVisBtn = $("settingsToggleTokenVisBtn");
    if (toggleTokenVisBtn) {
      toggleTokenVisBtn.onclick = () => {
        const inEl = $("settingsHfTokenInput");
        if (!inEl) return;
        if (inEl.type === "password") {
          inEl.type = "text";
          toggleTokenVisBtn.textContent = "Hide";
        } else {
          inEl.type = "password";
          toggleTokenVisBtn.textContent = "Show";
        }
      };
    }
  }

  // ---------- Gated Model Access Modal & Token Handling ----------
  let pendingGatedDownload = null;

  function showGatedModal(repo, file) {
    pendingGatedDownload = { repo, file };
    const modal = $("gatedModelModal");
    if (!modal) return;

    if ($("gatedTargetRepo")) {
      $("gatedTargetRepo").textContent = repo ? `${repo} (${file || "gguf weights"})` : "Restricted Hugging Face Model";
    }

    const repoLink = $("gatedOpenRepoLink");
    if (repoLink && repo) {
      repoLink.href = `https://huggingface.co/${repo}`;
    }

    const tokenIn = $("gatedTokenInput");
    if (tokenIn) {
      tokenIn.value = state.settings?.hfToken || "";
    }

    modal.hidden = false;
  }

  function closeGatedModal() {
    const modal = $("gatedModelModal");
    if (modal) modal.hidden = true;
    pendingGatedDownload = null;
  }

  function initGatedModal() {
    $("closeGatedModalBtn")?.addEventListener("click", closeGatedModal);
    $("cancelGatedModalBtn")?.addEventListener("click", closeGatedModal);

    $("gatedToggleTokenVisBtn")?.addEventListener("click", () => {
      const inEl = $("gatedTokenInput");
      const btn = $("gatedToggleTokenVisBtn");
      if (!inEl) return;
      if (inEl.type === "password") {
        inEl.type = "text";
        if (btn) btn.textContent = "Hide";
      } else {
        inEl.type = "password";
        if (btn) btn.textContent = "Show";
      }
    });

    $("saveGatedTokenAndRetryBtn")?.addEventListener("click", async () => {
      const token = $("gatedTokenInput")?.value?.trim() || "";
      if (!token) {
        toast("Please paste your Hugging Face User Access Token", true);
        return;
      }
      if (!token.startsWith("hf_")) {
        toast("Note: Hugging Face user tokens start with 'hf_'", true);
      }

      try {
        const btn = $("saveGatedTokenAndRetryBtn");
        btn.disabled = true;
        btn.textContent = "Saving…";

        await api("saveSettings", { hfToken: token });
        if (!state.settings) state.settings = {};
        state.settings.hfToken = token;
        if ($("settingsHfTokenInput")) {
          $("settingsHfTokenInput").value = token;
        }

        toast("Token saved successfully!");
        const pending = pendingGatedDownload;
        closeGatedModal();

        if (pending && pending.repo && pending.file) {
          toast(`Resuming download for ${pending.file}…`);
          const key = `${pending.repo}/${pending.file}`;
          if (state.downloads.has(key)) {
            await api("resumeDownload", { key });
          } else {
            await startDownload(pending.repo, pending.file);
          }
        } else if (pending && pending.repo) {
          toast(`Re-scanning ${pending.repo}…`);
          runScan(pending.repo);
        }
      } catch (e) {
        toast("Failed to save token: " + e.message, true);
      } finally {
        const btn = $("saveGatedTokenAndRetryBtn");
        if (btn) {
          btn.disabled = false;
          btn.innerHTML = "<span>Save Token &amp; Start Download</span>";
        }
      }
    });

    const modal = $("gatedModelModal");
    if (modal) {
      modal.addEventListener("click", (e) => {
        if (e.target === modal) closeGatedModal();
      });
    }

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && modal && !modal.hidden) closeGatedModal();
    });
  }

  // ---------- Speed preview ----------
  // Three numbers and a preview of the writing pace. Nothing runs: everything shown
  // comes from the scan's estimate for this machine, and the text is a fixed sample.
  const SpeedPreview = {
    res: null,
    quant: null,
    timer: null,
    sample:
      "A local model writes its answer one small piece at a time. What you are reading now appears at about the speed " +
      "this version is expected to reach on your computer, so you can judge it before spending the download. " +
      "If the words arrive faster than you read them, long answers will feel comfortable. If you find yourself " +
      "waiting for each word, pick a smaller version from the list above and watch the pace again.",

    open(res, quant) {
      const modal = $("simModal");
      if (!modal || !quant) return;
      this.res = res;
      $("simModalTitle").textContent = res.modelName || res.repoId || "Model";

      const select = $("simQuantSelect");
      select.innerHTML = "";
      const quants = (res.quants && res.quants.length ? res.quants : [quant]).slice().sort((a, b) => a.sizeGb - b.sizeGb);
      for (const q of quants) {
        const opt = el("option", null, `${q.quant || "GGUF"} · ${gb(q.sizeGb, 1)}`);
        opt.value = q.file;
        if (q.file === quant.file) opt.selected = true;
        select.append(opt);
      }

      modal.hidden = false;
      document.body.style.overflow = "hidden";
      this.show(quant);
    },

    // The answer first: is this version right for this PC. Then three facts and the pace.
    verdict(q, budget) {
      const tps = q.estTps || 0;
      const need = q.memoryRequiredGb || q.sizeGb || 0;
      const use = budget > 0 && need > 0 ? need / budget : 1;
      const tight = q.verdict === "tight";
      const slow = tps > 0 && tps < 7;
      if (q.verdict === "no")
        return { cls: "bad", head: "Too big for this PC", why: `It needs ${gb(need, 1)} and this PC has ${gb(budget, 0)}. Pick a smaller version.` };
      if (q.verdict === "slow")
        return { cls: "warn", head: "Runs slower on this PC", why: "It is bigger than the memory budget, so part of it runs from system memory. A smaller version is faster." };
      if (tight && slow)
        return { cls: "warn", head: "Tight and slow on this PC", why: "It uses almost all the memory and writes slower than you read. A smaller version is the better pick." };
      if (tight)
        return { cls: "warn", head: "Tight on this PC", why: "It runs, but uses almost all the memory. Close other apps first." };
      if (slow)
        return { cls: "warn", head: "Slow on this PC", why: "It fits, but writes slower than you read. A smaller version is faster." };
      if (tps >= 20 && use <= 0.6)
        return { cls: "good", head: "Perfect for this PC", why: "Writes faster than you read, with memory to spare." };
      return { cls: "good", head: "Good for this PC", why: "Fits comfortably and writes at about reading pace." };
    },

    show(q) {
      this.quant = q;
      const tps = q.estTps || 0;
      const budget = state.hw?.budgetGb || 0;
      const need = q.memoryRequiredGb || q.sizeGb || 0;

      const v = this.verdict(q, budget);
      $("simVerdict").className = "tbd-verdict " + v.cls;
      $("simVerdictText").textContent = v.head;
      $("simVerdictWhy").textContent = v.why;

      $("simSpeedVal").textContent = !(tps > 0) ? "speed unknown"
        : q.measured ? `${num(tps, 1)} tok/s measured`
        : `${tpsRange(q.tpsLow, q.tpsHigh)} tok/s`;
      $("simMemVal").textContent = need > 0 ? `${gb(need, 1)} memory` : "memory unknown";
      $("simSizeVal").textContent = `${gb(q.sizeGb, 1)} download`;
      $("simPaceLabel").textContent = q.measured ? "Measured pace · sample text" : "Estimated pace · sample text";
      this.syncMeasure();

      const owned = this.res.repoId && hasInstalled(this.res.repoId, q.file);
      $("simDownloadDirectBtn").disabled = !!owned || q.verdict === "no";
      $("simDownloadBtnText").textContent = owned ? "In your library" : `Download ${q.quant || "model"}`;
      this.play();
    },

    // types the sample at the middle of the estimated range; one token is about four characters
    play() {
      clearInterval(this.timer);
      const out = $("simPreviewText");
      const tps = this.quant?.estTps || 0;
      out.textContent = "";
      out.classList.remove("done");
      $("simFinishInfo").hidden = true;
      if (!(tps > 0)) {
        out.textContent = window.I18n.t("No speed estimate for this version.");
        return;
      }
      // the sample is typed in the chosen language; a token is about four letters, or two characters where words are not spaced
      const text = window.I18n.t(this.sample);
      const pieces = text.match(/[぀-ヿ㐀-鿿가-힯]/.test(text) ? /\s*\S{1,2}/g : /\s*\S{1,4}/g) || [];
      const started = performance.now();
      let shown = 0;
      this.timer = setInterval(() => {
        const due = Math.min(pieces.length, Math.floor(((performance.now() - started) / 1000) * tps));
        if (due !== shown) {
          shown = due;
          out.textContent = pieces.slice(0, shown).join("");
        }
        if (shown >= pieces.length) {
          clearInterval(this.timer);
          out.classList.add("done");
          this.finished();
        }
      }, 40);
    },

    // what that pace means for a real answer, in one line
    finished() {
      const q = this.quant;
      if (!q || !(q.estTps > 0)) return;
      const page = 500; // tokens: about a page of text
      $("simFinishInfo").textContent = q.measured || !(q.tpsLow > 0)
        ? `A one-page answer takes about ${fmtDur(page / q.estTps)}.`
        : `A one-page answer takes about ${fmtDur(page / q.tpsHigh)} to ${fmtDur(page / q.tpsLow)}.`;
      $("simFinishInfo").hidden = false;
    },

    // the real measurement: offered once the version is on disk
    syncMeasure() {
      const q = this.quant;
      const repo = this.res?.repoId;
      const btn = $("simMeasureBtn");
      const text = $("simMeasureText");
      if (!q || !btn || !text) return;
      const owned = repo && hasInstalled(repo, q.file);
      const running = bench.key === benchKey(repo, q.file);
      btn.hidden = !owned || (!!bench.key && !running);
      btn.textContent = running ? "Cancel" : q.measured ? "Measure again" : "Measure real speed";
      btn.onclick = () => (running ? api("cancelMeasure") : measure(repo, q.file));
      text.textContent = running ? benchLabel()
        : bench.key ? "Another model is being measured."
        : !owned ? "An estimate. Download it to measure the real speed."
        : q.measured ? (q.firstTokenMs > 0 ? `First word after ${(q.firstTokenMs / 1000).toFixed(1)} s.` : "Measured on this PC.")
        : "On your disk. Run it once for the real speed.";
    },

    close() {
      clearInterval(this.timer);
      $("simModal").hidden = true;
      document.body.style.overflow = "";
    },
  };

  function openSpeedPreview(res, selectedQuant) {
    const p = selectedQuant || res?.primary || res?.quants?.[0];
    if (!res || !p) {
      toast("Nothing to preview yet — check a model first", true);
      return;
    }
    SpeedPreview.open(res, p);
  }

  function initSpeedPreview() {
    const modal = $("simModal");
    if (!modal) return;
    const close = () => SpeedPreview.close();
    $("closeSimModalBtn")?.addEventListener("click", close);
    $("closeSimModalFootBtn")?.addEventListener("click", close);
    modal.addEventListener("click", (e) => {
      if (e.target === modal) close();
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !modal.hidden) close();
    });
    $("simReplayBtn")?.addEventListener("click", () => SpeedPreview.play());
    $("simQuantSelect")?.addEventListener("change", (e) => {
      const match = (SpeedPreview.res?.quants || []).find((q) => q.file === e.target.value);
      if (match) SpeedPreview.show(match);
    });
    $("simDownloadDirectBtn")?.addEventListener("click", () => {
      const { res, quant } = SpeedPreview;
      if (!res?.repoId || !quant) return;
      close();
      startDownload(res.repoId, quant.file, quant.sizeGb);
    });
  }

  // ---------- real speed: measure a downloaded file ----------
  // The host loads the file in llama.cpp, times a few answers and stops it again.
  // One at a time; progress arrives as "bench" events.
  const bench = { key: null, kind: "speed", phase: "", run: 0, runs: 3 };
  const benchKey = (repo, file) => `${repo}/${file}`;
  const benchLabel = () =>
    bench.phase === "loading-cpu" ? "The GPU could not load it. Trying the CPU…"
      : bench.phase === "loading" ? "Loading the model…"
      : bench.kind === "quality" ? `Testing… question ${Math.max(1, bench.run)} of ${bench.runs}`
      : bench.run > 0 ? `Measuring… run ${bench.run} of ${bench.runs}`
      : "Warming up…";

  async function measure(repo, file) {
    if (bench.key) {
      toast("Another model is being measured", true);
      return;
    }
    try {
      let r = await api("measure", { repo, file });
      if (r.needEngine) {
        if (!(await installEngine())) return;
        r = await api("measure", { repo, file });
      }
      if (r.error) {
        toast(r.error, true);
        return;
      }
      if (r.started) {
        Object.assign(bench, { key: r.key, kind: "speed", phase: "loading", run: 0 });
        benchChanged();
      }
    } catch (e) {
      toast(e.message, true);
    }
  }

  // llama.cpp is fetched only when the user says so, with the real name and size in the question
  async function installEngine(why = "Measuring runs the model with llama.cpp, which is not on this PC yet.") {
    let info;
    try {
      info = await api("engineLatest");
    } catch (e) {
      toast("Could not look up llama.cpp on GitHub: " + e.message, true);
      return false;
    }
    const ok = confirm(
      why + "\n\n" +
        `Download ${info.asset} (${info.mb} MB) from github.com/ggml-org/llama.cpp?`
    );
    if (!ok) return false;
    try {
      toast("Downloading llama.cpp…");
      await api("installEngine");
      toast("llama.cpp is ready");
      return true;
    } catch (e) {
      toast("llama.cpp could not be downloaded: " + e.message, true);
      return false;
    }
  }

  on("engine", (d) => {
    if (!d || !(d.total > 0)) return;
    const pct = Math.round((d.received / d.total) * 100);
    if (d.nvidia) {
      // a long download: its progress lives in the hardware card, not in a message that keeps popping up
      if (state.nvidia && state.nvidia.pct !== pct) {
        state.nvidia.pct = pct;
        renderHardware();
      }
      return;
    }
    toast(`Downloading llama.cpp… ${pct}%`);
  });

  // NVIDIA's own build: asked for with its real size, used by itself once it is here
  async function installNvidia(auto = false) {
    if (state.nvidia) return false;
    if (!auto) {
      // asked for by hand: the size is said first
      let info;
      try {
        info = await api("nvidiaLatest");
      } catch (e) {
        toast(e.message, true);
        return false;
      }
      const ok = confirm(
        "Download the NVIDIA engine?\n\n" +
          `${info.files.join("\n")}\n${info.mb} MB from github.com/ggml-org/llama.cpp\n\n` +
          "The app uses it by itself after that."
      );
      if (!ok) return false;
      try { localStorage.removeItem("mfs_nvidia_skip"); } catch {}
    }
    state.nvidia = { pct: 0 };
    renderHardware();
    toast(auto ? "NVIDIA card found. Downloading its faster engine…" : "Downloading the NVIDIA engine…");
    let done = false;
    try {
      const r = await api("installNvidia");
      done = !!r.ok;
      if (r.ok) toast("The NVIDIA engine is active");
      else if (r.cancelled) {
        try { localStorage.setItem("mfs_nvidia_skip", "1"); } catch {}
        toast("Stopped. You can get the NVIDIA engine later in Settings.");
      }
    } catch (e) {
      // no network, or GitHub said no: nothing is remembered, so the next start tries again
      toast("The NVIDIA engine could not be downloaded: " + e.message, true);
    }
    state.nvidia = null;
    await loadSpeedSettings();
    return done;
  }

  on("bench", (d) => {
    if (!d) return;
    const kind = d.kind === "quality" ? "quality" : "speed";
    if (d.phase === "loading" || d.phase === "loading-cpu" || d.phase === "measuring" || d.phase === "testing") {
      Object.assign(bench, { key: d.key, kind, phase: d.phase, run: d.run || 0, runs: d.runs || 3 });
      benchChanged();
      return;
    }
    Object.assign(bench, { key: null, kind: "speed", phase: "", run: 0 });
    if (d.phase === "done" && kind === "quality") {
      toast(`${d.file}: quality ${num(d.quality, 0)}% (${d.correct} of ${d.total} right)`);
      applyQuality(d);
    } else if (d.phase === "done") {
      toast(`${d.file}: ${num(d.genTps, 1)} tok/s measured${d.fallback ? " on the CPU (the GPU could not load it)" : ""}`);
      applyMeasurement(d);
    } else if (d.phase === "failed") {
      toast(d.error || (kind === "quality" ? "The quality test failed" : "The measurement failed"), true);
    } else {
      toast(kind === "quality" ? "Quality test cancelled" : "Measurement cancelled");
    }
    benchChanged();
  });

  // every place that shows a measurement in progress
  function benchChanged() {
    renderLibrary();
    if (!$("simModal").hidden) SpeedPreview.syncMeasure();
    Compare.render();
  }

  // a finished run: show the real number at once, then let the corrected estimates follow
  function applyMeasurement(d) {
    const set = (q) => {
      if (!q) return;
      Object.assign(q, { measured: true, estTps: d.genTps, tpsLow: d.genTps, tpsHigh: d.genTps, firstTokenMs: d.firstTokenMs });
    };
    const res = state.result;
    if (res && !res.error && res.repoId && res.repoId.toLowerCase() === d.repo.toLowerCase()) {
      (res.quants || []).filter((q) => q.file === d.file).forEach(set);
      if (res.primary?.file === d.file) set(res.primary);
      if (!$("verdictWrap").hidden) renderResult(res);
    }
    if (!$("simModal").hidden && SpeedPreview.quant?.file === d.file && SpeedPreview.res?.repoId === d.repo) {
      set(SpeedPreview.quant);
      SpeedPreview.show(SpeedPreview.quant);
    }
    Compare.measured(d);
    LibBattle.measured(d);
    refreshLibrary();
    loadExplore(); // the list is corrected by the new measurement
  }

  // ---------- quality test: a downloaded file answers 40 questions ----------
  async function testQuality(repo, file) {
    if (bench.key) {
      toast("Another model is being measured", true);
      return;
    }
    try {
      let r = await api("testQuality", { repo, file });
      if (r.needEngine) {
        if (!(await installEngine())) return;
        r = await api("testQuality", { repo, file });
      }
      if (r.error) {
        toast(r.error, true);
        return;
      }
      if (r.started) {
        Object.assign(bench, { key: r.key, kind: "quality", phase: "loading", run: 0 });
        benchChanged();
      }
    } catch (e) {
      toast(e.message, true);
    }
  }

  // a finished test: the score goes wherever this file is shown
  function applyQuality(d) {
    const res = state.result;
    if (res && !res.error && res.repoId && res.repoId.toLowerCase() === d.repo.toLowerCase()) {
      (res.quants || []).filter((q) => q.file === d.file).forEach((q) => (q.qualityTested = d.quality));
      if (res.primary?.file === d.file) res.primary.qualityTested = d.quality;
      if (!$("verdictWrap").hidden) renderResult(res);
    }
    const id = benchKey(d.repo, d.file);
    const inBattle = Compare.items.find((x) => x.id === id);
    if (inBattle) {
      inBattle.tested = d.quality;
      Compare.save();
    }
    const inLibrary = LibBattle.rows.find((r) => r.e.id === id);
    if (inLibrary && !inLibrary.note) {
      inLibrary.e.tested = d.quality;
      LibBattle.save();
    }
    refreshLibrary();
  }

  // the two buttons a downloaded file gets wherever it appears: its speed, its quality
  function benchButtons(repo, file, measured, tested) {
    const running = bench.key === benchKey(repo, file);
    const busyElsewhere = !!bench.key && !running;
    const speed = el("button", "btn-secondary sm",
      running && bench.kind !== "quality" ? "Cancel" : measured ? "Measure again" : "Measure speed");
    speed.title = "Run this model once and keep its real speed on this PC";
    speed.disabled = busyElsewhere || (running && bench.kind === "quality");
    speed.addEventListener("click", () => (running ? api("cancelMeasure") : measure(repo, file)));
    const quality = el("button", "btn-secondary sm",
      running && bench.kind === "quality" ? "Cancel" : tested ? "Test again" : "Test quality");
    quality.title = "Ask this model 40 questions with one right answer each, and keep its score";
    quality.disabled = busyElsewhere || (running && bench.kind !== "quality");
    quality.addEventListener("click", () => (running ? api("cancelMeasure") : testQuality(repo, file)));
    return [speed, quality];
  }

  // ---------- community: what others measured, from the llmfit project ----------
  const Community = {
    rows: null,

    async load() {
      if (this.rows) return this.rows;
      try {
        const doc = await (await fetch("community.json")).json();
        this.rows = doc.rows || [];
      } catch {
        this.rows = [];
      }
      return this.rows;
    },

    // a model name without its quant, format and decoration: "Qwen3-8B-Q4_K_M.gguf" and "qwen3:8b" both give "qwen38b"
    key(name) {
      return String(name || "")
        .toLowerCase()
        .replace(/\.gguf$/, "")
        .replace(/[-_.:](ud-)?(i?q\d[a-z0-9_]*|bf16|fp?16|fp?32|mxfp4[a-z_]*)$/i, "")
        .replace(/:latest$/, "")
        .replace(/(^|[-_:.\s])(instruct|it|chat|gguf|mlx)(?=$|[-_:.\s])/g, "$1")
        .replace(/[^a-z0-9]/g, "");
    },

    hwKey(name) {
      return String(name || "")
        .toLowerCase()
        .replace(/\((r|tm)\)/g, "")
        .replace(/\([^)]*gb\)|\(integrated\)|\bgpu\b|\bgraphics\b/g, "")
        .replace(/[^a-z0-9]/g, "");
    },

    async render(res) {
      const card = $("communityCard");
      if (!card) return;
      card.hidden = true;
      if (!res || res.error || !res.repoId) return;
      const rows = await this.load();
      if (state.result !== res) return; // another model was checked meanwhile
      const want = this.key(shortName(res.repoId));
      if (want.length < 4) return;
      const mine = this.hwKey(state.hw?.gpuName);
      const hits = rows
        .filter((r) => this.key(r.model) === want)
        .map((r) => {
          const theirs = this.hwKey(r.hw);
          const same = mine.length > 5 && theirs.length > 5 && (mine.includes(theirs) || theirs.includes(mine));
          return { ...r, same };
        })
        .sort((a, b) => b.same - a.same || b.tps - a.tps);
      if (!hits.length) return;

      const sameCount = hits.filter((h) => h.same).length;
      $("communityHint").textContent =
        `${hits.length} run${hits.length === 1 ? "" : "s"} from community benchmarks` +
        (sameCount ? ` · ${sameCount} on your GPU` : " · none on your GPU yet");
      const list = $("communityRows");
      list.innerHTML = "";
      const top = Math.max(...hits.map((h) => h.tps));
      hits.slice(0, 8).forEach((h) => {
        const row = el("div", "cm-row" + (h.same ? " same" : ""));
        row.title = h.cpu ? `CPU: ${h.cpu}` : "";
        const name = el("span", "cm-hw");
        name.append(el("b", null, h.hw), el("small", null, `${h.model} · ${h.provider}${h.same ? " · same GPU as yours" : ""}`));
        row.append(name);
        const track = el("div", "cmp-track");
        const bar = el("i");
        bar.dataset.w = String(Math.max(1.5, (h.tps / top) * 100));
        track.append(bar);
        row.append(track);
        const val = el("span", "cmp-val");
        val.append(el("b", null, `${num(h.tps, 1)} tok/s`));
        val.append(el("small", null, h.ttftMs >= 100 ? `first word ${(h.ttftMs / 1000).toFixed(1)} s`
          : h.ttftMs > 0 ? `first word ${h.ttftMs} ms` : "measured"));
        row.append(val);
        list.append(row);
      });
      card.hidden = false;
      requestAnimationFrame(() => list.querySelectorAll(".cmp-track > i").forEach((b) => (b.style.width = b.dataset.w + "%")));
    },
  };

  // ---------- community: share my speeds ----------
  // llmfit does the benchmark and the upload itself; this dialog shows every line it
  // writes, and nothing is sent before the user presses Send.
  const Share = {
    busy: false,
    ready: false,

    open() {
      $("shareModal").hidden = false;
      document.body.style.overflow = "hidden";
      this.sync();
    },

    close() {
      if (this.busy) api("shareCancel").catch(() => {});
      $("shareModal").hidden = true;
      document.body.style.overflow = "";
    },

    line(text) {
      const log = $("shareLog");
      log.hidden = false;
      log.textContent += text + "\n";
      log.scrollTop = log.scrollHeight;
    },

    sync(status) {
      if (status !== undefined) $("shareStatus").textContent = status;
      $("shareStartBtn").disabled = this.busy;
      $("shareStartBtn").textContent = this.ready ? "Run the benchmark again" : "Run the benchmark";
      $("shareSendBtn").hidden = !this.ready;
      $("shareSendBtn").disabled = this.busy;
    },

    async prepare() {
      if (this.busy) return;
      this.busy = true;
      this.ready = false;
      $("shareLog").textContent = "";
      $("shareGithubBtn").hidden = true;
      this.sync("llmfit is benchmarking. Nothing is being sent.");
      try {
        let r = await api("sharePrepare");
        if (r.needEngine) {
          if (!(await installEngine())) {
            this.sync("llama.cpp is needed for the benchmark.");
            return;
          }
          r = await api("sharePrepare");
        }
        this.ready = !!r.ok;
        this.sync(r.ok ? "This is what would be sent. Nothing has been sent yet."
          : r.cancelled ? "Stopped." : r.error || "The benchmark did not finish.");
      } catch (e) {
        this.sync(e.message);
      } finally {
        this.busy = false;
        this.sync();
      }
    },

    async send() {
      if (this.busy || !this.ready) return;
      const ok = confirm(
        "Send these speeds to the llmfit project?\n\n" +
          "llmfit opens a public pull request on GitHub from your account, with what the preview showed. " +
          "GitHub will ask you to approve it with a code."
      );
      if (!ok) return;
      this.busy = true;
      $("shareGithubBtn").hidden = false;
      this.sync("Follow what llmfit says below: it shows a code to enter on GitHub.");
      this.line("");
      try {
        const r = await api("shareSend");
        this.sync(r.ok ? "Sent. Thank you." : r.cancelled ? "Stopped. Nothing was sent." : "llmfit did not finish the upload. Read its last lines above.");
        if (r.ok) this.ready = false;
      } catch (e) {
        this.sync(e.message);
      } finally {
        this.busy = false;
        this.sync();
      }
    },

    init() {
      on("share", (d) => {
        if (d && typeof d.line === "string") this.line(d.line);
      });
      $("shareSpeedsBtn")?.addEventListener("click", () => this.open());
      $("closeShareModalBtn")?.addEventListener("click", () => this.close());
      $("shareStartBtn")?.addEventListener("click", () => this.prepare());
      $("shareSendBtn")?.addEventListener("click", () => this.send());
      $("shareGithubBtn")?.addEventListener("click", () => api("openUrl", { url: "https://github.com/login/device" }));
      $("shareModal")?.addEventListener("click", (e) => {
        if (e.target === $("shareModal")) this.close();
      });
    },
  };

  // ---------- share: the battle as a picture ----------
  // Drawn by hand on a canvas in the colours of the current theme, so it needs no
  // library and looks like the app. The host saves it and copies it to the clipboard.
  function shareBattleImage() {
    const budget = state.hw?.budgetGb || 0;
    const ranked = Points.rank(Compare.items, budget);
    if (!ranked.length) return;
    const winner = ranked.length > 1 && !ranked[0].s.out ? ranked[0] : null;

    const css = getComputedStyle(document.documentElement);
    const v = (name, fallback) => (css.getPropertyValue(name) || "").trim() || fallback;
    // the picture is always the dark card of the app, whatever the page behind it is
    const col = {
      bg: v("--share-bg", v("--panel", "#16181D")),
      card: v("--share-card", v("--panel-2", "#121418")),
      line: "rgba(255,255,255,0.10)",
      text: "#F2F2F2",
      text2: "#B4B4B4",
      text3: "#7C7C7C",
      accent: v("--accent", "#D2FF3A"),
      ok: v("--ok", "#22C55E"),
      warn: v("--warn", "#F59E0B"),
      bad: v("--bad", "#EF4444"),
    };
    const W = 1600, H = 900, P = 64;
    const cv = document.createElement("canvas");
    cv.width = W;
    cv.height = H;
    const g = cv.getContext("2d");
    const font = (size, weight = 400) => (g.font = `${weight} ${size}px "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif`);
    const rr = (x, y, w, h, r) => {
      g.beginPath();
      g.roundRect(x, y, w, h, r);
    };
    // text that breaks at spaces and hyphens, at most `max` lines; returns the y after the last one
    const wrap = (text, x, y, width, lh, max = 2) => {
      const parts = String(text).split(/(?<=[\s-])/);
      let line = "";
      let n = 0;
      for (let i = 0; i < parts.length; i++) {
        const next = line + parts[i];
        if (g.measureText(next).width > width && line) {
          n++;
          if (n === max) {
            let cut = line.trimEnd();
            while (cut.length > 1 && g.measureText(cut + "…").width > width) cut = cut.slice(0, -1);
            g.fillText(cut + "…", x, y);
            return y + lh;
          }
          g.fillText(line.trimEnd(), x, y);
          y += lh;
          line = parts[i];
        } else line = next;
      }
      g.fillText(line.trimEnd(), x, y);
      return y + lh;
    };

    g.fillStyle = col.bg;
    g.fillRect(0, 0, W, H);

    // header
    g.textBaseline = "alphabetic";
    font(22, 600);
    g.fillStyle = col.text3;
    g.fillText("BATTLE ON THIS PC", P, P + 18);
    font(22, 400);
    const hwLine = [state.hw?.hasGpu ? state.hw.gpuName : state.hw?.cpuName, budget > 0 ? `${gb(budget, 0)} for models` : ""].filter(Boolean).join("  ·  ");
    g.fillStyle = col.text2;
    g.textAlign = "right";
    g.fillText(hwLine, W - P, P + 18);
    g.textAlign = "left";

    font(54, 650);
    g.fillStyle = col.text;
    const headline = winner ? `${Compare.label(winner.e)} wins` : ranked.length > 1 ? "No winner: none of these fit" : Compare.label(ranked[0].e);
    wrap(headline, P, P + 96, W - 2 * P, 62, 1);
    font(24, 400);
    g.fillStyle = col.text2;
    const sub = winner
      ? `${winner.e.quant} · ${Compare.grade(winner.e, winner.s, budget).text} on this PC · ${winner.s.total} points`
      : "Scored on quality, speed, memory and context";
    g.fillText(sub, P, P + 140);

    // fighters
    const top = P + 190;
    const gap = 24;
    const n = ranked.length;
    const cw = (W - 2 * P - gap * (n - 1)) / n;
    const ch = H - top - P - 56;
    ranked.forEach(({ e, s }, i) => {
      const x = P + i * (cw + gap);
      const isWin = winner && i === 0;
      rr(x, top, cw, ch, 26);
      g.fillStyle = col.card;
      g.fill();
      g.lineWidth = isWin ? 3 : 1.5;
      g.strokeStyle = isWin ? col.accent : col.line;
      g.stroke();

      const ix = x + 30;
      const iw = cw - 60;
      let y = top + 54;
      font(19, 700);
      g.fillStyle = s.out ? col.bad : isWin ? col.accent : col.text3;
      g.fillText(s.out ? "KNOCKED OUT" : isWin ? "WINNER" : `#${i + 1}`, ix, y);

      y += 48;
      font(30, 650);
      g.fillStyle = col.text;
      y = wrap(Compare.label(e), ix, y, iw, 38, 2);
      font(20, 400);
      g.fillStyle = col.text3;
      g.fillText([e.quant, gb(e.sizeGb, 1)].filter(Boolean).join(" · "), ix, y + 2);

      y += 104;
      font(96, 600);
      g.fillStyle = s.out ? col.text3 : col.text;
      const pts = String(s.total);
      g.fillText(pts, ix, y);
      const ptsW = g.measureText(pts).width;
      font(22, 400);
      g.fillStyle = col.text3;
      g.fillText("points", ix + ptsW + 12, y);

      y += 30;
      rr(ix, y, iw, 10, 5);
      g.fillStyle = "rgba(255,255,255,0.08)";
      g.fill();
      if (s.total > 0) {
        rr(ix, y, Math.max(10, (iw * s.total) / 100), 10, 5);
        g.fillStyle = isWin ? col.accent : "rgba(255,255,255,0.4)";
        g.fill();
      }

      y += 58;
      const grade = Compare.grade(e, s, budget);
      font(22, 650);
      g.fillStyle = grade.cls === "good" ? col.ok : grade.cls === "marginal" ? col.warn : col.bad;
      g.fillText(grade.text, ix, y);

      y += 40;
      font(21, 400);
      g.fillStyle = col.text2;
      const facts = s.out
        ? [`needs ${gb(e.memGb, 1)}`, `this PC has ${gb(budget, 0)}`]
        : [Compare.speedText(e), `${gb(e.memGb, 1)} memory`, Compare.qualityText(e, s)];
      facts.filter(Boolean).forEach((f) => {
        y = wrap(f, ix, y, iw, 30, 1);
      });
    });

    // footer
    font(20, 400);
    g.fillStyle = col.text3;
    g.fillText("Models Fit Studio  ·  points: quality 45, speed 30, memory 15, context 10", P, H - P + 14);
    g.textAlign = "right";
    g.fillText(new Date().toLocaleDateString(), W - P, H - P + 14);
    g.textAlign = "left";

    const stamp = new Date().toISOString().slice(0, 10);
    api("saveImage", { name: `battle-${stamp}.png`, dataUrl: cv.toDataURL("image/png") })
      .then((r) => {
        if (r.ok) toast(r.copied ? "Saved, and copied to the clipboard" : "Saved");
        else if (!r.cancelled) toast(r.error || "The picture could not be saved", true);
      })
      .catch((e) => toast(e.message, true));
  }

  // ---------- Battle: the points ----------
  // Every fighter is scored against this PC on fixed scales, so a score does not
  // change when an opponent is added. 100 points: quality 45, speed 30, memory 15,
  // context 10. A model that does not fit is knocked out: it stays in the battle
  // and shows why.
  const Points = {
    rounds: [
      { key: "quality", title: "Quality", max: 45, rule: "estimated score, lowered for heavy compression" },
      { key: "speed", title: "Speed", max: 30, rule: "full points at 20 tok/s, 60% at reading pace (7)" },
      { key: "memory", title: "Memory", max: 15, rule: "full points when it uses half of this PC's memory or less" },
      { key: "context", title: "Context", max: 10, rule: "full points at 32K tokens usable here" },
    ],

    // how much of a model's quality a version keeps: a rule of thumb, not a measurement (Test quality is the measurement)
    keeps(quant) {
      const q = String(quant || "").toUpperCase().replace(/^UD-/, "");
      if (/^(BF16|F16|F32|Q8|Q6)/.test(q)) return 0.999;
      if (/^Q5/.test(q)) return 0.998;
      if (/^Q4_K/.test(q)) return 0.992;
      if (/^IQ4/.test(q)) return 0.988;
      if (/^Q4/.test(q)) return 0.95;
      if (/^I?Q3/.test(q)) return 0.945;
      if (/^I?Q2/.test(q)) return 0.75;
      if (/^(I?Q1|TQ)/.test(q)) return 0.6;
      return 0.97;
    },

    pace(tps) {
      return !(tps > 0) ? "" : tps >= 20 ? "fast" : tps >= 7 ? "reading pace" : "slow";
    },

    // `useTested`: the quality round runs on the quality test. Only when every fighter
    // still standing was tested, because a test score and an estimate are different scales.
    score(e, budget, useTested = false) {
      const out = e.verdict === "no";
      const qKnown = useTested ? e.tested > 0 : e.quality > 0;
      const quality = useTested ? e.tested || 0
        : qKnown ? e.quality * this.keeps(e.quant) : 50; // unknown counts as average
      const tps = e.tps || 0;
      const speedShare = tps >= 20 ? 1 : tps >= 7 ? 0.6 + (0.4 * (tps - 7)) / 13 : (0.6 * tps) / 7;
      const use = budget > 0 && e.memGb > 0 ? e.memGb / budget : 1;
      const parts = {
        quality: (quality / 100) * 45,
        speed: Math.max(0, speedShare) * 30,
        memory: Math.max(0, Math.min(1, (1 - use) / 0.5)) * 15,
        context: Math.max(0, Math.min(1, (e.context || 0) / 32768)) * 10,
      };
      if (out) Object.keys(parts).forEach((k) => (parts[k] = 0));
      const sum = Object.values(parts).reduce((a, b) => a + b, 0);
      return { out, sum, total: Math.round(sum), parts, quality, qKnown, tested: useTested, pace: this.pace(tps) };
    },

    // best first; a knocked-out model is last however good it would be elsewhere
    allTested(items) {
      const standing = items.filter((e) => e.verdict !== "no");
      return standing.length > 0 && standing.every((e) => e.tested > 0);
    },

    rank(items, budget) {
      const useTested = this.allTested(items);
      return items
        .map((e) => ({ e, s: this.score(e, budget, useTested) }))
        .sort((a, b) => a.s.out - b.s.out || b.s.sum - a.s.sum || b.e.tps - a.e.tps);
    },
  };

  // ---------- Battle ----------
  // Up to four models fight on this PC. Each number says whether it is measured or
  // an estimate. The fighters are kept between sessions.
  const Compare = {
    max: 4,
    items: [],
    refreshed: false,
    busy: false,
    status: "",

    init() {
      try {
        this.items = (JSON.parse(localStorage.getItem("mfs_compare") || "[]") || []).slice(0, this.max);
      } catch {
        this.items = [];
      }
      $("cmpAddInputBtn")?.addEventListener("click", () => this.addFromInput());
      $("cmpInput")?.addEventListener("keydown", (e) => {
        if (e.key === "Enter") this.addFromInput();
      });
      this.render();
    },

    save() {
      try { localStorage.setItem("mfs_compare", JSON.stringify(this.items)); } catch {}
    },

    // the numbers of one version of one model, as the battle needs them
    entry(res, q) {
      return {
        id: benchKey(res.repoId, q.file),
        repo: res.repoId,
        file: q.file,
        name: res.modelName || shortName(res.repoId),
        quant: q.quant || "GGUF",
        sizeGb: q.sizeGb || 0,
        memGb: q.memoryRequiredGb || 0,
        tps: q.estTps || 0,
        tpsLow: q.tpsLow || 0,
        tpsHigh: q.tpsHigh || 0,
        measured: !!q.measured,
        firstTokenMs: q.firstTokenMs || 0,
        quality: res.quality || 0,
        tested: q.qualityTested || 0,
        context: q.usableContext || res.contextMax || 0,
        isMoe: !!res.isMoe,
        params: res.paramsTotal || 0,
        role: res.role || "",
        verdict: q.verdict || "no",
      };
    },

    // The version a model fights with: one that is already on this disk (it can be
    // measured), otherwise the one that scores best here.
    bestVersion(res) {
      const budget = state.hw?.budgetGb || 0;
      const all = res.quants || [];
      const owned = all.filter((q) => hasInstalled(res.repoId, q.file));
      // below 3 bits a model is no longer itself: such a version enters only when nothing better fits
      const sound = all.filter((q) => q.verdict !== "no" && Points.keeps(q.quant) >= 0.94);
      const pool = owned.length ? owned : sound.length ? sound : all;
      const ranked = Points.rank(pool.map((q) => this.entry(res, q)), budget);
      const best = ranked[0]?.e;
      return (best && res.quants.find((q) => q.file === best.file)) || res.primary || res.quants[0];
    },

    add(res, q) {
      if (!res?.repoId || !q) return false;
      const e = this.entry(res, q);
      const at = this.items.findIndex((x) => x.id === e.id);
      if (at >= 0) this.items[at] = e;
      else if (this.items.length >= this.max) {
        this.say(`The battle holds ${this.max} models. Remove one first.`);
        return false;
      } else this.items.push(e);
      this.status = "";
      this.save();
      this.render();
      return true;
    },

    remove(id) {
      this.items = this.items.filter((x) => x.id !== id);
      this.save();
      this.render();
    },

    say(text) {
      this.status = text || "";
      this.render();
    },

    addFromInput() {
      const v = ($("cmpInput").value || "").trim();
      if (!v) {
        $("cmpInput").focus();
        return;
      }
      this.addByName(v).then((ok) => {
        if (ok) $("cmpInput").value = "";
      });
    },

    // A check takes several seconds, so adds wait in line: the banner and a placeholder
    // card say what is happening, and more can be added meanwhile.
    queue: [],
    current: null,

    pending() {
      return (this.current ? [this.current] : []).concat(this.queue);
    },

    addByName(input, file) {
      if (this.items.length + this.pending().length >= this.max) {
        this.say(`The battle holds ${this.max} models. Remove one first.`);
        return Promise.resolve(false);
      }
      return new Promise((resolve) => {
        this.queue.push({ input, file, resolve });
        this.status = "";
        this.render();
        this.pump();
      });
    },

    async pump() {
      if (this.busy) return;
      this.busy = true;
      try {
        while (this.queue.length) {
          this.current = this.queue.shift();
          this.render();
          const ok = await this.addNow(this.current.input, this.current.file);
          this.current.resolve(ok);
        }
      } finally {
        this.current = null;
        this.busy = false;
        this.render();
      }
    },

    // Checks the model. `file` picks one version, otherwise the one that scores best
    // here. A repo without GGUF files is not an error: its GGUF mirror fights for it.
    async addNow(input, file) {
      try {
        let res = await api("analyze", { input });
        let via = "";
        if (res.error && (res.suggestions || []).length) {
          via = res.suggestions[0];
          res = await api("analyze", { input: via });
        }
        if (res.error) {
          this.status = res.error.replace(/ ?Pick a GGUF mirror below[^.]*\./, "").replace(/ ?Pick one:$/, "");
          return false;
        }
        const q = (file && res.quants.find((x) => x.file === file)) || this.bestVersion(res);
        const ok = this.add(res, q);
        if (ok && via) this.status = `${shortName(input)} has no GGUF files, so its GGUF mirror ${via} fights for it: the same weights.`;
        else if (ok && file && q.file !== file) this.status = `${file} is not one of the versions the check covers, so the best version entered instead.`;
        return ok;
      } catch (e) {
        this.status = e.message;
        return false;
      }
    },

    // the tab was opened: saved numbers may be older than the last measurement, so check them again once
    opened() {
      this.render();
      if (this.refreshed || !this.items.length) return;
      this.refreshed = true;
      this.refresh();
    },

    async refresh() {
      for (const old of [...this.items]) {
        try {
          const res = await api("analyze", { input: old.repo });
          if (res.error) continue;
          // a helper file that entered before the helper rule gives way to the model itself
          const q = isHelperFile(old.file, old.sizeGb, old.params)
            ? this.bestVersion(res)
            : res.quants.find((x) => x.file === old.file);
          const at = this.items.findIndex((x) => x.id === old.id);
          if (q && at >= 0) {
            const e = this.entry(res, q);
            if (e.id !== old.id && this.items.some((x) => x.id === e.id)) this.items.splice(at, 1);
            else this.items[at] = e;
          }
        } catch {}
        this.save();
        this.render();
      }
    },

    measured(d) {
      const e = this.items.find((x) => x.id === benchKey(d.repo, d.file));
      if (e) {
        Object.assign(e, { measured: true, tps: d.genTps, tpsLow: d.genTps, tpsHigh: d.genTps, firstTokenMs: d.firstTokenMs });
        this.save();
      }
      // a real run changes the estimates of the others too
      if (this.items.some((x) => !x.measured)) this.refresh();
      this.render();
    },

    label(e) {
      return e.name.replace(/[-_.]?GGUF$/i, "");
    },

    qualityText(e, s) {
      if (e.tested > 0) return `quality ${num(e.tested, 0)}% tested`;
      return s.qKnown && !s.tested ? `quality ${num(s.quality, 0)} of 100 est.` : "";
    },

    speedText(e) {
      if (!(e.tps > 0)) return "no speed estimate";
      return e.measured ? `${num(e.tps, 1)} tok/s measured` : `${tpsRange(e.tpsLow, e.tpsHigh)} tok/s est.`;
    },

    // one line that says how a fighter does on this PC
    outcome(e, s, budget) {
      if (s.out) return `Does not fit: needs ${gb(e.memGb, 1)}, this PC has ${gb(budget, 0)}.`;
      const fit = e.verdict === "tight" ? "Fits, but tight" : e.verdict === "slow" ? "Runs slower, partly from system memory" : "Fits";
      return `${fit} · ${s.pace || "speed unknown"} · ${this.speedText(e)}`;
    },

    // How a model does on this PC, in a word: perfect, good, too tight, too slow.
    grade(e, s, budget) {
      if (s.out) return { cls: "tight", text: "Does not fit" };
      const tight = e.verdict === "tight";
      const slow = e.tps > 0 && e.tps < 7;
      if (tight && slow) return { cls: "tight", text: "Too tight and too slow" };
      if (tight) return { cls: "marginal", text: "Too tight" };
      if (e.verdict === "slow") return { cls: "marginal", text: "Runs slower" };
      if (slow) return { cls: "marginal", text: "Too slow" };
      const use = budget > 0 && e.memGb > 0 ? e.memGb / budget : 1;
      return e.tps >= 20 && use <= 0.6 ? { cls: "good", text: "Perfect" } : { cls: "good", text: "Good" };
    },

    render() {
      const body = $("cmpBody");
      if (!body) return;
      const items = this.items;
      const budget = state.hw?.budgetGb || 0;
      const pend = this.pending();
      $("cmpHint").textContent = `${items.length} of ${this.max} · on this PC`;
      $("cmpStatus").textContent = this.status;
      $("cmpStatus").hidden = !this.status;
      $("cmpBanner").hidden = !pend.length;
      if (pend.length) {
        $("cmpBannerTitle").textContent = `Adding ${shortName(pend[0].input)} to the battle`;
        $("cmpBannerSub").textContent = pend.length > 1
          ? `Checking it against this PC · ${pend.length - 1} more waiting`
          : "Checking it against this PC: memory, speed and quality";
      }
      $("cmpEmpty").hidden = items.length > 0 || pend.length > 0;
      body.hidden = items.length === 0 && !pend.length;
      Picker.render();
      if (body.hidden) return;

      const ranked = Points.rank(items, budget);
      const winner = ranked.length > 1 && !ranked[0].s.out ? ranked[0] : null;

      // ---- the arena: the fighters in the order they finished, VS between them
      const arena = $("cmpArena");
      arena.innerHTML = "";
      ranked.forEach(({ e, s }, i) => {
        if (i > 0) arena.append(el("span", "vs", "VS"));
        const card = el("article", "fighter" + (winner && i === 0 ? " winner" : "") + (s.out ? " out" : ""));
        card.style.setProperty("--i", i);

        const head = el("header");
        head.append(el("span", "f-rank", s.out ? "Knocked out" : winner && i === 0 ? "Winner" : ranked.length > 1 ? `#${i + 1}` : "Fighter"));
        const x = el("button", "f-x", "×");
        x.title = "Remove from the battle";
        x.addEventListener("click", () => this.remove(e.id));
        head.append(x);
        card.append(head);

        card.append(el("h3", null, this.label(e)));
        card.append(el("p", "f-ver", [e.quant, gb(e.sizeGb, 1), e.isMoe ? "MoE" : "", e.params > 0 ? bn(e.params, 1) : ""].filter(Boolean).join(" · ")));

        const score = el("div", "f-score");
        const n = el("b", null, "0");
        n.dataset.to = String(s.total);
        score.append(n, el("span", null, "points"));
        card.append(score);
        const bar = el("div", "f-bar");
        const fill = el("i");
        fill.dataset.w = String(s.total);
        bar.append(fill);
        card.append(bar);

        const g = this.grade(e, s, budget);
        const gradeRow = el("p", "f-grade");
        gradeRow.append(el("span", "fit " + g.cls, g.text));
        card.append(gradeRow);
        card.append(el("p", "f-outcome", this.outcome(e, s, budget)));

        const foot = el("footer");
        if (bench.key === e.id) foot.append(el("span", "f-running", benchLabel()));
        if (hasInstalled(e.repo, e.file)) foot.append(...benchButtons(e.repo, e.file, e.measured, e.tested > 0));
        const c = el("button", "btn-ghost", "Check");
        c.addEventListener("click", () => {
          $("modelInput").value = e.repo;
          switchTab("check");
          runScan(e.repo);
        });
        foot.append(c);
        card.append(foot);
        arena.append(card);
      });
      pend.forEach((job, i) => {
        if (ranked.length || i > 0) arena.append(el("span", "vs", "VS"));
        const ghost = el("article", "fighter ghost");
        const head = el("header");
        head.append(el("span", "f-rank", i === 0 ? "Adding…" : "Waiting"));
        ghost.append(head, el("h3", null, shortName(job.input).replace(/[-_.]?GGUF$/i, "")));
        ghost.append(el("p", "f-ver", i === 0 ? "Checking it against this PC" : "Next in line"));
        ghost.append(el("div", "skeleton w-50"), el("div", "skeleton w-90"), el("div", "skeleton w-70"));
        arena.append(ghost);
      });

      // ---- the result, in a sentence
      const pickBox = $("cmpPick");
      pickBox.innerHTML = "";
      if (ranked.length === 0) {
        pickBox.append(el("span", "cmp-pick-why", "The first fighter is being checked."));
      } else if (ranked.length === 1) {
        pickBox.append(el("span", "cmp-pick-why", pend.length ? "The opponent is being checked." : "Add an opponent to start the battle."));
      } else if (!winner) {
        pickBox.append(el("b", null, "No winner: none of these fit this PC."));
        pickBox.append(el("span", "cmp-pick-why", "Try a smaller version of one of them, or a smaller model."));
      } else {
        const second = ranked[1];
        const w = winner.e;
        const g = this.grade(w, winner.s, budget);
        const took = Points.rounds
          .filter((r) => ranked.every((o) => o === winner || winner.s.parts[r.key] > o.s.parts[r.key]))
          .map((r) => r.title.toLowerCase());
        pickBox.append(el("span", "cmp-pick-k", "Best for this PC"));
        pickBox.append(el("b", null, `${this.label(w)} · ${w.quant}`));
        pickBox.append(el("span", "fit " + g.cls, g.text));

        const owned = hasInstalled(w.repo, w.file);
        const dl = el("button", "btn-primary sm cmp-dl", owned ? "In your library" : `Download · ${gb(w.sizeGb, 1)}`);
        dl.disabled = owned;
        dl.title = owned ? "This version is already downloaded" : `Download ${w.file}`;
        dl.addEventListener("click", () => startDownload(w.repo, w.file, w.sizeGb));
        pickBox.append(dl);
        const pic = el("button", "btn-secondary sm", "Share picture");
        pic.title = "Save this battle as a picture, and copy it to the clipboard";
        pic.addEventListener("click", () => shareBattleImage());
        pickBox.append(pic);

        const why = [
          second.s.out
            ? `${winner.s.total} points. The others do not fit this PC.`
            : winner.s.total === second.s.total
            ? `A near tie: ${winner.s.total} points each, ahead by less than a point.`
            : `${winner.s.total} points against ${second.s.total}.`,
          took.length ? `It wins on ${took.join(" and ")}.` : "It wins on the total, without winning a single round outright.",
          g.text === "Perfect" ? "It fits with room to spare and writes faster than you read." : "",
          g.text === "Good" ? "It fits and writes at reading pace or better." : "",
          /tight/i.test(g.text) ? "It is the best of these, but memory is tight: close other apps, or take a smaller version." : "",
          /slow/i.test(g.text) ? "It is the best of these, but slow here: a smaller version would write faster." : "",
        ].filter(Boolean).join(" ");
        pickBox.append(el("span", "cmp-pick-why", why));
      }

      // ---- the rounds: who took how many points, and from what
      const charts = $("cmpCharts");
      charts.innerHTML = "";
      const raw = {
        quality: (e, s) => (s.tested ? `${num(e.tested, 0)}% tested` : s.qKnown ? `${num(s.quality, 0)} of 100 est.` : "unknown"),
        speed: (e) => (e.tps > 0 ? (e.measured ? `${num(e.tps, 1)} tok/s ✓` : `${tpsRange(e.tpsLow, e.tpsHigh)} tok/s`) : "—"),
        memory: (e) => (budget > 0 && e.memGb > 0 ? `${gb(e.memGb, 1)} · ${Math.round((e.memGb / budget) * 100)}%` : gb(e.memGb, 1)),
        context: (e) => ktoks(e.context),
      };
      // a knocked-out model is in the arena with its reason; the rounds are for those still standing
      const standing = ranked.filter((o) => !o.s.out);
      (standing.length ? Points.rounds : []).forEach((r, ri) => {
        const best = Math.max(...standing.map((o) => o.s.parts[r.key]));
        const lead = standing.filter((o) => o.s.parts[r.key] === best && best > 0);
        const card = el("section", "cmp-chart");
        const head = el("header");
        head.append(el("h3", null, `Round ${ri + 1} · ${r.title}`), el("span", null, `${r.max} points`));
        const useTested = standing[0].s.tested;
        card.append(head, el("p", "cmp-rule", r.key !== "quality" ? r.rule
          : useTested ? "share of the 40-question quality test answered right, on this PC"
          : "estimate · test every fighter to use real scores"));
        standing.forEach(({ e, s }) => {
          const pts = s.parts[r.key];
          const isBest = standing.length > 1 && lead.length === 1 && lead[0].e === e;
          const row = el("div", "cmp-row" + (isBest ? " best" : ""));
          row.title = `${raw[r.key](e, s)} → ${num(pts, 1)} of ${r.max} points`;
          row.append(el("span", "cmp-name", `${this.label(e)} · ${e.quant}`));
          const track = el("div", "cmp-track");
          const barEl = el("i");
          barEl.dataset.w = String(Math.max(pts > 0 ? 1.5 : 0, (pts / r.max) * 100));
          track.append(barEl);
          row.append(track);
          const val = el("span", "cmp-val");
          val.append(el("b", null, `+${num(pts, 0)}`));
          val.append(el("small", null, raw[r.key](e, s)));
          row.append(val);
          card.append(row);
        });
        charts.append(card);
      });

      // the first word is real only when measured, so it is shown and not scored
      if (items.some((e) => e.firstTokenMs > 0)) {
        const card = el("section", "cmp-chart");
        const head = el("header");
        head.append(el("h3", null, "First word"), el("span", null, "no points"));
        card.append(head, el("p", "cmp-rule", "seconds after a short prompt · real only when measured"));
        const slowest = Math.max(...items.map((e) => e.firstTokenMs || 0));
        standing.forEach(({ e }) => {
          const has = e.firstTokenMs > 0;
          const row = el("div", "cmp-row" + (has ? "" : " none"));
          row.append(el("span", "cmp-name", `${this.label(e)} · ${e.quant}`));
          const track = el("div", "cmp-track");
          const barEl = el("i");
          barEl.dataset.w = has ? String(Math.max(1.5, (e.firstTokenMs / slowest) * 100)) : "0";
          track.append(barEl);
          row.append(track);
          const val = el("span", "cmp-val");
          if (has) val.append(el("b", null, `${(e.firstTokenMs / 1000).toFixed(e.firstTokenMs < 1000 ? 2 : 1)} s`), el("small", null, "shorter is better"));
          else val.append(el("small", null, "measure to see"));
          row.append(val);
          card.append(row);
        });
        charts.append(card);
      }

      // bars grow and scores count up once they are on the page
      requestAnimationFrame(() => {
        body.querySelectorAll(".cmp-track > i, .f-bar > i").forEach((b) => (b.style.width = b.dataset.w + "%"));
        body.querySelectorAll(".f-score > b").forEach((n) => countUp(n, Number(n.dataset.to) || 0));
      });

      const unmeasured = items.some((e) => !e.measured);
      $("cmpNote").textContent =
        (bench.key && items.some((e) => e.id === bench.key) ? benchLabel() + " " : "") +
        "How points work: quality 45, speed 30, memory 15, context 10, each on a fixed scale for this PC, so a score does not depend on the opponent. " +
        (Points.allTested(items)
          ? "Quality here is real: the share of the 40-question test each model answered right on this PC. "
          : "Quality is an estimate from model size, family and task fit, lowered for heavy compression. Download the fighters and use Test quality on each to fight on real scores. ") +
        (unmeasured
          ? "Speeds marked est. are estimates: download a model and use Measure speed to fight with its real number."
          : items.length ? "Every speed here was measured on this PC." : "");
    },
  };

  // a number that counts up to its value; jumps straight there when motion is not wanted
  function countUp(node, to) {
    if (!(to > 0) || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      node.textContent = String(to);
      return;
    }
    const start = performance.now();
    const tick = (now) => {
      const t = Math.min(1, (now - start) / 600);
      node.textContent = String(Math.round(to * (1 - Math.pow(1 - t, 3))));
      if (t < 1 && node.isConnected) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  // ---------- Library: compare the downloaded models ----------
  // Same points as the Battle tab, only for what is already on this disk. The result is
  // kept, and shown again while the same files are there.
  const LibBattle = {
    rows: [],     // { e, note } — note says why a file is not ranked
    running: false,
    progress: "",

    files() {
      return state.installed.filter((f) => f.complete && /\.gguf$/i.test(f.file) && !f.helper);
    },

    key() {
      return this.files().map((f) => benchKey(f.repo, f.file)).sort().join("|");
    },

    load() {
      try {
        const saved = JSON.parse(localStorage.getItem("mfs_lib_battle") || "null");
        this.rows = saved && saved.key === this.key() ? saved.rows || [] : [];
      } catch {
        this.rows = [];
      }
    },

    save() {
      try { localStorage.setItem("mfs_lib_battle", JSON.stringify({ key: this.key(), rows: this.rows })); } catch {}
    },

    async run() {
      if (this.running) return;
      const files = this.files().slice(0, 8);
      this.running = true;
      this.rows = [];
      try {
        const checked = new Map(); // one check per repo, however many of its files are here
        for (let i = 0; i < files.length; i++) {
          const f = files[i];
          this.progress = `Checking ${i + 1} of ${files.length}…`;
          this.render();
          try {
            if (!checked.has(f.repo)) checked.set(f.repo, await api("analyze", { input: f.repo }));
            const res = checked.get(f.repo);
            const q = !res.error && res.quants.find((x) => x.file === f.file);
            const name = f.file.replace(/\.gguf$/i, "");
            if (res.error || !q) this.rows.push({ e: { id: benchKey(f.repo, f.file), repo: f.repo, file: f.file, name, quant: "" }, note: "could not be checked" });
            else if (/audio|embedding/i.test(res.role || "")) this.rows.push({ e: Compare.entry(res, q), note: `${res.role} model: not a chat model, so it is left out` });
            else this.rows.push({ e: Compare.entry(res, q), note: "" });
          } catch {
            this.rows.push({ e: { id: benchKey(f.repo, f.file), repo: f.repo, file: f.file, name: f.file, quant: "" }, note: "could not be checked" });
          }
        }
        this.save();
      } finally {
        this.running = false;
        this.progress = "";
        this.render();
      }
    },

    measured(d) {
      const row = this.rows.find((r) => r.e.id === benchKey(d.repo, d.file));
      if (!row || row.note) return;
      Object.assign(row.e, { measured: true, tps: d.genTps, tpsLow: d.genTps, tpsHigh: d.genTps, firstTokenMs: d.firstTokenMs });
      this.save();
      this.render();
    },

    render() {
      const box = $("libBattle");
      if (!box) return;
      const files = this.files();
      box.hidden = files.length < 2;
      if (box.hidden) return;
      if (!this.running && !this.rows.length) this.load();

      const budget = state.hw?.budgetGb || 0;
      const fighters = this.rows.filter((r) => !r.note).map((r) => r.e);
      const ranked = Points.rank(fighters, budget);
      const benched = this.rows.filter((r) => r.note);
      const winner = ranked.length > 1 && !ranked[0].s.out ? ranked[0] : null;

      $("libBattleRunBtn").disabled = this.running;
      $("libBattleRunBtn").textContent = this.running ? this.progress : this.rows.length ? "Compare again" : "Compare";
      $("libBattleOpenBtn").hidden = this.running || ranked.length < 2;
      $("libBattleHint").textContent = this.running ? "Checking each model against this PC."
        : winner ? `${Compare.label(winner.e)} · ${winner.e.quant} is the best of your models for this PC.`
        : ranked.length === 1 ? "Only one of your models is a chat model: download another to compare."
        : this.rows.length ? "None of your models fits this PC."
        : "Which of your downloaded models is best for this PC.";

      const list = $("libBattleRows");
      list.innerHTML = "";
      ranked.forEach(({ e, s }, i) => {
        const row = el("div", "lb-row" + (winner && i === 0 ? " winner" : "") + (s.out ? " out" : ""));
        row.title = Points.rounds.map((r) => `${r.title} +${num(s.parts[r.key], 0)}`).join(" · ");
        row.append(el("span", "lb-rank", s.out ? "out" : `#${i + 1}`));
        const name = el("span", "lb-name");
        name.append(el("b", null, `${Compare.label(e)} · ${e.quant}`), el("small", null, Compare.outcome(e, s, budget)));
        row.append(name);
        const track = el("div", "cmp-track");
        const bar = el("i");
        bar.dataset.w = String(s.total);
        track.append(bar);
        row.append(track);
        const pts = el("span", "lb-pts");
        pts.append(el("b", null, String(s.total)), el("small", null, "points"));
        row.append(pts);
        list.append(row);
      });
      benched.forEach(({ e, note }) => {
        const row = el("div", "lb-row out");
        row.append(el("span", "lb-rank", "—"));
        const name = el("span", "lb-name");
        name.append(el("b", null, e.quant ? `${Compare.label(e)} · ${e.quant}` : e.name), el("small", null, note));
        row.append(name);
        list.append(row);
      });
      requestAnimationFrame(() => list.querySelectorAll(".cmp-track > i").forEach((b) => (b.style.width = b.dataset.w + "%")));
    },

    // the full battle, with the rounds: the best four go to the Battle tab
    open() {
      const budget = state.hw?.budgetGb || 0;
      const top = Points.rank(this.rows.filter((r) => !r.note).map((r) => r.e), budget).slice(0, Compare.max).map((o) => o.e);
      if (top.length < 2) return;
      Compare.items = top;
      Compare.status = "";
      Compare.save();
      switchTab("compare");
    },
  };

  // ---------- Battle: choose fighters from the models that fit this PC ----------
  const Picker = {
    rows: [],
    loading: false,
    error: "",
    seq: 0,
    timer: null,

    open() {
      $("pickModal").hidden = false;
      document.body.style.overflow = "hidden";
      $("pickQuery").value = "";
      this.load();
      $("pickQuery").focus();
    },

    close() {
      $("pickModal").hidden = true;
      document.body.style.overflow = "";
    },

    async load() {
      const mine = ++this.seq;
      this.loading = true;
      this.render();
      try {
        const { items, error } = await api("hfSearch", {
          q: ($("pickQuery").value || "").trim(),
          sort: "downloads",
          format: "gguf",
          fitsOnly: true,
        });
        if (mine !== this.seq) return;
        this.error = error || "";
        // a speed is only given to models that write text, so this keeps the chat models
        this.rows = (items || []).filter((r) => r.tpsHigh > 0);
      } catch (e) {
        if (mine !== this.seq) return;
        this.error = e.message;
        this.rows = [];
      } finally {
        if (mine === this.seq) {
          this.loading = false;
          this.render();
        }
      }
    },

    render() {
      if (!$("pickModal") || $("pickModal").hidden) return;
      const q = ($("pickQuery").value || "").trim();
      const budget = state.hw?.budgetGb || 0;
      const taken = Compare.items.length + Compare.pending().length;
      const full = taken >= Compare.max;
      $("pickCount").textContent = `${taken} of ${Compare.max} in the battle`;
      $("pickHint").textContent = this.loading ? "Looking for models that fit this PC…"
        : this.error ? this.error
        : this.rows.length ? `${this.rows.length} chat models that fit this PC${q ? ` for “${q}”` : ""}, most downloaded first.`
        : "Nothing that fits matched. Try a shorter search.";

      const list = $("pickList");
      list.innerHTML = "";
      if (this.loading) {
        list.innerHTML = `<div class="skeleton w-90"></div><div class="skeleton w-70"></div><div class="skeleton w-80"></div>`;
        return;
      }
      for (const r of this.rows) {
        const key = r.repoId.toLowerCase();
        const inBattle = Compare.items.some((x) => x.repo.toLowerCase() === key);
        const job = Compare.pending().find((j) => j.input.toLowerCase() === key);
        const row = el("div", "pick-row" + (inBattle ? " in" : ""));
        const name = el("div", "pick-name");
        name.append(el("b", null, shortName(r.repoId)), el("small", null, r.repoId));
        row.append(name);
        const facts = el("div", "pick-facts");
        facts.append(el("b", null, `${tpsRange(r.tpsLow, r.tpsHigh)} tok/s`));
        facts.append(el("small", null, `${gb(r.memGb, 1)} of ${gb(budget, 0)} · ${fmtK(r.downloads)} downloads`));
        row.append(facts);
        const btn = el("button", inBattle || job ? "btn-secondary sm" : "btn-primary sm",
          inBattle ? "In the battle" : job === Compare.current && job ? "Adding…" : job ? "Waiting…" : full ? "Battle is full" : "Add to battle");
        btn.disabled = inBattle || !!job || full;
        btn.addEventListener("click", () => {
          Compare.addByName(r.repoId);
          this.render();
        });
        row.append(btn);
        list.append(row);
      }
    },
  };

  // ---------- library: run a downloaded file ----------
  // One dialog, three ways. llama.cpp opens a chat page and stays in the list of running
  // models until its window closes; LM Studio is handed the file; Ollama gets a command,
  // because it keeps its own copy of every model.
  const LibRun = {
    f: null,
    way: "llama",
    tools: null,
    busy: false,                 // LM Studio is taking the file
    starting: new Set(),         // files being loaded right now
    opts: { ctx: 8192, gpu: true },
    lengths: [[4096, "4K"], [8192, "8K"], [16384, "16K"], [32768, "32K"]],

    key: (f) => `${f.repo}/${f.file}`.toLowerCase(),

    // what the app knows of this file's run: nothing, loading, or ready
    live(f) {
      const run = state.running.find((r) => this.key(r) === this.key(f));
      if (run) return run;
      return this.starting.has(this.key(f)) ? { ready: false } : null;
    },

    // by its name, or by what the library's own check already found
    notChat(f) {
      if (/(^|[-_./])(asr|stt|tts|whisper|speech|vocoder|embed(ding)?s?|rerank(er)?|mmproj)([-_./]|$)/i.test(`${f.repo}/${f.file}`)) return true;
      const row = LibBattle.rows.find((r) => r.e.id === benchKey(f.repo, f.file));
      return !!row && /not a chat model/.test(row.note || "");
    },

    quant(file) {
      const m = /[-._]((?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32))(?=[-._]|$)/i.exec(file.replace(/\.gguf$/i, ""));
      return m ? m[1].replace(/^UD-/i, "") : "";
    },

    // what a library row offers
    buttons(f) {
      const live = this.live(f);
      if (!live) {
        const run = el("button", "btn-primary sm lib-run");
        run.innerHTML = `<svg viewBox="0 0 24 24" width="11" height="11" fill="currentColor"><path d="M7 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5z"/></svg><span>Run</span>`;
        run.title = "Run this model on this PC";
        run.addEventListener("click", () => this.open(f));
        return [run];
      }
      const state_ = el("span", "lib-live" + (live.ready ? "" : " loading"));
      state_.append(el("i"), el("span", null, live.ready ? "Running" : "Loading…"));
      const stop = el("button", "btn-ghost sm", "Stop");
      stop.addEventListener("click", () => this.stop(f));
      if (!live.ready) return [state_, stop];
      const chat = el("button", "btn-primary sm", "Open chat");
      chat.addEventListener("click", () => api("runOpen", { repo: f.repo, file: f.file }));
      return [state_, chat, stop];
    },

    async open(f) {
      this.f = f;
      this.tools = null;
      $("runModal").hidden = false;
      this.render();
      try {
        const t = await api("runTools", { repo: f.repo, file: f.file });
        if (this.f !== f) return;
        this.tools = t;
        if (!t.hasGpu) this.opts.gpu = false;
      } catch (e) {
        toast(e.message, true);
      }
      this.render();
    },

    close() {
      $("runModal").hidden = true;
      this.f = null;
    },

    save() {
      try { localStorage.setItem("mfs_run_opts", JSON.stringify({ way: this.way, ...this.opts })); } catch {}
    },

    command() {
      const f = this.f;
      const t = this.tools || {};
      const path = `"${t.path || f.file}"`;
      if (this.way === "llama")
        return `${t.serverPath ? `"${t.serverPath}"` : "llama-server"} -m ${path} -c ${this.opts.ctx} -ngl ${this.opts.gpu ? 99 : 0} --jinja --port 8080`;
      if (this.way === "lmstudio") return `lms import ${path} ${t.lmsMode || "--copy"} --user-repo "${f.repo}" -y`;
      const q = this.quant(f.file);
      return `ollama run hf.co/${f.repo}${q ? ":" + q : ""}`;
    },

    render() {
      const f = this.f;
      if (!f || $("runModal").hidden) return;
      const t = this.tools;
      const live = this.live(f);
      $("runModalTitle").textContent = f.file;
      $("runModalSub").textContent = `${f.repo} · ${fbytes(f.bytes)}`;

      const says = {
        llama: !t ? "" : live ? (live.ready ? "Running now" : "Loading…") : t.engine ? "Ready on this PC" : "Small download first",
        lmstudio: !t ? "" : t.lms ? "On this PC" : "Not on this PC",
        ollama: !t ? "" : t.ollama ? "On this PC" : "Not on this PC",
      };
      [...$("runWays").children].forEach((b) => {
        const on = b.dataset.way === this.way;
        b.classList.toggle("active", on);
        b.setAttribute("aria-selected", String(on));
        b.classList.toggle("ok", /^(Ready|On this|Running)/.test(says[b.dataset.way]));
        b.querySelector("small").textContent = says[b.dataset.way];
      });

      const panel = $("runPanel");
      panel.innerHTML = "";
      const lede = (text) => panel.append(el("p", "runm-lede", text));
      const option = (label, hint, choices, value, pick) => {
        const row = el("div", "runm-opt");
        const text = el("div", "runm-opt-k");
        text.append(el("b", null, label), el("span", null, hint));
        const chips = el("div", "runm-chips");
        for (const [v, name] of choices) {
          const b = el("button", "runm-chip" + (v === value ? " active" : ""), name);
          b.type = "button";
          b.addEventListener("click", () => {
            pick(v);
            this.save();
            this.render();
          });
          chips.append(b);
        }
        row.append(text, chips);
        panel.append(row);
      };
      const command = () => {
        const text = this.command();
        const more = el("details", "runm-cmd");
        more.append(el("summary", null, "Show the command"));
        const box = el("div", "run-cmd");
        box.append(el("code", null, text));
        const copy = el("button", "btn-ghost sm", "Copy");
        copy.type = "button";
        copy.addEventListener("click", () => copyText(text));
        box.append(copy);
        more.append(box);
        panel.append(more);
      };
      // the two buttons under the panel
      const act = (go, alt) => {
        const g = $("runGoBtn");
        g.textContent = go.text;
        g.disabled = !!go.off;
        g.onclick = go.fn || null;
        const a = $("runAltBtn");
        a.hidden = !alt;
        if (alt) {
          a.textContent = alt.text;
          a.onclick = alt.fn;
        }
      };
      $("runModalNote").textContent = "";
      if (this.notChat(f))
        panel.append(el("p", "runm-warn", "This does not look like a chat model (speech or embeddings). A chat program may refuse to load it."));
      if (!t) {
        act({ text: "Checking this PC…", off: true });
        return;
      }

      if (this.way === "llama") {
        if (live && live.ready) {
          lede(RUN.live(this.lengths.find(([v]) => v === live.context)?.[1] || "8K", live.gpu ? "graphics chip" : "processor"));
          act({ text: "Open chat", fn: () => api("runOpen", { repo: f.repo, file: f.file }) }, { text: "Stop", fn: () => this.stop(f) });
        } else if (live) {
          lede(RUN.loading);
          act({ text: "Loading…", off: true }, { text: "Stop", fn: () => this.stop(f) });
        } else if (!t.engine) {
          lede("llama.cpp is the engine that runs the model. It is not on this PC yet: about 30 MB, chosen for this PC.");
          act({
            text: "Install llama.cpp",
            fn: async () => {
              if (await installEngine("Running a model needs llama.cpp, which is not on this PC yet.")) this.open(f);
            },
          });
        } else {
          lede(RUN.lede);
          option("Conversation length", "Longer remembers more of the chat and uses more memory.",
            this.lengths, this.opts.ctx, (v) => (this.opts.ctx = v));
          if (t.hasGpu)
            option("Run on", "The graphics chip is faster. The processor works when memory is short.",
              [[true, "Graphics"], [false, "Processor"]], this.opts.gpu, (v) => (this.opts.gpu = v));
          command();
          act({ text: "Start", fn: () => this.start() });
        }
      } else if (this.way === "lmstudio") {
        if (t.lms) {
          lede(t.lmsMode === "--hard-link"
            ? "Adds this file to LM Studio's list of models. It is linked, not copied, so it takes no more disk space."
            : "Adds this file to LM Studio's list of models as a copy, because LM Studio keeps its models on another drive.");
          lede("Then open LM Studio, pick the model at the top and start typing.");
          command();
          act({ text: this.busy ? "Adding…" : "Add to LM Studio", off: this.busy, fn: () => this.toLmStudio() });
        } else {
          lede("LM Studio is a desktop app with its own chat window. It is not on this PC, or it has not been opened yet.");
          lede("Install it, open it once, then come back here to add this file to it.");
          act({ text: "Get LM Studio", fn: () => api("openUrl", { url: "https://lmstudio.ai" }) });
        }
      } else {
        const text = this.command();
        lede("One command in a terminal. Ollama downloads its own copy of the model, so it needs the disk space a second time.");
        if (!t.ollama) lede("Ollama is not on this PC yet.");
        const box = el("div", "run-cmd");
        box.append(el("code", null, text));
        panel.append(box);
        if (t.ollama) act({ text: "Copy the command", fn: () => copyText(text) });
        else act({ text: "Get Ollama", fn: () => api("openUrl", { url: "https://ollama.com/download" }) }, { text: "Copy the command", fn: () => copyText(text) });
      }
    },

    async start() {
      const f = this.f;
      const k = this.key(f);
      this.starting.add(k);
      this.render();
      renderLibrary();
      try {
        const r = await api("runLocal", { repo: f.repo, file: f.file, ctx: this.opts.ctx, gpu: this.opts.gpu });
        if (r.ok) {
          toast(RUN.started);
          if (this.f === f) this.close();
        } else toast(r.error || "The model did not start", true);
      } catch (e) {
        toast(e.message, true);
      } finally {
        this.starting.delete(k);
        this.render();
        renderLibrary();
      }
    },

    async stop(f) {
      await api("runStop", { repo: f.repo, file: f.file }).catch(() => {});
      toast("Stopped");
    },

    async toLmStudio() {
      const f = this.f;
      this.busy = true;
      this.render();
      try {
        const r = await api("lmsImport", { repo: f.repo, file: f.file });
        if (r.ok) toast("Added. Open LM Studio and pick it at the top.");
        else toast(r.error || "LM Studio did not take the file", true);
      } catch (e) {
        toast(e.message, true);
      } finally {
        this.busy = false;
        this.render();
      }
    },

    init() {
      try {
        const saved = JSON.parse(localStorage.getItem("mfs_run_opts") || "{}");
        if (["llama", "lmstudio", "ollama"].includes(saved.way)) this.way = saved.way;
        if (this.lengths.some(([v]) => v === saved.ctx)) this.opts.ctx = saved.ctx;
        if (typeof saved.gpu === "boolean") this.opts.gpu = saved.gpu;
      } catch {}
      $("runWays").addEventListener("click", (e) => {
        const b = e.target.closest(".runm-way");
        if (!b) return;
        this.way = b.dataset.way;
        this.save();
        this.render();
      });
      $("closeRunModalBtn").addEventListener("click", () => this.close());
      $("runModal").addEventListener("click", (e) => {
        if (e.target === $("runModal")) this.close();
      });
      document.addEventListener("keydown", (e) => {
        if (e.key === "Escape" && !$("runModal").hidden) this.close();
      });
      // a model started, finished loading, or its window was closed
      on("running", (list) => {
        state.running = list || [];
        renderLibrary();
        this.render();
      });
      api("runList").then((list) => {
        state.running = list || [];
        if (state.running.length) renderLibrary();
      }).catch(() => {});
    },
  };

  // ---------- agents: one model behind an OpenAI- and Anthropic-style address ----------
  // llama.cpp answers both kinds of request itself, with a key, and takes any model name an
  // agent sends; the tab gives the addresses, the key and the name to paste into an agent,
  // and says what the server is doing, so a long first read never looks like a hang.
  const Agents = {
    s: null,            // the host's last status
    files: [],          // downloaded models that can be served
    ctx: 0,             // 0 = Auto: the model's longest that fits in memory, chosen by llama.cpp
    agent: "claude",
    showKey: false,
    lengths: [[0, "Auto"], [16384, "16K"], [32768, "32K"], [65536, "64K"]],

    async opened() {
      try { this.status(await api("agentStatus")); } catch {}
      try {
        const lib = await api("installed");
        this.files = (lib.files || []).filter((f) => f.complete && !f.helper && !LibRun.notChat(f));
      } catch { this.files = []; }
      this.renderModels();
    },

    status(s) {
      this.s = s;
      if (s && (s.state !== "off" || !this.touched)) this.ctx = s.asked;
      this.render();
    },

    renderModels() {
      const sel = $("agModel");
      const s = this.s || {};
      const keep = this.preselect || sel.value || (s.repo && s.file ? `${s.repo}\n${s.file}` : "");
      this.preselect = null;
      sel.innerHTML = "";
      for (const f of this.files) {
        const o = el("option", null, `${shortName(f.file).replace(/\.gguf$/i, "")} · ${fbytes(f.bytes)}`);
        o.value = `${f.repo}\n${f.file}`;
        sel.append(o);
      }
      if (this.files.some((f) => `${f.repo}\n${f.file}` === keep)) sel.value = keep;
      $("agEmpty").hidden = this.files.length > 0;
      this.render();
    },

    picked() {
      const [repo, file] = ($("agModel").value || "").split("\n");
      return repo && file ? { repo, file } : null;
    },

    render() {
      const s = this.s;
      if (!s) return;
      const on = s.state === "on";
      const busy = s.state === "loading";
      const live = on || busy;

      // the footer says the server is on from any tab
      $("sbAgent").hidden = !live;
      $("sbAgentText").textContent = busy ? "Agents: loading" : `Agents: ${s.modelId}`;
      $("sbAgent").classList.toggle("loading", busy);

      $("agModel").disabled = live;
      const chips = $("agCtx");
      chips.innerHTML = "";
      for (const [v, name] of this.lengths) {
        const b = el("button", "runm-chip" + (v === this.ctx ? " active" : ""), name);
        b.type = "button";
        b.disabled = live;
        b.addEventListener("click", () => { this.ctx = v; this.touched = true; this.render(); });
        chips.append(b);
      }
      const go = $("agGoBtn");
      go.textContent = live ? "Stop" : "Start";
      go.className = (live ? "btn-secondary" : "btn-primary") + " ag-go";
      go.disabled = !live && !this.picked();

      // what it is doing
      const box = $("agState");
      box.className = "ag-state " + (s.state === "error" ? "error" : on ? (s.activity.phase === "idle" ? "on" : "busy") : busy ? "busy" : "off");
      const a = s.activity || {};
      let title = "Stopped";
      let sub = "Start a model, then paste these into your agent.";
      if (busy) {
        title = `Loading ${s.modelId}…`;
        sub = "Putting the model in memory. The address works once it is ready.";
      } else if (s.state === "error") {
        title = "It did not start";
        sub = s.error || "";
      } else if (on) {
        if (a.phase === "reading") {
          title = `Reading the agent's request… ${Math.round((a.progress || 0) * 100)}%`;
          sub = a.readSoFar ? `${a.readSoFar.toLocaleString()} tokens read so far. Long requests take a while on this PC; it is working.` : "Long requests take a while on this PC; it is working.";
        } else if (a.phase === "writing") {
          title = "Writing the answer…";
          sub = a.lastRead ? `Read ${a.lastRead.toLocaleString()} tokens in ${fmtDur(a.lastReadSeconds)}.` : "";
        } else if (a.tooBigTokens && !(s.fitOn && s.lastFit && s.lastFit.answered && s.lastFit.fromTokens >= a.tooBigTokens)) {
          // the agent's request does not fit and was not made to: the one error an agent shows without saying why
          box.className = "ag-state error";
          title = `Your agent sent too much: ${a.tooBigTokens.toLocaleString()} tokens`;
          sub = `This model holds at most ${(a.tooBigContext || s.context).toLocaleString()} tokens per request. ` +
            (s.fitOn
              ? "Making it fit did not work for this request. Turn off the agent's MCP servers and extra tools, or start a new chat."
              : "Turn on \"Keep requests short\" below, or turn off the agent's MCP servers and extra tools.");
        } else {
          title = `Ready: ${s.modelId}`;
          sub = (s.context ? `Holds up to ${s.context.toLocaleString()} tokens per request. ` : "") +
            (s.loaded && s.loaded !== s.modelId ? `In memory now: ${s.loaded}. ` : "") +
            this.fitText(s.lastFit) +
            (s.drafting ? "Drafts ahead with its own MTP layers: same answers, sooner. " : "") + (a.requests
            ? `Last request: ${a.lastRead.toLocaleString()} tokens read in ${fmtDur(a.lastReadSeconds)}, answer at ${num(a.lastTps)} tokens a second.`
            : "Waiting for an agent.");
        }
        if (s.onCpu) sub += " Running on the processor: the graphics chip could not load it.";
      }
      $("agStateTitle").textContent = title;
      $("agStateSub").textContent = sub;
      $("agLogBtn").hidden = s.state === "off";
      $("agFit").checked = s.fitOn !== false;
      // this PC's own number, never another machine's
      $("agFitNow").textContent = s.fitOn === false ? ""
        : s.read12k > 0 ? `On this PC: about ${Number(s.targetTokens).toLocaleString()} tokens.`
        : "Not measured on this PC yet: 12,000 tokens until the first long request is read, or test the model in Battle → Best for agents.";
      $("agThinking").checked = !!s.thinking;

      // the four things an agent asks for
      const rows = $("agRows");
      rows.innerHTML = "";
      const masked = s.key ? s.key.slice(0, 9) + "•".repeat(12) + s.key.slice(-4) : "";
      const row = (k, hint, value, shown = value, extra = []) => {
        const r = el("div", "ag-row");
        const t = el("div", "ag-row-k");
        t.append(el("b", null, k), el("span", null, hint));
        const v = el("code", "ag-val", shown || "—");
        v.setAttribute("data-no-i18n", "");
        const copy = el("button", "btn-ghost sm", "Copy");
        copy.type = "button";
        copy.disabled = !value;
        copy.addEventListener("click", () => copyText(value));
        r.append(t, v, ...extra, copy);
        rows.append(r);
      };
      row("OpenAI base URL", "OpenCode, Codex, Cline, Continue, Aider and most others", s.openai);
      row("Anthropic base URL", "Claude Code and other Anthropic-style agents", s.anthropic);
      const show = el("button", "btn-ghost sm", this.showKey ? "Hide" : "Show");
      show.type = "button";
      show.addEventListener("click", () => { this.showKey = !this.showKey; this.render(); });
      const renew = el("button", "btn-ghost sm", "New key");
      renew.type = "button";
      renew.title = "Make a new key. Agents using the old one stop working.";
      renew.addEventListener("click", () => this.newKey());
      row("API key", "Works as a Bearer token or x-api-key", s.key, this.showKey ? s.key : masked, [show, renew]);
      const sel = this.picked();
      const id = live ? s.modelId : sel ? this.idOf(sel.file) : s.modelId;
      row("Model ID", "Any name works, this one is the real one", id);
      // the other models agents may name: the router loads one when asked, one at a time
      const others = (s.models || []).filter((m) => m !== id);
      if (others.length) {
        const r = el("div", "ag-row ag-others");
        const t = el("div", "ag-row-k");
        t.append(el("b", null, "Other models"), el("span", null, "An agent that asks for one of these by name gets it, loaded in a few seconds"));
        const v = el("code", "ag-val", others.join("\n"));
        v.setAttribute("data-no-i18n", "");
        r.append(t, v);
        rows.append(r);
      }

      this.renderHow(s, id);
    },

    // what was left out of the last request so it would fit
    fitText(f) {
      if (!f || !f.answered) return "";
      const parts = [];
      if (f.toolsAfter < f.toolsBefore) parts.push(`kept ${f.toolsAfter} of ${f.toolsBefore} tools`);
      if (f.turnsDropped) parts.push(`left out ${f.turnsDropped} old turn${f.turnsDropped > 1 ? "s" : ""}`);
      if (f.stepsDropped) parts.push(`left out ${f.stepsDropped} old step${f.stepsDropped > 1 ? "s" : ""}`);
      if (f.outputsCut) parts.push(`shortened ${f.outputsCut} long output${f.outputsCut > 1 ? "s" : ""}`);
      if (f.instructionsCut) parts.push("shortened the instructions");
      return `Shortened a request of about ${f.fromTokens.toLocaleString()} tokens: ${parts.join(", ")}. `;
    },

    // the same rule as the host: the file's name, lower case, without .gguf or a split suffix
    idOf(file) {
      const n = String(file).split(/[\\/]/).pop().replace(/\.gguf$/i, "").replace(/-\d{5}-of-\d{5}$/i, "")
        .toLowerCase().replace(/[^a-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "");
      return n || "local-model";
    },

    agents: {
      claude: ["Claude Code", "In PowerShell, then start claude in the same window:"],
      opencode: ["OpenCode", "Add this to opencode.json (in your project, or in %USERPROFILE%\\.config\\opencode):"],
      codex: ["Codex", "Add this to %USERPROFILE%\\.codex\\config.toml. The last line is the key to set in PowerShell:"],
      other: ["Others", "In the agent's model settings choose \"OpenAI compatible\" (or Anthropic), then paste:"],
    },

    // The agent is told the model's limit, so it shortens its own conversation before it is
    // refused; the limit is known once the server runs (with Auto, llama.cpp chooses it).
    snippet(s, id) {
      const key = s.key;
      const limit = s.state === "on" && s.context ? s.context : s.asked || 0;
      if (this.agent === "claude")
        return `$env:ANTHROPIC_BASE_URL="${s.anthropic}"\n$env:ANTHROPIC_AUTH_TOKEN="${key}"\n$env:ANTHROPIC_MODEL="${id}"\n$env:ANTHROPIC_DEFAULT_HAIKU_MODEL="${id}"\nclaude`;
      if (this.agent === "opencode") {
        const models = {};
        for (const m of [id, ...(s.models || []).filter((x) => x !== id)]) {
          models[m] = { name: m };
          if (limit) models[m].limit = { context: limit, output: Math.min(8192, Math.floor(limit / 4)) };
        }
        return JSON.stringify({
          $schema: "https://opencode.ai/config.json",
          provider: {
            modelsfit: {
              npm: "@ai-sdk/openai-compatible",
              name: "Models Fit Studio",
              options: { baseURL: s.openai, apiKey: key },
              models,
            },
          },
        }, null, 2);
      }
      if (this.agent === "codex")
        return `model = "${id}"\nmodel_provider = "modelsfit"\n` +
          (limit ? `model_context_window = ${limit}\nmodel_auto_compact_token_limit = ${Math.floor(limit * 0.75)}\n` : "") +
          `\n[model_providers.modelsfit]\nname = "Models Fit Studio"\nbase_url = "${s.openai}"\nenv_key = "MODELSFIT_API_KEY"\nwire_api = "responses"\n\n# then, in PowerShell before starting codex:\n# $env:MODELSFIT_API_KEY="${key}"`;
      return `Base URL: ${s.openai}\nAPI key:  ${key}\nModel:    ${id}` + (limit ? `\nContext:  ${limit} tokens (the agent's "context window" or "max tokens" setting)` : "");
    },

    renderHow(s, id) {
      const chips = $("agAgents");
      chips.innerHTML = "";
      for (const [k, [name]] of Object.entries(this.agents)) {
        const b = el("button", "runm-chip" + (k === this.agent ? " active" : ""), name);
        b.type = "button";
        b.addEventListener("click", () => {
          this.agent = k;
          try { localStorage.setItem("mfs_agent_how", k); } catch {}
          this.render();
        });
        chips.append(b);
      }
      const known = s.state === "on" && s.context;
      $("agHowLede").textContent = this.agents[this.agent][1] +
        (!known && !s.asked && this.agent !== "claude" ? " Start the model first to add its context limit." : "");
      $("agSnippet").textContent = this.snippet(s, id);
    },

    async go() {
      const s = this.s || {};
      if (s.state === "on" || s.state === "loading") {
        this.status(await api("agentStop"));
        toast("Stopped");
        return;
      }
      const sel = this.picked();
      if (!sel) return;
      const r = await api("agentStart", { repo: sel.repo, file: sel.file, ctx: this.ctx });
      if (!r.ok) toast(r.error || "The server did not start", true);
    },

    async newKey() {
      if (!confirm("Make a new key? Agents that use the old key stop working until you paste the new one.")) return;
      this.status(await api("agentNewKey"));
      toast("New key made");
    },

    init() {
      try {
        const a = localStorage.getItem("mfs_agent_how");
        if (a && this.agents[a]) this.agent = a;
      } catch {}
      $("agGoBtn").addEventListener("click", () => this.go().catch((e) => toast(e.message, true)));
      $("agModel").addEventListener("change", () => this.render());
      $("agLogBtn").addEventListener("click", () => api("agentOpenLog"));
      $("agFit").addEventListener("change", async (e) => this.status(await api("agentSetFit", { on: e.target.checked })));
      $("agBestLink").addEventListener("click", () => {
        switchTab("compare");
        requestAnimationFrame(() => $("agentBest").scrollIntoView({ behavior: "smooth", block: "start" }));
      });
      $("agThinking").addEventListener("change", async (e) => this.status(await api("agentSetThinking", { on: e.target.checked })));
      $("agSnippetCopy").addEventListener("click", () => copyText($("agSnippet").textContent));
      $("sbAgent").addEventListener("click", () => switchTab("agents"));
      on("agent", (s) => this.status(s));
      on("toast", (t) => t && toast(t.text, !!t.error));
      api("agentStatus").then((s) => this.status(s)).catch(() => {});
    },
  };

  // ---------- best for agents: the downloaded models, tested as an agent uses them ----------
  // One model at a time (they share the graphics chip): reading a 12,000-token request, ten small
  // tool tasks, writing speed. Results are kept by the host, so the ranking survives a restart.
  const AgentBest = {
    rows: [],
    queue: [],
    current: null,      // the file being tested and what it is doing
    stopped: false,

    key: (f) => `${f.repo}\n${f.file}`,
    label: (f) => shortName(f.file).replace(/\.gguf$/i, ""),

    async opened() {
      try {
        this.rows = (await api("agentTestList")).filter((f) => !LibRun.notChat(f));
      } catch { this.rows = []; }
      this.render();
      if (!this.community) this.loadCommunity();
    },

    ranked() {
      return [...this.rows].sort((a, b) => (b.tested - a.tested) || (b.points - a.points) || (a.readSeconds - b.readSeconds));
    },

    render() {
      const busy = !!this.current;
      const tested = this.rows.filter((r) => r.tested);
      $("abRunBtn").hidden = busy;
      $("abRunBtn").disabled = !this.rows.length;
      $("abRunBtn").textContent = tested.length && tested.length === this.rows.length ? "Test again" : tested.length ? "Test the others" : "Test my models";
      $("abStopBtn").hidden = !busy;
      const c = this.current;
      $("abStatus").textContent = !c ? (this.rows.length ? "" : "No downloaded chat models yet.")
        : `Testing ${this.label(c)}: ` + ({
          loading: "loading it", "loading-cpu": "loading it on the processor", reading: "reading 12,000 tokens",
          tools: `tool task ${c.n || 1} of ${c.total || 10}`, writing: "writing",
        }[c.phase] || "starting") + (this.queue.length ? ` · ${this.queue.length} more after this` : "");

      const ranked = this.ranked();
      const best = ranked.find((r) => r.tested);
      $("abHint").textContent = best ? `${this.label(best)} is the best of your models for agents on this PC.` : "Your downloaded models, tested the way an AI agent uses them";

      const list = $("abRows");
      list.innerHTML = "";
      ranked.forEach((r, i) => {
        const winner = r === best;
        const row = el("div", "lb-row ab-row" + (winner ? " winner" : "") + (r.tested ? "" : " out"));
        row.append(el("span", "lb-rank", r.tested ? `#${i + 1}` : "—"));
        const name = el("span", "lb-name");
        const testing = c && this.key(c) === this.key(r);
        name.append(el("b", null, this.label(r)), el("small", null,
          testing ? "testing now…"
          : r.tested ? `Reads 12K tokens in ${fmtDur(r.readSeconds)} · right tool ${r.correct} of ${r.total} · writes ${num(r.writeTps)} tok/s` + (r.thinking ? " · thinking on" : "")
          : "Not tested yet"));
        if (r.tested && !testing) {
          const share = el("button", "ag-link ab-share", "Share this result");
          share.type = "button";
          share.title = "Opens a GitHub page with this result filled in (graphics chip, memory, model, scores) for you to post";
          share.addEventListener("click", () => this.share(r));
          name.append(share);
        }
        row.append(name);
        const track = el("div", "cmp-track");
        const bar = el("i");
        bar.dataset.w = String(r.tested ? r.points : 0);
        track.append(bar);
        row.append(track);
        const pts = el("span", "lb-pts");
        pts.append(el("b", null, r.tested ? String(r.points) : "—"), el("small", null, "points"));
        row.append(pts);
        if (winner) {
          const use = el("button", "btn-secondary sm", "Use in Agents");
          use.type = "button";
          use.title = "Pick this model in the Agents tab";
          use.addEventListener("click", () => {
            Agents.preselect = this.key(r);
            switchTab("agents");
          });
          row.append(use);
        } else row.append(el("span"));
        list.append(row);
      });
      requestAnimationFrame(() => list.querySelectorAll(".cmp-track > i").forEach((b) => (b.style.width = b.dataset.w + "%")));
      this.renderCommunity();
    },

    // what others measured on the same graphics chip, from the shared list on GitHub
    community: null,

    async loadCommunity() {
      try { this.community = await api("agentCommunity"); } catch { this.community = null; }
      this.renderCommunity();
    },

    renderCommunity() {
      const box = $("abCommunity");
      const c = this.community;
      box.hidden = !c;
      if (!c) return;
      box.innerHTML = "";
      const gpu = String(c.gpu || "this graphics chip").replace(/\s*\(\d+\s*GB\)/i, "");
      box.append(el("h3", null, `Measured by others with ${gpu}`));
      if (!c.rows?.length) {
        box.append(el("p", "ab-lede", "No shared results for this graphics chip yet. Share yours to start the list."));
        return;
      }
      for (const r of c.rows) {
        const row = el("div", "lb-row ab-row out");
        row.append(el("span", "lb-rank", "·"));
        const name = el("span", "lb-name");
        name.append(el("b", null, shortName(r.file).replace(/\.gguf$/i, "")),
          el("small", null, `Reads 12K tokens in ${fmtDur(r.readSeconds)} · right tool ${r.correct} of ${r.total} · writes ${num(r.writeTps)} tok/s · ${r.results} result${r.results > 1 ? "s" : ""}`));
        row.append(name, el("span"));
        const pts = el("span", "lb-pts");
        pts.append(el("b", null, String(r.points)), el("small", null, "points"));
        row.append(pts, el("span"));
        box.append(row);
      }
    },

    async share(r) {
      if (!confirm("Share this result? A GitHub page opens in your browser with it filled in: your graphics chip, memory, the model and its scores. Nothing is sent until you press Submit there.")) return;
      const res = await api("agentShare", { repo: r.repo, file: r.file }).catch((e) => ({ ok: false, error: e.message }));
      if (!res.ok) toast(res.error || "Could not open the page", true);
    },

    run() {
      const all = this.rows.every((r) => r.tested);
      this.queue = this.rows.filter((r) => all || !r.tested).map((r) => ({ repo: r.repo, file: r.file }));
      this.stopped = false;
      this.next();
    },

    async next() {
      const f = this.queue.shift();
      if (!f || this.stopped) {
        this.current = null;
        this.queue = [];
        this.render();
        return;
      }
      this.current = { ...f, phase: "starting" };
      this.render();
      try {
        const r = await api("agentTestRun", { repo: f.repo, file: f.file });
        if (!r.started) {
          toast(r.error || "The test did not start", true);
          this.current = null;
          this.queue = [];
          this.render();
        }
      } catch (e) {
        toast(e.message, true);
        this.current = null;
        this.render();
      }
    },

    async stop() {
      this.stopped = true;
      this.queue = [];
      await api("cancelMeasure").catch(() => {});
    },

    init() {
      $("abRunBtn").addEventListener("click", () => this.run());
      $("abStopBtn").addEventListener("click", () => this.stop());
      on("agentTest", async (ev) => {
        if (!ev) return;
        if (["done", "failed", "cancelled"].includes(ev.phase)) {
          if (ev.phase === "failed") toast(`${shortName(ev.file)}: ${ev.error}`, true);
          await this.opened();
          this.next();
          return;
        }
        this.current = { repo: ev.repo, file: ev.file, phase: ev.phase, n: ev.n, total: ev.total };
        this.render();
      });
    },
  };

  // ---------- updates: a newer app or a newer llama.cpp ----------
  // The host asks GitHub at most once a day; the footer says so when something is newer, and
  // nothing is downloaded until a button is pressed.
  // The About dialog: what the app is, who made it, where it lives.
  const About = {
    repo: "https://github.com/uranrog-01/models-fit-studio",
    site: "https://uranrog-01.github.io/models-fit-studio/",

    open() {
      const ver = new URLSearchParams(location.search).get("ver");
      $("aboutVer").textContent = ver ? "v" + ver : "";
      $("aboutModal").hidden = false;
    },

    close() { $("aboutModal").hidden = true; },

    init() {
      $("aboutBtn")?.addEventListener("click", () => this.open());
      $("settingsAboutBtn")?.addEventListener("click", () => this.open());
      $("closeAboutBtn")?.addEventListener("click", () => this.close());
      $("aboutModal")?.addEventListener("click", (e) => { if (e.target === $("aboutModal")) this.close(); });
      document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("aboutModal").hidden) this.close(); });
      $("aboutSiteBtn")?.addEventListener("click", () => api("openUrl", { url: this.site }));
      $("aboutGithubBtn")?.addEventListener("click", () => api("openUrl", { url: this.repo }));
      $("aboutNoticesBtn")?.addEventListener("click", () => api("openUrl", { url: this.repo + "/blob/main/LICENSE" }));
    },
  };

  const Updates = {
    data: null,
    busy: false,

    async check(force = false) {
      try {
        this.data = await api("updatesCheck", { force });
      } catch { this.data = null; }
      this.render();
    },

    render() {
      const d = this.data;
      const app = d?.app;
      const eng = d?.engine;
      const newer = !!(app?.newer || eng?.newer);
      $("sbUpdate").hidden = !newer;
      $("sbUpdateText").textContent = app?.newer ? `Version ${app.latest.replace(/^v/i, "")} available` : "Faster engine available";
      if (!d) {
        $("updHint").textContent = "Could not reach GitHub";
        return;
      }
      $("updHint").textContent = "Checked once a day";
      $("updAppText").textContent = app?.newer
        ? `Models Fit Studio ${app.latest.replace(/^v/i, "")} is available`
        : `Models Fit Studio ${app?.current || ""}: up to date`;
      $("updAppHint").textContent = app?.latest ? (app.newer ? `You have ${app.current}. The download page opens in your browser.` : "New versions are published on GitHub.")
        : "No release is published yet, or GitHub could not be reached.";
      $("updAppBtn").hidden = !app?.newer;

      const cur = eng?.current || "?";
      $("updEngineText").textContent = eng?.newer
        ? `llama.cpp ${eng.latest} is available (you have ${cur})`
        : `llama.cpp ${cur}${eng?.latest ? ": up to date" : ""}`;
      $("updEngineHint").textContent = eng?.newer
        ? `About ${eng.mb} MB from the official llama.cpp release, checked against its published hash. New builds are often faster.`
        : "The engine that runs models. New builds are often faster.";
      $("updEngineBtn").hidden = !eng?.newer;
      $("updEngineBtn").disabled = this.busy;
      $("updEngineBtn").textContent = this.busy ? "Updating…" : `Update engine (${eng?.mb || "?"} MB)`;
    },

    async updateEngine() {
      this.busy = true;
      this.render();
      try {
        const r = await api("updateEngine");
        toast(`llama.cpp ${r.build} is installed` + (r.restartAgents ? ". Stop and start the Agents server to use it." : "."));
        refreshEngine();
        await this.check(true);
      } catch (e) {
        toast(e.message, true);
      } finally {
        this.busy = false;
        this.render();
      }
    },

    init() {
      $("updCheckBtn").addEventListener("click", () => this.check(true));
      $("updAppBtn").addEventListener("click", () => this.data?.app?.url && api("openUrl", { url: this.data.app.url }));
      $("updEngineBtn").addEventListener("click", () => this.updateEngine());
      $("sbUpdate").addEventListener("click", () => {
        switchTab("settings");
        requestAnimationFrame(() => $("settingsUpdatesCard").scrollIntoView({ behavior: "smooth", block: "start" }));
      });
      // a little after start, so it never slows the first screen
      setTimeout(() => this.check(false), 4000);
    },
  };

  // ---------- suggest: two or three models worth trying on this PC ----------
  const Suggest = {
    data: null,
    loading: false,
    kinds: {
      best: ["Best overall", "The highest quality that still feels quick on this PC."],
      smartest: ["Smartest", "The highest quality that still writes at reading pace."],
      fastest: ["Fastest", "The quickest one that still answers well."],
    },

    get open() {
      return $("suggestBanner").classList.contains("open");
    },

    async toggle(show = !this.open) {
      $("suggestBanner").classList.toggle("open", show);
      $("suggestBanner").setAttribute("aria-hidden", String(!show));
      $("suggestBtn").setAttribute("aria-expanded", String(show));
      if (!show) return;
      this.render();
      if (this.data || this.loading) return;
      this.loading = true;
      this.render();
      try {
        this.data = await api("suggest");
      } catch (e) {
        this.data = { picks: [], error: e.message };
      }
      this.loading = false;
      this.render();
    },

    render() {
      const box = $("suggestCards");
      box.innerHTML = "";
      $("suggestSub").textContent = "";
      if (this.loading || !this.data) {
        for (let i = 0; i < 3; i++) {
          const ghost = el("div", "sg-card sg-ghost");
          ghost.style.setProperty("--i", i);
          ghost.append(el("span", "skeleton w-50"), el("span", "skeleton w-80"), el("span", "skeleton w-70"));
          box.append(ghost);
        }
        $("suggestSub").textContent = "Looking through the models that fit…";
        return;
      }
      const { picks, budgetGb } = this.data;
      if (!picks.length) {
        box.append(el("p", "sg-none", this.data.error || "No well-known chat model fits this PC comfortably. Try the search below with a small size, like 1B or 3B."));
        return;
      }
      $("suggestSub").textContent = "Chosen from the models that fit, by quality and speed.";
      picks.forEach((p, i) => {
        const [label, why] = this.kinds[p.kind] || [p.kind, ""];
        const card = el("article", "sg-card");
        card.style.setProperty("--i", i);
        card.append(el("span", "sg-kind " + p.kind, label));
        const name = el("b", "sg-name", p.name);
        name.title = p.repo;
        card.append(name);
        const facts = el("span", "sg-facts");
        facts.append(el("span", null, `${tpsRange(p.tpsLow, p.tpsHigh)} tok/s`));
        facts.append(el("span", null, budgetGb > 0 ? `${num(p.memGb, 1)} of ${num(budgetGb, 0)} GB memory` : `${num(p.memGb, 1)} GB memory`));
        if (p.vision) facts.append(el("span", null, "Reads images"));
        card.append(facts, el("span", "sg-why", why));
        const actions = el("span", "sg-actions");
        const check = el("button", "btn-primary sm", "Check");
        check.title = "Check this model against this PC and see its files";
        check.addEventListener("click", () => {
          $("modelInput").value = p.repo;
          runScan(p.repo);
        });
        const fight = el("button", "btn-ghost sm", "Add to battle");
        fight.addEventListener("click", () => {
          Compare.addByName(p.repo);
          toast("Added to Battle");
        });
        actions.append(check, fight);
        card.append(actions);
        box.append(card);
      });
    },

    init() {
      $("suggestBtn").addEventListener("click", () => this.toggle());
      $("suggestCloseBtn").addEventListener("click", () => this.toggle(false));
    },
  };

  // ---------- languages ----------
  // The page is written in English; i18n.js swaps the words. Each language shows its
  // own name, so it can be found by someone who cannot read the current one.
  const Lang = {
    list: [
      ["en", "English"], ["zh", "简体中文"], ["es", "Español"], ["pt", "Português"], ["ru", "Русский"],
      ["de", "Deutsch"], ["fr", "Français"], ["ja", "日本語"], ["ko", "한국어"], ["tr", "Türkçe"], ["it", "Italiano"],
    ],
    current: "en",

    async apply(code, save) {
      if (!this.list.some(([c]) => c === code)) code = "en";
      this.current = await window.I18n.set(code);
      try { localStorage.setItem("mfs_lang", this.current); } catch {}
      $("langName").textContent = (this.list.find(([c]) => c === this.current) || this.list[0])[1];
      [...$("langMenu").children].forEach((b) => b.classList.toggle("active", b.dataset.lang === this.current));
      if (save) api("saveSettings", { language: this.current }).catch(() => {});
    },

    toggle(open) {
      const menu = $("langMenu");
      const btn = $("langBtn");
      const show = open ?? menu.hidden;
      menu.hidden = !show;
      btn.setAttribute("aria-expanded", String(show));
      if (!show) return;
      // under its button when it fits there; otherwise on the side with more room, scrolling inside
      const r = btn.getBoundingClientRect();
      menu.style.maxHeight = "";
      const h = menu.offsetHeight;
      const below = window.innerHeight - r.bottom - 44;   // the footer stays clear
      const above = r.top - 14;
      const under = h <= below || below >= above;
      const room = Math.max(120, under ? below : above);
      menu.style.maxHeight = `${room}px`;
      menu.style.left = `${Math.max(8, r.right - menu.offsetWidth)}px`;
      menu.style.top = under ? `${r.bottom + 6}px` : `${Math.max(8, r.top - 6 - Math.min(h, room))}px`;
      menu.style.bottom = "auto";
    },

    init() {
      const menu = $("langMenu");
      for (const [code, name] of this.list) {
        const b = el("button");
        b.type = "button";
        b.setAttribute("role", "menuitem");
        b.dataset.lang = code;
        b.append(el("span", null, name));
        menu.append(b);
      }
      let start = "en";
      try { start = new URLSearchParams(location.search).get("lang") || localStorage.getItem("mfs_lang") || "en"; } catch {}
      this.apply(start, false);
      $("langBtn").addEventListener("click", (e) => {
        e.stopPropagation();
        this.toggle();
      });
      menu.addEventListener("click", (e) => {
        const b = e.target.closest("button");
        if (!b) return;
        this.apply(b.dataset.lang, true);
        this.toggle(false);
      });
      document.addEventListener("click", (e) => {
        if (!menu.hidden && !e.target.closest("#langMenu")) this.toggle(false);
      });
      document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") this.toggle(false);
      });
      window.addEventListener("resize", () => this.toggle(false));
      document.querySelector(".main").addEventListener("scroll", () => this.toggle(false), { passive: true });
    },
  };

  // ---------- themes ----------
  // The look is a data-theme on the page; the host keeps the choice and paints the
  // window's title bar to match.
  const Theme = {
    names: { default: "Classic", ember: "Ember", solar: "Solar", glass: "Glass" },
    current: "default",

    apply(name, save) {
      this.current = name in this.names ? name : "default";
      if (this.current === "default") delete document.documentElement.dataset.theme;
      else document.documentElement.dataset.theme = this.current;
      try { localStorage.setItem("mfs_theme", this.current); } catch {}
      $("themeName").textContent = this.names[this.current];
      [...$("themeMenu").children].forEach((b) => b.classList.toggle("active", b.dataset.themeName === this.current));
      if (save) api("saveSettings", { theme: this.current }).catch(() => {});
    },

    // the menu sits above its button, wherever the footer puts it
    toggle(open) {
      const menu = $("themeMenu");
      const btn = $("themeBtn");
      const show = open ?? menu.hidden;
      menu.hidden = !show;
      btn.setAttribute("aria-expanded", String(show));
      if (!show) return;
      const r = btn.getBoundingClientRect();
      menu.style.left = `${Math.max(8, r.left)}px`;
      menu.style.bottom = `${window.innerHeight - r.top + 6}px`;
    },

    init() {
      // the page already wears the theme the host named in the address; this only marks it in the menu
      this.apply(document.documentElement.dataset.theme || "default", false);
      $("themeBtn").addEventListener("click", (e) => {
        e.stopPropagation();
        this.toggle();
      });
      $("themeMenu").addEventListener("click", (e) => {
        const b = e.target.closest("button");
        if (!b) return;
        this.apply(b.dataset.themeName, true);
        this.toggle(false);
      });
      document.addEventListener("click", (e) => {
        if (!$("themeMenu").hidden && !e.target.closest("#themeMenu")) this.toggle(false);
      });
      document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") this.toggle(false);
      });
      window.addEventListener("resize", () => this.toggle(false));
    },
  };

  // ---------- the side panel folds away ----------
  const SidePanel = {
    set(hidden) {
      document.querySelector(".app").classList.toggle("side-hidden", hidden);
      $("sidebarToggle").classList.toggle("on", !hidden);
      $("sidebarShowBtn").hidden = !hidden;
      try { localStorage.setItem("mfs_side_hidden", hidden ? "1" : "0"); } catch {}
    },

    init() {
      let hidden = false;
      try { hidden = localStorage.getItem("mfs_side_hidden") === "1"; } catch {}
      this.set(hidden);
      const flip = () => this.set(!document.querySelector(".app").classList.contains("side-hidden"));
      $("sidebarToggle").addEventListener("click", flip);
      $("sidebarHideBtn").addEventListener("click", flip);
      $("sidebarShowBtn").addEventListener("click", flip);
    },
  };

  // ---------- Glass theme: light follows the pointer across a panel ----------
  // The panel under the pointer gets the pointer's place as two numbers; the theme's
  // style sheet paints a soft highlight there. Other themes never read them.
  const Sheen = {
    last: null,
    frame: 0,

    init() {
      document.addEventListener("pointermove", (e) => {
        if (Theme.current !== "glass" || this.frame) return;
        this.frame = requestAnimationFrame(() => {
          this.frame = 0;
          const panel = e.target.closest?.(".card, .verdict, .modal-dialog, .sidebar");
          if (this.last && this.last !== panel) {
            this.last.style.removeProperty("--mx");
            this.last.style.removeProperty("--my");
          }
          this.last = panel || null;
          if (!panel) return;
          const r = panel.getBoundingClientRect();
          panel.style.setProperty("--mx", `${Math.round(e.clientX - r.left)}px`);
          panel.style.setProperty("--my", `${Math.round(e.clientY - r.top)}px`);
        });
      }, { passive: true });
    },
  };

  // ---------- wire up ----------
  function init() {
    initHwCollapse();
    initQuantGuideModal();
    initSettingsEvents();
    initGatedModal();
    initSpeedPreview();
    Compare.init();
    Share.init();
    Theme.init();
    SidePanel.init();
    Sheen.init();
    LibRun.init();
    Agents.init();
    AgentBest.init();
    Updates.init();
    Suggest.init();
    Lang.init();
    $("nvidiaGetBtn")?.addEventListener("click", () => installNvidia());
    $("nvidiaRemoveBtn")?.addEventListener("click", async () => {
      await api("removeNvidia");
      toast("The NVIDIA engine was removed");
      loadSpeedSettings();
    });
    try { $("aboutVersion").textContent = "Version " + (new URLSearchParams(location.search).get("ver") || ""); } catch {}
    About.init();
    $("cmpPickBtn")?.addEventListener("click", () => Picker.open());
    $("closePickModalBtn")?.addEventListener("click", () => Picker.close());
    $("pickDoneBtn")?.addEventListener("click", () => Picker.close());
    $("pickModal")?.addEventListener("click", (e) => {
      if (e.target === $("pickModal")) Picker.close();
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !$("pickModal").hidden) Picker.close();
    });
    $("pickQuery")?.addEventListener("input", () => {
      clearTimeout(Picker.timer);
      Picker.timer = setTimeout(() => Picker.load(), 500);
    });
    $("libBattleRunBtn")?.addEventListener("click", () => LibBattle.run());
    $("libBattleOpenBtn")?.addEventListener("click", () => LibBattle.open());

    // Back navigation buttons
    $("verdictBackToAllBtn")?.addEventListener("click", () => {
      $("verdictWrap").hidden = true;
      $("exploreCard")?.scrollIntoView({ behavior: "smooth", block: "start" });
    });

    Run.init();
    // the top box: a link is checked, a name is handed to the list's search
    $("scanBtn").addEventListener("click", () => submitHero());
    $("modelInput").addEventListener("keydown", (e) => {
      if (e.key === "Enter") submitHero();
    });
    $("modelInput").addEventListener("input", () => {
      $("scanBtn").querySelector(".btn-label").textContent = heroLabel();
    });
    // the model list: live Hugging Face search (debounced, one cached request per search)
    let hfTimer;
    $("hfQuery")?.addEventListener("input", () => {
      clearTimeout(hfTimer);
      hfTimer = setTimeout(loadExplore, 600);
    });
    $("hfQuery")?.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      clearTimeout(hfTimer);
      loadExplore();
    });
    $("pasteBtn").addEventListener("click", async () => {
      try {
        const t = await navigator.clipboard.readText();
        if (t) {
          $("modelInput").value = t.trim();
          $("scanBtn").querySelector(".btn-label").textContent = heroLabel();
          submitHero();
        } else toast("Clipboard is empty");
      } catch {
        toast("Clipboard blocked — paste with Ctrl+V", true);
      }
    });
    $("rescanBtn").addEventListener("click", async () => {
      toast("Re-scanning hardware…");
      await scanHardware();
      loadExplore();
      if (state.result && !state.result.error) renderResult(state.result);
    });

    $("tabsBar").addEventListener("click", (e) => {
      const b = e.target.closest(".tab");
      if (b) switchTab(b.dataset.tab);
    });


    $("hfSort")?.addEventListener("change", () => loadExplore());
    $("hfFormat")?.addEventListener("change", () => loadExplore());
    // the filter is remembered; it is on until the user turns it off
    try {
      if (localStorage.getItem("mfs_fits_only") === "0" && $("hfFitsOnly")) $("hfFitsOnly").checked = false;
    } catch {}
    $("hfFitsOnly")?.addEventListener("change", (e) => {
      try { localStorage.setItem("mfs_fits_only", e.target.checked ? "1" : "0"); } catch {}
      loadExplore();
    });
    $("hfRefreshBtn")?.addEventListener("click", () => loadExplore(true));

    $("openFolderBtn").addEventListener("click", () => api("openFolder"));
    $("dlResumeAllBtn")?.addEventListener("click", async () => {
      const { resumed } = await api("resumeAllDownloads");
      toast(resumed
        ? `Resuming ${resumed} download${resumed === 1 ? "" : "s"} from where they stopped`
        : "Nothing to resume");
    });
    $("changeLibFolderBtn")?.addEventListener("click", async () => {
      try {
        const res = await api("pickFolder");
        if (res && res.ok && res.dir) {
          toast("Model storage updated to " + res.dir);
          await refreshLibrary();
        }
      } catch (e) {
        toast("Could not change folder: " + e.message, true);
      }
    });
    $("refreshLibBtn").addEventListener("click", async () => {
      await refreshLibrary();
      await refreshDownloads();
      toast("Library refreshed");
    });

    on("uiVisibility", ({ visible }) => {
      state.uiVisible = visible;
    });

    (async () => {
      try {
        // The library must not wait behind hardware profiling or network catalog requests.
        await Promise.allSettled([refreshLibrary(), refreshDownloads(), loadSettings()]);
        await scanHardware();
        loadExplore(false);
      } catch (e) {
        $("hwBody").innerHTML = `<div class="bench-hint" style="color:var(--bad)">${esc(
          e.message
        )}</div><div class="bench-hint">Required profiling tools missing from <code>tools\\</code> directory or PATH.</div>`;
        return;
      }

      // CLI launch support: ModelFitStudio.exe "huggingface.co/org/repo"
      const m = /#model=(.+)$/.exec(location.hash);
      if (m) {
        const value = decodeURIComponent(m[1]);
        $("modelInput").value = value;
        await runScan(value);
      }
      snapshot("ready");
    })();
  }

  document.addEventListener("DOMContentLoaded", init);
})();
