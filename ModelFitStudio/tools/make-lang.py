"""Builds the app's language files (Web/lang/<code>.json) from the plain text in tools/lang.

    tools/lang/en.txt          every English entry, one per line, in the order of the app.
                               {0}, {1}… stand for whatever the app puts there.
    tools/lang/languages.txt   one line per language: its code, a space, its own name.
    tools/lang/strings-*.txt   the translations, entry by entry:

                                   > Download this model
                                   de Dieses Modell herunterladen
                                   fr Télécharger ce modèle

                               A language that has no line for an entry keeps the English.

Run it after editing any of them:   python tools/make-lang.py
It stops, naming the entry, when a translation has lost one of its {0} places or
belongs to no entry of en.txt. The app shows English for whatever a language lacks.
"""
import glob
import json
import os
import re

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, "lang")
out = os.path.join(here, "..", "Web", "lang")


def lines(path):
    return [l for l in open(path, encoding="utf-8").read().split("\n") if l.strip()]


keys = lines(os.path.join(src, "en.txt"))
doubles = sorted({k for k in keys if keys.count(k) > 1})
assert not doubles, "en.txt has the same entry twice: " + " | ".join(doubles)
languages = dict(l.split(" ", 1) for l in lines(os.path.join(src, "languages.txt")))

places = lambda s: sorted(re.findall(r"\{\d\}", s))
table = {code: {} for code in languages}
seen = set()
for path in sorted(glob.glob(os.path.join(src, "strings-*.txt"))):
    key = None
    for n, line in enumerate(lines(path), 1):
        where = f"{os.path.basename(path)}:{n}"
        if line.startswith("> "):
            key = line[2:]
            assert key in keys, f"{where}: not an entry of en.txt: '{key}'"
            assert key not in seen, f"{where}: entry given twice: '{key}'"
            seen.add(key)
            continue
        code, _, text = line.partition(" ")
        assert key and code in languages and text, f"{where}: expected '<code> <text>' under an entry, found '{line[:50]}'"
        assert places(text) == places(key), f"{where}: '{code}' must keep {places(key)} of '{key}'"
        assert key not in table[code], f"{where}: '{code}' given twice for '{key}'"
        table[code][key] = text

missing = [k for k in keys if k not in seen]
assert not missing, f"{len(missing)} entries of en.txt have no block, the first: '{missing[0]}'"

os.makedirs(out, exist_ok=True)
total = 0
for code, name in languages.items():
    done = table[code]
    # every entry is written: one a language leaves out keeps the English, and still takes part in longer lines
    strings = {k: done.get(k, k) for k in keys}
    path = os.path.join(out, code + ".json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"name": name, "strings": strings}, f, ensure_ascii=False, separators=(",", ":"))
    total += os.path.getsize(path)
    print(f"{code}  {len(done)} of {len(keys)} translated  {os.path.getsize(path) / 1024:.0f} KB")
print(f"all languages: {total / 1024:.0f} KB")
