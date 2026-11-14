"""Builds community/index.json from community/entries/<kind>/*.json (one approved upload each).

Entries already in index.json that have no entry file (added by hand) are kept while their file exists; deleting an
upload's .json takes it out of the gallery. Each list is newest first.
"""
import hashlib
import json
import os
import re
import sys

ROOT = "community"
KINDS = ["layouts", "gamepages", "looks", "widgets"]
SAFE = re.compile(r"^[A-Za-z0-9._/-]+$")


def safe_path(p):
    return isinstance(p, str) and p != "" and SAFE.match(p) and ".." not in p and not p.startswith("/")


def main():
    index_path = os.path.join(ROOT, "index.json")
    index = {"version": 2}
    if os.path.exists(index_path):
        with open(index_path, encoding="utf-8-sig") as f:
            try:
                index = json.load(f)
            except ValueError:
                print("index.json couldn't be read; starting a new one", file=sys.stderr)
    index["version"] = 2
    for kind in KINDS:
        # entries already listed stay while their file is still here (deleting an upload's file takes it out of the gallery)
        items = {e.get("id"): e for e in index.get(kind, [])
                 if isinstance(e, dict) and e.get("id") and safe_path(e.get("file")) and os.path.exists(os.path.join(ROOT, e["file"]))}
        folder = os.path.join(ROOT, "entries", kind)
        if os.path.isdir(folder):
            for name in sorted(os.listdir(folder)):
                if not name.endswith(".json"):
                    continue
                try:
                    with open(os.path.join(folder, name), encoding="utf-8-sig") as f:
                        e = json.load(f)
                except ValueError as ex:
                    print(f"skipped {kind}/{name}: {ex}", file=sys.stderr)
                    continue
                if not isinstance(e, dict) or not e.get("id") or not e.get("name"):
                    print(f"skipped {kind}/{name}: no id or name", file=sys.stderr)
                    continue
                if not safe_path(e.get("file")) or not os.path.exists(os.path.join(ROOT, e["file"])):
                    print(f"skipped {kind}/{name}: its file is missing", file=sys.stderr)
                    continue
                if e.get("preview") and (not safe_path(e["preview"]) or not os.path.exists(os.path.join(ROOT, e["preview"]))):
                    e["preview"] = ""
                # every picture (Kippix shows them one after another); the first is "preview"
                pics = [p for p in (e.get("previews") or []) if safe_path(p) and os.path.exists(os.path.join(ROOT, p))][:8]
                if pics:
                    e["previews"] = pics
                    e["preview"] = pics[0]
                else:
                    e.pop("previews", None)
                if not isinstance(e.get("tags"), list):
                    e["tags"] = []
                items[e["id"]] = e
        # its file's copy in the "community" release, named by its contents: GitHub counts downloads of release files
        for e in items.values():
            with open(os.path.join(ROOT, e["file"]), "rb") as f:
                e["asset"] = f"{kind}--{e['id']}--{hashlib.sha256(f.read()).hexdigest()[:10]}.json"
        index[kind] = sorted(items.values(), key=lambda e: str(e.get("added", "")), reverse=True)
    with open(index_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(index, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("index.json: " + ", ".join(f"{k} {len(index[k])}" for k in KINDS))


if __name__ == "__main__":
    main()
