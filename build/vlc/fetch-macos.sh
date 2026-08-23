#!/usr/bin/env bash
set -euo pipefail

# Build a relocatable LibVLC runtime from the exact official VideoLAN macOS application image.
# VideoLAN's old Mac NuGet contains only an Intel dylib; the current signed DMGs provide matching
# Intel and Apple-Silicon libvlc, libvlccore and the complete plugin set. We retain the original
# lib/plugins/share sibling layout so the dylib rpaths and VLC plugin discovery remain valid.

if [[ $# -ne 2 ]]; then
  echo "usage: fetch-macos.sh <osx-x64|osx-arm64> <destination>" >&2
  exit 2
fi

RID="$1"
DESTINATION="$2"
VERSION="3.0.23"

case "$RID" in
  osx-x64)
    ASSET="vlc-$VERSION-intel64.dmg"
    ARCHIVE_SIZE="57544217"
    ARCHIVE_SHA256="ec01530ce69d849dd057fba8876e68ac39bf279dc28de4e9c04e4aec11fc98db"
    LIBVLC_SHA256="e85fe5f2522d8b114d3a51d14346be2ed1facd188fbe5445bc82f4f455a289f8"
    CORE_SHA256="707af2f99cb8f0516216c5d04257c7fb999e8aa08230ed74cb498dc0693ad10b"
    PLUGIN_CACHE_SHA256="767690eca59afa68730ef7eb31358fecc52438e4651e3386b7d84ea94212e7e9"
    FILE_ARCH="x86_64"
    EXPECTED_FILES="444"
    EXPECTED_PLUGIN_DYLIBS="343"
    EXPECTED_BYTES="102768349"
    ;;
  osx-arm64)
    ASSET="vlc-$VERSION-arm64.dmg"
    ARCHIVE_SIZE="51273389"
    ARCHIVE_SHA256="fc6fac08d87f538517d44aca0c5e7a244b67c8c4cb589bf478363a7315fd5e0d"
    LIBVLC_SHA256="4157cfffc9994f3cd0be73a307384681a39040e076e7667a3a87c778eec44a82"
    CORE_SHA256="739dd7e5622951239669199349e8ece74b07d618e7320cc9b11a1d40ac903183"
    PLUGIN_CACHE_SHA256="176ec9aff8a22354f0edee941f08809916cca08a5cabf467c1272989a2e9f214"
    FILE_ARCH="arm64"
    EXPECTED_FILES="438"
    EXPECTED_PLUGIN_DYLIBS="337"
    EXPECTED_BYTES="86442957"
    ;;
  *)
    echo "unsupported VLC runtime identifier: $RID" >&2
    exit 2
    ;;
esac

if [[ -z "$DESTINATION" || "$DESTINATION" == "/" || "$DESTINATION" == "." \
    || "$DESTINATION" == ".." ]]; then
  echo "refusing unsafe VLC destination" >&2
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

runtime_file_count() {
  find "$1" -type f ! -name '.missionplanner-vlc-*' | wc -l | tr -d ' '
}

runtime_plugin_count() {
  find "$1/plugins" -type f -name '*.dylib' | wc -l | tr -d ' '
}

runtime_byte_count() {
  local total=0
  local size
  while IFS= read -r file; do
    size="$(wc -c < "$file" | tr -d ' ')"
    total=$((total + size))
  done < <(find "$1" -type f ! -name '.missionplanner-vlc-*' | LC_ALL=C sort)
  printf '%s\n' "$total"
}

verify_manifest() {
  local root="$1"
  local manifest="$root/.missionplanner-vlc-sha256"
  local line hash relative
  [[ -f "$manifest" ]] || return 1
  while IFS= read -r line; do
    hash="${line%%  *}"
    relative="${line#*  }"
    [[ "$relative" == ./* && -f "$root/${relative#./}" ]] || return 1
    [[ "$(sha256_file "$root/${relative#./}")" == "$hash" ]] || return 1
  done < "$manifest"
}

validate_runtime() {
  local root="$1"
  [[ -d "$root/lib" && -d "$root/plugins" && -d "$root/share/lua" ]] || return 1
  [[ -L "$root/lib/libvlc.dylib" && "$(readlink "$root/lib/libvlc.dylib")" == "libvlc.5.dylib" ]] || return 1
  [[ -L "$root/lib/libvlccore.dylib" && "$(readlink "$root/lib/libvlccore.dylib")" == "libvlccore.9.dylib" ]] || return 1
  [[ "$(sha256_file "$root/lib/libvlc.5.dylib")" == "$LIBVLC_SHA256" ]] || return 1
  [[ "$(sha256_file "$root/lib/libvlccore.9.dylib")" == "$CORE_SHA256" ]] || return 1
  [[ "$(sha256_file "$root/plugins/plugins.dat")" == "$PLUGIN_CACHE_SHA256" ]] || return 1
  [[ "$(runtime_file_count "$root")" == "$EXPECTED_FILES" ]] || return 1
  [[ "$(runtime_plugin_count "$root")" == "$EXPECTED_PLUGIN_DYLIBS" ]] || return 1
  [[ "$(runtime_byte_count "$root")" == "$EXPECTED_BYTES" ]] || return 1
  [[ "$(cat "$root/.missionplanner-vlc-source" 2>/dev/null)" == "$VERSION $RID $ARCHIVE_SHA256" ]] || return 1
  verify_manifest "$root"
}

if validate_runtime "$DESTINATION"; then
  exit 0
fi

TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/missionplanner-vlc.XXXXXX")"
MOUNTED=""
MOUNT_POINT="$TEMP_ROOT/mount"
OLD_RUNTIME=""
cleanup() {
  if [[ -n "$MOUNTED" ]]; then
    hdiutil detach "$MOUNT_POINT" -quiet || true
  fi
  if [[ -n "$OLD_RUNTIME" && ( -e "$OLD_RUNTIME" || -L "$OLD_RUNTIME" ) ]]; then
    if [[ ! -e "$DESTINATION" && ! -L "$DESTINATION" ]]; then
      mv "$OLD_RUNTIME" "$DESTINATION" || true
    else
      rm -rf "$OLD_RUNTIME"
    fi
  fi
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT

ARCHIVE="$TEMP_ROOT/$ASSET"
EXTRACTED="$TEMP_ROOT/extracted"
URL="https://download.videolan.org/pub/videolan/vlc/$VERSION/macosx/$ASSET"

curl --proto '=https' --tlsv1.2 --fail --location --retry 3 --retry-all-errors \
  --connect-timeout 15 --max-time 180 \
  --output "$ARCHIVE" "$URL"

ACTUAL_SIZE="$(wc -c < "$ARCHIVE" | tr -d ' ')"
if [[ "$ACTUAL_SIZE" != "$ARCHIVE_SIZE" ]]; then
  echo "VLC archive size mismatch: expected $ARCHIVE_SIZE, got $ACTUAL_SIZE" >&2
  exit 1
fi
if [[ "$(sha256_file "$ARCHIVE")" != "$ARCHIVE_SHA256" ]]; then
  echo "VLC archive SHA-256 mismatch" >&2
  exit 1
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
  mkdir -p "$MOUNT_POINT"
  hdiutil attach -readonly -nobrowse -mountpoint "$MOUNT_POINT" "$ARCHIVE" >/dev/null
  MOUNTED="1"
  SOURCE_ROOT="$MOUNT_POINT/VLC.app/Contents/MacOS"
else
  if ! command -v 7z >/dev/null 2>&1; then
    echo "7z is required to extract the official VLC DMG outside macOS" >&2
    exit 1
  fi
  mkdir -p "$EXTRACTED"
  7z x -y -o"$EXTRACTED" "$ARCHIVE" \
    'VLC media player/VLC.app/Contents/MacOS/lib/*' \
    'VLC media player/VLC.app/Contents/MacOS/plugins/*' \
    'VLC media player/VLC.app/Contents/MacOS/share/lua/*' \
    'VLC media player/VLC.app/Contents/MacOS/share/hrtfs/*' >/dev/null
  SOURCE_ROOT="$EXTRACTED/VLC media player/VLC.app/Contents/MacOS"
fi

DESTINATION_PARENT="$(dirname "$DESTINATION")"
mkdir -p "$DESTINATION_PARENT"
STAGING="$(mktemp -d "$DESTINATION_PARENT/.missionplanner-vlc-$RID.XXXXXX")"
mkdir -p "$STAGING/lib" "$STAGING/plugins" "$STAGING/share/lua" "$STAGING/share/hrtfs"
# plugins.dat records each dylib's size and modification time. Preserve both timestamps and
# symlinks so VLC can use its signed upstream cache without treating every plugin as stale.
cp -RpP "$SOURCE_ROOT/lib/." "$STAGING/lib/"
cp -RpP "$SOURCE_ROOT/plugins/." "$STAGING/plugins/"
cp -RpP "$SOURCE_ROOT/share/lua/." "$STAGING/share/lua/"
cp -RpP "$SOURCE_ROOT/share/hrtfs/." "$STAGING/share/hrtfs/"

# 7-Zip exposes HFS+ code-signature extended attributes as colon-named sidecar files. They are not
# runtime content; the enclosing Mission Planner app is signed after these dylibs are assembled.
find "$STAGING" -type f -name '*:*' -delete

while IFS= read -r dylib; do
  if ! file "$dylib" | grep -q "$FILE_ARCH"; then
    echo "VLC runtime contains a non-$FILE_ARCH dylib: $dylib" >&2
    exit 1
  fi
done < <(find "$STAGING/lib" "$STAGING/plugins" -type f -name '*.dylib' | LC_ALL=C sort)

printf '%s %s %s\n' "$VERSION" "$RID" "$ARCHIVE_SHA256" > "$STAGING/.missionplanner-vlc-source"
while IFS= read -r file; do
  relative="./${file#"$STAGING/"}"
  printf '%s  %s\n' "$(sha256_file "$file")" "$relative"
done < <(find "$STAGING" -type f ! -name '.missionplanner-vlc-*' | LC_ALL=C sort) \
  > "$STAGING/.missionplanner-vlc-sha256"

if ! validate_runtime "$STAGING"; then
  echo "extracted VLC runtime validation failed" >&2
  exit 1
fi

if [[ -e "$DESTINATION" || -L "$DESTINATION" ]]; then
  OLD_RUNTIME="$(mktemp -d "$DESTINATION_PARENT/.missionplanner-vlc-$RID.old.XXXXXX")"
  rmdir "$OLD_RUNTIME"
  mv "$DESTINATION" "$OLD_RUNTIME"
fi
mv "$STAGING" "$DESTINATION"
if [[ -n "$OLD_RUNTIME" ]]; then
  rm -rf "$OLD_RUNTIME"
  OLD_RUNTIME=""
fi
