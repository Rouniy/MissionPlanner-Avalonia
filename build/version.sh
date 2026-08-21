#!/usr/bin/env bash

# Resolve one version contract for assemblies, UI, release archives and native packages.
# This file is intended to be sourced by another Bash script. When executed directly it
# prints the resolved values as tab-separated fields.

_mp_version_script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_mp_version_root="$(cd "$_mp_version_script_dir/.." && pwd)"
_mp_upstream_info="$_mp_version_root/external/MissionPlanner/Properties/AssemblyInfo.cs"

if [[ ! -r "$_mp_upstream_info" ]]; then
  echo "MissionPlanner version source is unavailable: $_mp_upstream_info" >&2
  return 2 2>/dev/null || exit 2
fi

MP_UPSTREAM_VERSION="${MISSIONPLANNER_UPSTREAM_VERSION:-$(
  sed -nE 's/.*AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\).*/\1/p' \
    "$_mp_upstream_info" | head -1
)}"
MP_BUILD_DATE="${MISSIONPLANNER_BUILD_DATE:-$(date -u +%Y%m%d)}"
MP_COMMIT="${MISSIONPLANNER_COMMIT:-$(git -C "$_mp_version_root" rev-parse --short=7 HEAD)}"

if [[ -n "${MISSIONPLANNER_DIRTY_SUFFIX+x}" ]]; then
  MP_DIRTY_SUFFIX="$MISSIONPLANNER_DIRTY_SUFFIX"
elif [[ -n "$(git -C "$_mp_version_root" status --porcelain --untracked-files=normal -- \
    . ':!out')" ]]; then
  MP_DIRTY_SUFFIX=".dirty"
else
  MP_DIRTY_SUFFIX=""
fi

if [[ ! "$MP_UPSTREAM_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Official MissionPlanner version must be numeric x.y.z: '$MP_UPSTREAM_VERSION'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ ! "$MP_BUILD_DATE" =~ ^[0-9]{8}$ ]]; then
  echo "Build date must use UTC yyyyMMdd: '$MP_BUILD_DATE'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ ! "$MP_COMMIT" =~ ^[0-9A-Fa-f]{7,40}$ ]]; then
  echo "Port commit must be a 7-40 character Git hash: '$MP_COMMIT'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ "$MP_DIRTY_SUFFIX" != "" && "$MP_DIRTY_SUFFIX" != ".dirty" ]]; then
  echo "Dirty suffix must be empty or '.dirty': '$MP_DIRTY_SUFFIX'" >&2
  return 2 2>/dev/null || exit 2
fi

MP_COMMIT="$(printf '%s' "$MP_COMMIT" | tr '[:upper:]' '[:lower:]')"
if [[ "$(git -C "$_mp_version_root" rev-parse --is-shallow-repository)" == "true" ]]; then
  _mp_default_revision="$(git -C "$_mp_version_root" show -s --format=%ct HEAD)"
else
  _mp_default_revision="$(git -C "$_mp_version_root" rev-list --count HEAD)"
fi
MP_PORT_REVISION="${MISSIONPLANNER_PORT_REVISION:-$_mp_default_revision}"
if [[ ! "$MP_PORT_REVISION" =~ ^[0-9]+$ ]]; then
  echo "Port build revision must be numeric: '$MP_PORT_REVISION'" >&2
  return 2 2>/dev/null || exit 2
fi
MP_FILE_VERSION="$MP_UPSTREAM_VERSION.0"
MP_INFORMATIONAL_VERSION="$MP_UPSTREAM_VERSION+$MP_BUILD_DATE.$MP_COMMIT$MP_DIRTY_SUFFIX"
MP_PACKAGE_VERSION="${PACKAGE_VERSION:-$MP_INFORMATIONAL_VERSION}"
MP_ARTIFACT_VERSION="$MP_UPSTREAM_VERSION-$MP_BUILD_DATE.$MP_COMMIT$MP_DIRTY_SUFFIX"
# Epoch 1 migrates from the old un-epoched CalVer packages. Put the monotonically ordered
# port revision before the hash so lexical hash order never decides which package is newer.
MP_DEBIAN_VERSION="${DEBIAN_VERSION:-1:$MP_UPSTREAM_VERSION+$MP_BUILD_DATE.r$MP_PORT_REVISION.$MP_COMMIT$MP_DIRTY_SUFFIX}"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$MP_UPSTREAM_VERSION" "$MP_BUILD_DATE" "$MP_COMMIT$MP_DIRTY_SUFFIX" \
    "$MP_FILE_VERSION" "$MP_INFORMATIONAL_VERSION" "$MP_PACKAGE_VERSION" \
    "$MP_ARTIFACT_VERSION" "$MP_PORT_REVISION" "$MP_DEBIAN_VERSION"
fi
