#!/usr/bin/env python3
"""Emit manifest.json for the auto-updater.

Usage:
  gen-manifest.py <publish_dir> <version> <notes_url> <out.json>
      Loose-file mode (Windows/Linux): every file under <publish_dir> with its SHA-256 + size.
  gen-manifest.py <publish_dir> <version> <notes_url> <out.json> \
      --bundle-url URL --bundle-sha256 HEX --bundle-size N
      Full-bundle mode (macOS): manifest carries a single signed+notarized package the client
      swaps whole; files[] is emitted empty so per-file diffing is skipped. Overwriting loose
      files inside a notarized .app breaks its seal/staple, so mac must replace the whole bundle.

Paths are stored relative to <publish_dir> with forward slashes. The client verifies the Ed25519
signature over the exact bytes of the emitted file, so sign this file as-is (no re-formatting).
"""
import argparse
import hashlib
import json
import os


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("publish_dir")
    ap.add_argument("version")
    ap.add_argument("notes")
    ap.add_argument("out")
    ap.add_argument("--bundle-url")
    ap.add_argument("--bundle-sha256")
    ap.add_argument("--bundle-size", type=int)
    a = ap.parse_args()

    manifest = {"version": a.version, "notes": a.notes}

    if a.bundle_url:
        if not (a.bundle_sha256 and a.bundle_size):
            ap.error("--bundle-url requires --bundle-sha256 and --bundle-size")
        manifest["bundle"] = {"url": a.bundle_url, "sha256": a.bundle_sha256, "size": a.bundle_size}
        manifest["files"] = []
        summary = f"bundle {a.bundle_sha256[:12]}…"
    else:
        files = []
        for root, _, names in os.walk(a.publish_dir):
            for name in names:
                full = os.path.join(root, name)
                rel = os.path.relpath(full, a.publish_dir).replace(os.sep, "/")
                files.append({"path": rel, "sha256": sha256(full), "size": os.path.getsize(full)})
        files.sort(key=lambda f: f["path"])
        manifest["files"] = files
        summary = f"{len(files)} files"

    with open(a.out, "w") as f:
        json.dump(manifest, f, indent=2)
    print(f"manifest: {summary} -> {a.out}")


if __name__ == "__main__":
    main()
