#!/usr/bin/env bash
set -euo pipefail

# Wrap a `dotnet publish` output directory into a macOS .app bundle.
# Usage: make-app.sh <publish-dir> <version> <output-app-path>

PUBLISH_DIR="$1"
VERSION="$2"
APP="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
EXE="MissionPlannerAvalonia"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$EXE"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  # Real Developer ID: inside-out hardened-runtime signing (required for notarization).
  # Every nested Mach-O is signed first, then the main executable carries the
  # entitlements, then the bundle is sealed. --timestamp = secure timestamp.
  ENT="$HERE/entitlements.plist"
  while IFS= read -r -d '' f; do
    if file -b "$f" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" "$f"
    fi
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  codesign --force --timestamp --options runtime --entitlements "$ENT" \
    --sign "$SIGN_IDENTITY" "$APP/Contents/MacOS/$EXE"
  codesign --force --timestamp --options runtime --entitlements "$ENT" \
    --sign "$SIGN_IDENTITY" "$APP"
  codesign --verify --strict --verbose=2 "$APP"
elif command -v codesign >/dev/null 2>&1; then
  # Ad-hoc sign the whole bundle (preview / forks / local, no Developer ID). The apphost
  # ships with a standalone signature that is malformed for a bundle; strip it, then --deep
  # ad-hoc sign so Info.plist is bound and _CodeSignature is sealed (else Gatekeeper: "damaged").
  codesign --remove-signature "$APP/Contents/MacOS/$EXE" 2>/dev/null || true
  codesign --force --deep --sign - --identifier com.semaaviation.missionplanneravalonia "$APP"
  codesign --verify --strict --verbose=2 "$APP"
fi

echo "Built $APP"
