# Models Fit Studio

**Find the AI model that runs well on your PC, measure it for real, and start chatting, all in one free Windows app.**

![Models Fit Studio: a model check, speed preview, battle and the Agents server](../docs/assets/demo.gif)

Made by **zer0ne**. Free, under the MIT licence. Windows 10 and 11, in 11 languages.

The program file and its data folder keep the short name `ModelFitStudio`.

Paste a Hugging Face link (or a model name), and the app scans **your** machine —
CPU, RAM, GPU and its real memory budget — then tells you:

- the file size of every GGUF quantisation in the repo
- the estimated speed of each one, as a **tok/s range**
- how much context you will actually get
- what the model is (dense / MoE, parameters, role, licence)
- **which file to download**
- and a clear visual verdict: ✅ Runs on this machine · ⚠️ Runs — but tight · ⚠️ Runs, but slower · ❌ Won't fit

Everything is scored against the machine the app is running on. Nothing is hardcoded —
the hardware is scanned each time the app starts.

The app checks, downloads, measures and starts models. llama.cpp, the engine that runs
them, ships inside the app, so all of that works with no extra download.

## What it does

- **Check box** (top) — paste a link or `org/model` and it is checked against this PC. A
  plain name typed there is handed to the list's search.
- **Suggest** — one button on the Models page. A banner slides open with two or three
  models for this PC: the **best overall** (the highest quality that still feels quick,
  12 tok/s or more), the **smartest** (the highest quality that still writes at reading
  pace) and the **fastest** that still answers well. They are picked from the general chat
  models that fit, with a GGUF copy from a publisher people use; each has **Check** and
  **Add to battle**.
- **Models** — one list of Hugging Face models with its own search and a **Fits this PC**
  filter (on by default). Every card carries a fit and speed estimate for a Q4-class build, read
  from the model's real parameter count, and a **Check** button that runs the full scan on
  your machine. When that build is over the budget and a smaller version of the same model
  is not, the card says **Smaller fits** and shows the smaller one's numbers, so the card
  and the check agree. A model that is over the budget in every version but still within
  what the whole PC can hold says **Runs slower**. One cached API request per search.
- **Check** — the verdict, the memory needed, every quant with its size, speed range
  and fit, and how the recommended file fits this machine (Turbo Fit: full GPU, a compact
  cache, MoE experts in RAM on a card with its own memory, or too large). On a chip that
  shares system memory no split between graphics and processor is advised: measured
  there, a split wrote slower than either alone. Repos without GGUF files suggest GGUF
  mirrors instead of dead-ending.
- **Speed preview** — one answer first (perfect, good, tight, slow or too big for this
  PC), three facts, and sample text typed at the estimated pace. It is a preview: no model
  runs unless you press Measure.
- **Run it locally** — on a checked model: the steps and the exact command for llama.cpp,
  Ollama and LM Studio. When the file and llama.cpp are on this PC, **Start it now** opens
  llama.cpp in its own terminal window and its chat page in the browser; closing that
  window stops the model.
- **Run from the Library** — every downloaded file has a **Run** button. It opens one
  dialog with three ways: llama.cpp (pick the conversation length and graphics chip or
  processor, then Start), LM Studio (the file is added to LM Studio's list with its own
  `lms import`, as a hard link when both are on one drive) and Ollama (the command). The
  dialog says which of the three are on this PC. A model the app started shows
  **Running** in its row, with **Open chat** and **Stop**; closing its window does the same.
- **Which version should I pick?** — a short guide to the GGUF versions: bits per weight
  and size (llama.cpp's own figures for an 8B model) and what to expect from each.
- **Measure speed** — once a file is downloaded, one click runs it in llama.cpp, times
  three answers and keeps the real tok/s and the time to the first word. If the GPU cannot
  load a file the run is repeated on the CPU and marked. The server lives only for the
  measurement.
- **The engine is built in** — llama.cpp ships in the `engine\` folder next to the app
  (the Vulkan build, which draws on Intel, AMD and NVIDIA cards and on the processor
  alone), so measuring and running work offline from the first start. On a PC with an
  **NVIDIA** card the app detects it and switches by itself to NVIDIA's own build (CUDA)
  when that build is on the PC. It is fetched by itself the first time the app runs on such
  a PC (about 580 MB, hash-checked, from the official llama.cpp release): the hardware
  card shows the progress and a **Stop** button, and stopping is remembered. **Settings →
  Get the NVIDIA engine** fetches it later. It picks CUDA 13.4 or 12.4 from the card and its
  driver, and goes back to the built-in engine if a model will not load with NVIDIA's.
- **Test quality** — once a file is downloaded, the app asks it 40 questions with one right
  answer each (reasoning, math, knowledge, following instructions, reading code) and keeps
  the share it got right. The model may reason; its final line is what is marked. The test
  is small on purpose, a few minutes on a laptop: it ranks models and shows what a heavy
  quant costs. The large public sets (MMLU, GSM8K and their 100-question "tiny" versions)
  measure more but take far longer on a PC; llama.cpp's perplexity tool measures what a
  quant loses against its own F16, not how good a model is.
- **Measured by others** — on a checked model, the speeds other people measured for it on
  their hardware (`Web/community.json`, built by `tools/make-community.py` from a public
  set of community benchmarks). **Settings → Share my speeds** adds yours: a benchmark
  runs on your measured models, the dialog shows exactly what would be sent and where, and
  only on a second click is a pull request opened on GitHub from your account.
- **Battle** — up to four models fight on this PC. Each gets points out of 100 (quality
  45, speed 30, memory 15, context 10, on fixed scales for this machine), the rounds show
  where the points came from, and one winner is named with its reason. A model that does
  not fit is knocked out with the reason, not refused; a repo without GGUF files is
  replaced by its GGUF mirror. **Choose models that fit** opens a list of chat models that
  fit this PC, one click each; adds wait in line and a banner says what is being checked.
  The winner is graded (perfect, good, too tight, too slow) and can be downloaded there.
  When every fighter has been through the quality test, the quality round uses those
  scores instead of the estimate. **Share picture** saves the result as a PNG and copies
  it to the clipboard. A model enters with the version already on your disk, or
  the version that scores best here. Every speed says whether it is measured or an estimate.
- **Library** — also compares the models you already downloaded: which of them is best
  for this PC.
- **Side panel and footer** — the side panel folds away (the « in it, or the panel button
  in the footer) and comes back from the tab it leaves on the left edge. Maximised, the
  window stops at the taskbar.
- **Themes** — four looks, chosen from the Theme menu in the footer: Default, Ember (near-black, pill
  controls, copper-to-cream data marks), Solar (a light warm page carrying charcoal
  cards, orange actions, yellow marks) and Glass (frosted panels over a dusk backdrop, a
  yellow accent, white pills for the chosen item; panels arrive empty and fill in, and a
  soft highlight follows the pointer). The choice is kept in `app-settings.json`.
- **Library** — everything you downloaded. Downloads are resumable, run one at a time,
  and report live progress. Files land in `%LOCALAPPDATA%\ModelFitStudio\Models`
  (changeable in Settings).
- **One library with zer0ne code** — when zer0ne code is on the same PC, the models it
  downloaded show in the Library too, marked **From zer0ne code**, and can be run, measured
  and tested here. They are used from zer0ne code's folder, never copied, and a model that
  one app has is not downloaded again by the other. zer0ne code lists this app's models
  the same way. Deleting such a file here removes it for both.
- **Languages** — **Settings → Language**: English, 简体中文, Español, Português, Русский,
  Deutsch, Français, 日本語, 한국어, Türkçe, Italiano. The page is written in English and
  `Web/i18n.js` swaps the words as they appear; a language is one small file in
  `Web/lang/` (about 30 KB each). The text lives in `tools/lang/` and
  `python tools/make-lang.py` builds the files. What a language does not cover stays in
  English: today that is the dialog for gated models, the Share my speeds dialog, and
  rare error messages.
- **Settings** — where models are stored, a Hugging Face token for gated models, cleaning
  up unfinished downloads, and whether closing the window keeps the app in the tray.

## Getting it running

1. Unzip `ModelFitStudio-win-x64.zip` anywhere.
2. Run `ModelFitStudio.exe`.
3. That's it. The app needs:
   - **WebView2 runtime** (preinstalled on Windows 10/11; the app tells you if it is missing)
   - **llmfit.exe** (the fit scorer) and the **engine\** folder (llama.cpp) — both shipped
     in the zip, right next to the exe

## Rebuilding after edits

Double-click **`rebuild.bat`** in the project folder. It regenerates the icon, publishes
a single-file win-x64 build into `dist\`, checks that `llmfit.exe`, the engine and the `Web\` UI really
landed there, and rebuilds `ModelFitStudio-win-x64.zip`, then reads the zip back.

Two things it guards against, because both produce a build that looks fine and isn't:

- **Close the app first.** A running `dist\ModelFitStudio.exe` cannot be replaced, so the
  script asks you to close it (or names `taskkill`) instead of dying inside the publish
  with an `MSB4018`.
- **The zip's paths are checked.** Windows PowerShell's own zipper stores nested entries as
  `Web\app.js`; unpacked by anything but Explorer that is a single file with a backslash in
  its name, so the app starts with no UI. `tools\make-zip.ps1` writes real `Web/` paths, and
  the build fails if the archive does not contain `Web/app.js`.

> ⚠️ Do **not** add `-p:IncludeNativeLibrariesForSelfExtract=true` to the publish line.
> It folds `tools\llmfit.exe` into the single-file bundle where the app can no longer
> run it, and the model list comes up empty.

## Where the numbers come from

- **Memory budget** — the memory the graphics chip can give a model. For a card with its
  own memory that is the card's memory. For a chip that shares system memory (Intel Arc
  and Iris, Radeon in a laptop) it is the share Windows lets it use, usually half of the
  RAM: 16 GB on a 32 GB laptop. It is never more than the RAM minus 6 GB. It is not disk
  space: the disk only decides whether a download has room.
- **The second limit** — past the budget a model still runs, with part of it in system
  memory, slower. That works up to 92% of what the whole PC may give a model (80% of the
  RAM, or the card's memory when that is more): about 24 GB on a 32 GB laptop. Such a file
  is **Slower**, not refused. On a chip that shares system memory its speed is shown as
  three quarters of the chip's own, measured on the author's laptop (Arc 140T, llama.cpp
  b11574): a 1.7B Q4_K_M wrote 41 tok/s on the chip, 31 on the processor alone and 19 to
  36 split between the two; a 7B Q2_K wrote 8, 13 and 6. A split was never the fastest,
  so the estimate is the processor running the whole model. On a card with its own memory
  the part outside it is counted at least twice as slow; that case is not measured.
- **Fit / memory verdict** — the bundled fit scorer (`llmfit.exe`) scores every quant against the
  detected memory budget (VRAM + shared pool, with a correction for Windows wrapping
  pools over 4 GB).
- **Which files are the model** — a repo often holds small files that only help the model:
  a vision projector (`mmproj`), a draft head that speeds it up (`mtp-…`), an importance
  matrix, a draft model. They are left out of the check by name, and so is any file far smaller than the
  parameter count allows (under 0.1 GB per billion parameters). In the Library such a
  file is marked **Helper file** and is not run, measured or compared.
- **A draft head (`mtp-…`)** — a small file some repositories ship beside the model. It
  guesses the next few tokens and the model confirms them, so the model writes faster
  with the same output. The check names it and the llama.cpp command in **Run** carries
  the switches (`--spec-type draft-mtp --spec-draft-model`). Measured on the author's
  laptop (Arc 140T, llama.cpp b11541): Gemma 4 E4B Q4_K_M wrote 21 tok/s alone and 29
  (prose), 30 to 36 (new code) and 47 (rewriting code) with its 0.1 GB head. Keep the
  head on the graphics chip: with the head on the processor the same model wrote 3.
- **A model with experts (MoE)** — "26B-A4B" means 26 billion parameters of which 4 are
  read for each token. Memory follows the 26 (every expert has to be loaded), speed
  follows the 4. So such a model is as hard to fit as a 26B one and about as quick as a 4B one.
- **tok/s range** — the scorer's bandwidth model gives a raw number; for models not in its
  catalog the app derives the machine's bytes-per-second from the catalog and applies it
  to the file size. The raw number is then corrected for the kind of GPU and shown as a
  range (`Core/SpeedEstimate.cs`):

  | GPU | Shown range | Basis |
  | --- | --- | --- |
  | shares system memory (Arc 140T, Iris, Radeon 890M…) | 45–65% of raw | measured: raw said ~15 tok/s, a real run gave 7–10 |
  | graphics card with its own memory | 75–100% of raw | not measured, a cautious band |
  | no GPU | 70–100% of raw | not measured, a cautious band |

  These factors are only the starting point. **Measure speed** on one downloaded dense
  model of 0.7 GB or more replaces them for that PC: every other estimate is then scaled
  by what the measured model really did (±15%), and the other versions of a measured model
  are estimated from it directly. So the check is right for any machine after one run,
  not only for the one the factors were measured on.
- **Memory and fit** — one rule, the same one zer0ne code uses (`Core/FitRule.cs` here,
  `engine/src/localCatalog.ts` there; `CoreTests/fit-cases.json` is a set of cases both
  apps' tests must answer the same way). A file needs its own size, the vision projector
  when the model has one, the cache for the conversation and 0.35 GB of engine buffers.
  The conversation length is the longest of 32K, 16K, 8K and 4K that keeps the need within
  85% of the budget. Up to 60% of the budget is **Perfect**, up to 85% **Good**, up to 98%
  **Marginal**; past that **Slower** up to the second limit, then **Too tight**. (The scorer
  sizes a model at the quant it would pick itself, so its number is the same for every
  file of a repo.)
- **Quality** — until a file is tested: the scorer's estimate from model size, family and task
  fit, lowered for heavy compression by a rule of thumb (an estimate). After
  **Test quality**: the share of the 40 questions it answered right on this PC.
- **Which version is recommended** — the largest one that fits with room to spare and
  still writes at reading pace (7 tok/s), unquantised files aside; if none reaches that
  pace, the usual best trade (Q4_K_M first); if none has room to spare, the one that
  needs the least memory. It is one rule shared with zer0ne code (`FitRule.Pick`), which
  asks for 15 tok/s instead, the pace an agent needs.
- **Which versions are listed** — up to nine: the largest quantised one that runs with room to spare,
  the largest that runs inside the budget, one that runs past it (the usual Q4 when it
  is one), the first that does not run, the two smallest, then the usual versions (a
  publisher's own mix such as `UD-Q4_K_M` stands in for `Q4_K_M`).
- **Battle points** — `Points` in `Web/app.js`. Speed gives full points at 20 tok/s and
  60% at reading pace (7 tok/s); memory gives full points at half of the budget or less;
  context gives full points at 32K usable tokens.
- **Parameter count** — the GGUF header as Hugging Face reads it, or the size in the repo
  name; a count worked out from `config.json` is used only when both are missing.
- **Context** — from the repo's `config.json`, capped by what fits after the weights.

## Environment variables (for debugging)

| Variable | Effect |
| --- | --- |
| `LLMFIT_EXE` | path to `llmfit.exe` if you don't want to ship it in `tools\` |
| `HF_TOKEN` | optional Hugging Face token — raises the anonymous per-IP limit when you scan all day |
| `MODELFIT_DIAG=1` | writes diagnostics to `%TEMP%\modelfit-diag.log` |
| `MODELFIT_GPU_NAME` | pretend the PC has this graphics card (e.g. `NVIDIA GeForce RTX 4070`), to see the NVIDIA screens without one |
| `MODELFIT_NVIDIA_DRIVER` | with the line above: the driver number to pretend (e.g. `581.29`) |
| `MODELFIT_SECOND_INSTANCE=1` | lets a second copy start beside one that is open (for working on the app) |
| `MODELFIT_SHARED_DIR` | another folder to read zer0ne code's models from; empty turns the shared library off |
| — | the UI also writes smoke-test state to `%TEMP%\modelfit-debug.log` |

## Layout

```
ModelFitStudio/
├─ Core/                hardware scan, HF API, analyzer, speed range, measurement, downloads, settings
├─ Web/                 index.html + app.js + styles.css (the whole UI), i18n.js + lang/ (languages)
├─ engine/              llama.cpp, shipped with the app
├─ docs/icon-256.png    the app icon
├─ Assets/app.ico       generated by tools/make-icon.py
├─ tools/llmfit.exe     the fit scorer
├─ tools/make-zip.ps1   packages dist\ with real Web/ paths, then verifies them
├─ tools/lang/          the text of every language; tools/make-lang.py builds Web/lang/
├─ THIRD-PARTY-NOTICES.txt  the licence notices of the two programs the app ships
├─ rebuild.bat          one-click publish + zip
└─ dist/                publish output (ModelFitStudio.exe, llmfit.exe, engine/, Web/)
```

## Network behaviour

Hugging Face serves unauthenticated API calls from **one quota per IP**, shared with
anything else on the machine that talks to HF (a browser tab counts). When a scan
fails with `HTTP 429`, it is rate limiting — the app retries with backoff (honouring
HF's `Retry-After`), keeps a **12-hour cache** of every repo and config it fetched in
`%LOCALAPPDATA%\ModelFitStudio\cache`, and falls back to an older cached copy during
an outage, so re-scans of things you already looked at never touch the network.
`HF_TOKEN` is optional and quietly raises that limit.

## Known limits

- The app has no chat and no local API of its own. It runs a file to measure it, and
  **Start it now** hands a file to llama.cpp's own server and chat page.
- The NVIDIA engine has not been run on a real NVIDIA card by the author: the choice of
  build is covered by unit tests and the screens were checked with a pretend card, but the
  download and a model actually loading with CUDA are untested. The built-in engine is
  the fallback either way.
- The app has only been run on one PC (an Intel laptop with shared graphics memory).
- One download at a time on purpose: Hugging Face throttles parallel reads.

## Licence and credits

Models Fit Studio is made by **zer0ne** and released under the MIT licence.

It ships two programs by other people, each under its own MIT licence: llama.cpp (the
engine) and llmfit (the fit scorer). Their notices are in `THIRD-PARTY-NOTICES.txt`, which
must stay in every copy of the app.
