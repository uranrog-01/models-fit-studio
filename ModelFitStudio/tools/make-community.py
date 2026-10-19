"""Builds Web/community.json: the speeds llmfit users measured and shared.

The source is the llmfit repository's community folder (MIT licence), one file per
submission. The app only needs a flat list, so this keeps the hardware name, the
model, and the measured numbers.

usage: python tools/make-community.py [path-to-llmfit-repo]
       (default: ../llmfit next to the ModelFitStudio folder)
"""
import glob
import json
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
app = os.path.dirname(here)
repo = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(app), "llmfit")
src = os.path.join(repo, "llmfit-core", "data", "community")
if not os.path.isdir(src):
    sys.exit(f"no community folder at {src}")

rows = []
for path in sorted(glob.glob(os.path.join(src, "*", "*.json"))):
    try:
        doc = json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        continue
    hw = doc.get("hardware") or {}
    for r in doc.get("results") or []:
        tps = r.get("avgTps")
        if not r.get("model") or not isinstance(tps, (int, float)) or tps <= 0:
            continue
        model = r["model"]
        # some submissions carry the contributor's whole path: only the file name is wanted
        if model.lower().endswith(".gguf"):
            model = model.replace("\\", "/").split("/")[-1]
        rows.append({
            "hw": hw.get("hardwareName") or os.path.basename(os.path.dirname(path)),
            "cpu": hw.get("cpu") or "",
            "model": model,
            "provider": r.get("provider") or "",
            "tps": round(tps, 1),
            "ttftMs": round(r["avgTtftMs"]) if isinstance(r.get("avgTtftMs"), (int, float)) else 0,
        })

out = os.path.join(app, "Web", "community.json")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump({"source": "llmfit community benchmarks (MIT)", "rows": rows}, f, separators=(",", ":"))
print(f"{len(rows)} measured speeds from {len({r['hw'] for r in rows})} kinds of hardware -> {out}")
