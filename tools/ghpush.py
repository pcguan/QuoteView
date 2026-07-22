#!/usr/bin/env python3
"""Mirror the working tree to GitHub via the HTTP API.

This box's network passes github.com web/API but BLOCKS the git smart-HTTP
transport (git-receive-pack), so `git push` can't work. This uploads the whole
tree through api.github.com (which does work) as one commit on `main`.

Usage:  GITHUB_TOKEN=<pat> python3 tools/ghpush.py "commit message"
"""
import os, sys, json, base64, urllib.request, urllib.error

OWNER, REPO, BRANCH = "pcguan", "QuoteView", "main"
API = "https://api.github.com"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOKEN = os.environ.get("GITHUB_TOKEN") or sys.exit("set GITHUB_TOKEN")

IGNORE_DIRS = {".git", "bin", "obj", "dist", ".vs"}
IGNORE_SUFFIX = (".user", ".suo")
IGNORE_FILES = {"panel.log"}


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Authorization", "token " + TOKEN)
    req.add_header("User-Agent", "ghpush")
    req.add_header("Accept", "application/vnd.github+json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read() or "null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or "null")


def collect():
    out = []
    for dp, dns, fns in os.walk(ROOT):
        dns[:] = [d for d in dns if d not in IGNORE_DIRS]
        for fn in fns:
            if fn in IGNORE_FILES or fn.endswith(IGNORE_SUFFIX):
                continue
            full = os.path.join(dp, fn)
            out.append((os.path.relpath(full, ROOT).replace(os.sep, "/"), full))
    return sorted(out)


def b64(path):
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode()


def head_parents():
    st, ref = api("GET", f"/repos/{OWNER}/{REPO}/git/ref/heads/{BRANCH}")
    if st == 200:
        return [ref["object"]["sha"]]
    return []


def main():
    msg = sys.argv[1] if len(sys.argv) > 1 else "update"
    files = collect()

    parents = head_parents()
    if not parents:
        # Empty repo: the git-data API refuses blobs until a first commit exists,
        # so seed one file through the Contents API, which initializes `main`.
        rel, full = files[0]
        st, rb = api("PUT", f"/repos/{OWNER}/{REPO}/contents/{rel}",
                     {"message": "init", "content": b64(full), "branch": BRANCH})
        assert st in (200, 201), (st, rb)
        parents = head_parents()

    tree = []
    for rel, full in files:
        st, rb = api("POST", f"/repos/{OWNER}/{REPO}/git/blobs",
                     {"content": b64(full), "encoding": "base64"})
        assert st in (200, 201), (rel, st, rb)
        tree.append({"path": rel, "mode": "100644", "type": "blob", "sha": rb["sha"]})

    # No base_tree: the listed blobs ARE the whole tree, so removals propagate too.
    st, tr = api("POST", f"/repos/{OWNER}/{REPO}/git/trees", {"tree": tree})
    assert st in (200, 201), (st, tr)

    st, cm = api("POST", f"/repos/{OWNER}/{REPO}/git/commits",
                 {"message": msg, "tree": tr["sha"], "parents": parents})
    assert st in (200, 201), (st, cm)

    st, up = api("PATCH", f"/repos/{OWNER}/{REPO}/git/refs/heads/{BRANCH}",
                 {"sha": cm["sha"], "force": True})
    assert st in (200, 201), (st, up)

    print(f"pushed {len(files)} files -> {cm['sha'][:8]} on {OWNER}/{REPO}@{BRANCH}")


if __name__ == "__main__":
    main()
