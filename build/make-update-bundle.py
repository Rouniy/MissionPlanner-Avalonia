#!/usr/bin/env python3
"""Create a deterministic root-relative ZIP for the signed portable updater."""

import argparse
import os
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("publish_dir")
    parser.add_argument("output_zip")
    args = parser.parse_args()

    publish_dir = os.path.abspath(args.publish_dir)
    output_zip = os.path.abspath(args.output_zip)
    os.makedirs(os.path.dirname(output_zip), exist_ok=True)

    files = []
    for root, _, names in os.walk(publish_dir):
        for name in names:
            path = os.path.join(root, name)
            files.append((os.path.relpath(path, publish_dir).replace(os.sep, "/"), path))

    with zipfile.ZipFile(
        output_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6
    ) as archive:
        for relative, path in sorted(files):
            archive.write(path, relative)

    print(f"update bundle: {len(files)} files -> {output_zip}")


if __name__ == "__main__":
    main()
