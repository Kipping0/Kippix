"""Puts every gallery file into the repository's "community" release, so GitHub counts how often each is downloaded.

Kippix downloads from there (and shows the counts in the gallery). The release is a pre-release, so it's never mistaken for a
Kippix version ("latest" stays the newest Kippix release). Files are only added, never replaced: a new version of an upload
gets a new name, and every version's downloads are added up.
Needs GH_TOKEN (the workflow's own token).
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = "community"
KINDS = ["layouts", "gamepages", "looks", "widgets"]
TAG = "community"


def gh(*args, check=True):
    return subprocess.run(["gh", *args], capture_output=True, text=True, check=check)


def main():
    with open(os.path.join(ROOT, "index.json"), encoding="utf-8-sig") as f:
        index = json.load(f)
    if gh("release", "view", TAG, check=False).returncode != 0:
        gh("release", "create", TAG, "--prerelease", "--latest=false", "--title", "Community gallery files",
           "--notes", "The Community gallery's files, kept here so downloads from Kippix are counted. This isn't a Kippix version: "
                      "get Kippix from the latest release.")
    view = json.loads(gh("release", "view", TAG, "--json", "assets").stdout or "{}")
    have = {a["name"] for a in view.get("assets", [])}
    tmp = tempfile.mkdtemp()
    added = 0
    for kind in KINDS:
        for e in index.get(kind, []):
            name, path = e.get("asset"), os.path.join(ROOT, e.get("file", ""))
            if not name or name in have or not os.path.isfile(path):
                continue
            dest = os.path.join(tmp, name)
            shutil.copyfile(path, dest)
            gh("release", "upload", TAG, dest)
            added += 1
            print("added " + name)
    print(f"{added} new file(s) in the {TAG} release")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as ex:
        print(ex.stderr or ex, file=sys.stderr)
        sys.exit(1)
