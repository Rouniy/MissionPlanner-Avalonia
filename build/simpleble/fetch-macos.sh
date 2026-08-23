#!/usr/bin/env bash
set -euo pipefail

# Fetch the exact SimpleBLE ABI used by official Mission Planner without checking generated
# Mach-O binaries into this repository. Only the two runtime dylibs are extracted; the archive and
# each extracted file are independently pinned so a replaced release asset fails the build.

if [[ $# -ne 2 ]]; then
  echo "usage: fetch-macos.sh <osx-x64|osx-arm64> <destination>" >&2
  exit 2
fi

RID="$1"
DESTINATION="$2"
VERSION="0.7.3"

case "$RID" in
  osx-x64)
    ASSET="simpleble_shared_macos-x86_64.zip"
    ARCHIVE_SIZE="482496"
    ARCHIVE_SHA256="657a9a971a74e509263e8fea1dadc645f3ef82f617d78c9abdaa3fe7500d9253"
    C_SHA256="a728cfa352c1a2f4aa51b85f859cf737abd0b405a56b513aa81622461302b22e"
    CORE_SHA256="86baa174fd3d39af5db8ffd55a875af08d1f539feb5c2ac3e469f6be83a73026"
    ;;
  osx-arm64)
    ASSET="simpleble_shared_macos-arm64.zip"
    ARCHIVE_SIZE="446013"
    ARCHIVE_SHA256="2de649389c6ad0e19f0f111e9515acdbf46bf627cec920d3e1a1afd23e9eede4"
    C_SHA256="0f361702db20bc43b68f3eab2b3c3d659980b72b0a7543f8afd12d5b3dc2bf5d"
    CORE_SHA256="81c38a3ce28a7cb224a374f57dceea9c2ca7346e84bdbc3d7d56162828c0363b"
    ;;
  *)
    echo "unsupported SimpleBLE runtime identifier: $RID" >&2
    exit 2
    ;;
esac

if [[ -z "$DESTINATION" || "$DESTINATION" == "/" ]]; then
  echo "refusing unsafe SimpleBLE destination" >&2
  exit 2
fi

sha256_file() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    echo "a SHA-256 utility (shasum or sha256sum) is required" >&2
    return 1
  fi
}

C_LIBRARY="$DESTINATION/libsimpleble-c.dylib"
CORE_LIBRARY="$DESTINATION/libsimpleble.0.dylib"
if [[ -f "$C_LIBRARY" && -f "$CORE_LIBRARY" ]] &&
   [[ "$(sha256_file "$C_LIBRARY")" == "$C_SHA256" ]] &&
   [[ "$(sha256_file "$CORE_LIBRARY")" == "$CORE_SHA256" ]]; then
  exit 0
fi

TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/missionplanner-simpleble.XXXXXX")"
trap 'rm -rf "$TEMP_ROOT"' EXIT
ARCHIVE="$TEMP_ROOT/$ASSET"
EXTRACTED="$TEMP_ROOT/extracted"
URL="https://github.com/simpleble/simpleble/releases/download/v$VERSION/$ASSET"

curl --proto '=https' --tlsv1.2 --fail --location --retry 3 --retry-all-errors \
  --connect-timeout 15 --max-time 120 \
  --output "$ARCHIVE" "$URL"

ACTUAL_SIZE="$(wc -c < "$ARCHIVE" | tr -d ' ')"
if [[ "$ACTUAL_SIZE" != "$ARCHIVE_SIZE" ]]; then
  echo "SimpleBLE archive size mismatch: expected $ARCHIVE_SIZE, got $ACTUAL_SIZE" >&2
  exit 1
fi
if [[ "$(sha256_file "$ARCHIVE")" != "$ARCHIVE_SHA256" ]]; then
  echo "SimpleBLE archive SHA-256 mismatch" >&2
  exit 1
fi

mkdir -p "$EXTRACTED"
unzip -q -j "$ARCHIVE" \
  '*/lib/libsimpleble-c.dylib' \
  '*/lib/libsimpleble.0.dylib' \
  -d "$EXTRACTED"

if [[ "$(sha256_file "$EXTRACTED/libsimpleble-c.dylib")" != "$C_SHA256" ]] ||
   [[ "$(sha256_file "$EXTRACTED/libsimpleble.0.dylib")" != "$CORE_SHA256" ]]; then
  echo "extracted SimpleBLE dylib SHA-256 mismatch" >&2
  exit 1
fi

mkdir -p "$DESTINATION"
install -m 0644 "$EXTRACTED/libsimpleble-c.dylib" "$C_LIBRARY.tmp.$$"
install -m 0644 "$EXTRACTED/libsimpleble.0.dylib" "$CORE_LIBRARY.tmp.$$"
mv -f "$C_LIBRARY.tmp.$$" "$C_LIBRARY"
mv -f "$CORE_LIBRARY.tmp.$$" "$CORE_LIBRARY"
