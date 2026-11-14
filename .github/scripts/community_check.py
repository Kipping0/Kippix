"""Checks a Community upload (a pull request) before it's approved. Fails with a list of problems; prints what it found.

Usage: community_check.py <base ref> [<pull request author>]   (e.g. origin/main someone)

An upload may add new files, and may only change files that are the author's own (Kippix names every upload
<name>-<their GitHub name>, so their own files end with "-<their name>"). Changing or deleting anyone else's fails.
"""
import json
import os
import re
import subprocess
import sys

SAFE = re.compile(r"^community/(layouts|gamepages|looks|widgets|previews|previews/gamepages|previews/looks|previews/widgets|entries/(layouts|gamepages|looks|widgets))/[A-Za-z0-9._-]+\.(json|jpg)$")
MAX_JSON = 8 * 1024 * 1024
MAX_JPG = 3 * 1024 * 1024


def slug(s):
    out = "".join(ch if ch.isascii() and ch.isalnum() else "-" for ch in (s or "").lower()).strip("-")
    while "--" in out:
        out = out.replace("--", "-")
    return out


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else "origin/main"
    author = slug(sys.argv[2]) if len(sys.argv) > 2 else ""
    out = subprocess.run(["git", "diff", "--name-status", base + "...HEAD"], capture_output=True, text=True, check=True).stdout
    problems, files = [], []
    for line in out.splitlines():
        parts = line.split("\t")
        status, path = parts[0], parts[-1]
        files.append(path)
        if status.startswith("D"):
            problems.append(f"{path}: deletes a file (uploads only add or update)")
            continue
        if not SAFE.match(path):
            problems.append(f"{path}: not a Community gallery file (only community/<kind>/, previews/ and entries/ are allowed)")
            continue
        if not status.startswith("A"):
            stem = os.path.splitext(os.path.basename(path))[0]
            if not author or not stem.endswith("-" + author):
                problems.append(f"{path}: changes a file that's already in the gallery and isn't this person's own")
                continue
        size = os.path.getsize(path)
        if path.endswith(".json"):
            if size > MAX_JSON:
                problems.append(f"{path}: too big ({size // 1024} KB)")
                continue
            try:
                with open(path, encoding="utf-8-sig") as f:
                    data = json.load(f)
            except ValueError as ex:
                problems.append(f"{path}: not valid JSON ({ex})")
                continue
            if "/entries/" in path:
                for key in ("id", "name", "file"):
                    if not data.get(key):
                        problems.append(f"{path}: the entry has no {key}")
                pics = data.get("previews") or []
                if not isinstance(pics, list) or len(pics) > 8:
                    problems.append(f"{path}: previews should be a list of up to 8 pictures")
                    pics = []
                for key, p in [("file", data.get("file") or ""), ("preview", data.get("preview") or "")] + [("picture", x) for x in pics if isinstance(x, str)]:
                    if p and (".." in p or p.startswith("/") or not re.match(r"^[A-Za-z0-9._/-]+$", p)):
                        problems.append(f"{path}: unsafe {key} path {p!r}")
                    elif p and not os.path.exists(os.path.join("community", p)):
                        problems.append(f"{path}: its {key} community/{p} isn't in the upload")
        else:
            with open(path, "rb") as f:
                head = f.read(3)
            if head != b"\xff\xd8\xff":
                problems.append(f"{path}: not a JPEG picture")
            if size > MAX_JPG:
                problems.append(f"{path}: picture too big ({size // 1024} KB)")
    print("Files in this upload:")
    for p in files:
        print("  " + p)
    if problems:
        print("\nProblems:")
        for p in problems:
            print("  - " + p)
        sys.exit(1)
    print("\nLooks fine: only Community gallery files, readable, sensible sizes.")


if __name__ == "__main__":
    main()
