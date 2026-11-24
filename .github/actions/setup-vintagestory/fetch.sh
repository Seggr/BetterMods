#!/usr/bin/env bash
# Puts the Vintage Story assemblies BetterMods compiles against into VS_DIR.
#
# Inputs (environment):
#   VS_VERSION  game version, e.g. 1.23.0-pre.1
#   VS_CHANNEL  CDN channel: stable | unstable | pre
#   VS_DIR      target folder; laid out like a game install (VintagestoryAPI.dll, Lib/...)
#   VS_CDN      optional; archive base URL, default https://cdn.vintagestory.at/gamefiles
#               (overridable only to exercise this script against local test archives)
#
# If VS_DIR already holds every required DLL (restored from the cache) nothing is downloaded.
# Otherwise the dedicated-server archive (~60 MB) is tried first and the client archive (~590 MB)
# only if the server one lacks a required DLL. Only VintagestoryAPI.dll, VintagestoryLib.dll and
# Lib/*.dll are extracted; the archives themselves are deleted. Game files never leave the runner
# except through the actions/cache entry.
#
# Writes to GITHUB_OUTPUT (when set): archive=<server|client|cached>, downloaded=<true|false>.
set -euo pipefail

: "${VS_VERSION:?}" "${VS_CHANNEL:?}" "${VS_DIR:?}"

required=(
  VintagestoryAPI.dll
  VintagestoryLib.dll
  Lib/0Harmony.dll
  Lib/Newtonsoft.Json.dll
  Lib/cairo-sharp.dll
)

# Prints the required DLLs missing from VS_DIR, one per line.
missing_dlls() {
  local f
  for f in "${required[@]}"; do
    test -f "$VS_DIR/$f" || printf '%s\n' "$f"
  done
}

set_output() {
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
  fi
}

# fetch_archive <server|client>: download one archive and extract the assemblies into VS_DIR.
# Returns non-zero (leaving a warning) when the archive cannot be fetched or lacks a required DLL.
fetch_archive() {
  local kind="$1"
  local url="${VS_CDN:-https://cdn.vintagestory.at/gamefiles}/${VS_CHANNEL}/vs_${kind}_linux-x64_${VS_VERSION}.tar.gz"
  local work
  work="$(mktemp -d "${RUNNER_TEMP:-/tmp}/vs-${kind}.XXXXXX")"
  local archive="$work/archive.tar.gz"

  echo "Downloading the ${kind} archive: $url"
  if ! curl -fL --retry 3 --retry-delay 5 --connect-timeout 30 -sS -o "$archive" "$url"; then
    echo "::warning::Could not download $url"
    rm -rf "$work"
    return 1
  fi
  echo "Downloaded $(du -h "$archive" | cut -f1)"

  tar -tzf "$archive" > "$work/entries"

  # The archive may or may not wrap everything in a top-level folder (or "./"). Find the
  # shallowest VintagestoryAPI.dll and treat whatever precedes it as the prefix to strip.
  local api
  api="$(grep -E '(^|/)VintagestoryAPI\.dll$' "$work/entries" \
    | awk -F/ '{ print NF "\t" $0 }' | sort -n | head -n 1 | cut -f2- || true)"
  if [ -z "$api" ]; then
    echo "::warning::The ${kind} archive has no VintagestoryAPI.dll"
    rm -rf "$work"
    return 1
  fi
  local prefix="${api%VintagestoryAPI.dll}"
  local strip
  strip="$(printf '%s' "$prefix" | tr -cd '/' | wc -c)"
  echo "Archive prefix: '${prefix}' (stripping ${strip} path component(s))"

  # Exact member names to extract: the two game assemblies and the DLLs directly under Lib/.
  awk -v p="$prefix" '
    substr($0, 1, length(p)) == p {
      r = substr($0, length(p) + 1)
      if (r == "VintagestoryAPI.dll" || r == "VintagestoryLib.dll" || r ~ /^Lib\/[^\/]+\.dll$/) print
    }' "$work/entries" > "$work/members"

  rm -rf "$VS_DIR"
  mkdir -p "$VS_DIR"
  tar -xzf "$archive" -C "$VS_DIR" --strip-components="$strip" \
    --no-wildcards --verbatim-files-from -T "$work/members"
  rm -rf "$work"

  local missing
  missing="$(missing_dlls)"
  if [ -n "$missing" ]; then
    echo "::warning::The ${kind} archive lacks: $(printf '%s' "$missing" | tr '\n' ' ')"
    return 1
  fi
  echo "${kind}" > "$VS_DIR/.archive-kind"
  return 0
}

if [ -z "$(missing_dlls)" ]; then
  kind="$(cat "$VS_DIR/.archive-kind" 2>/dev/null || echo unknown)"
  echo "Using cached Vintage Story ${VS_VERSION} assemblies (from the ${kind} archive) in $VS_DIR"
  set_output archive cached
  set_output downloaded false
  exit 0
fi

used=""
for kind in server client; do
  if fetch_archive "$kind"; then
    used="$kind"
    break
  fi
  if [ "$kind" = server ]; then
    echo "Falling back to the client archive."
  fi
done

if [ -z "$used" ]; then
  echo "::error::Could not get the Vintage Story ${VS_VERSION} assemblies from the ${VS_CHANNEL} channel (server or client archive)."
  exit 1
fi

echo "::notice::Vintage Story ${VS_VERSION} assemblies taken from the ${used} archive (${VS_CHANNEL} channel)"
for f in "${required[@]}"; do
  echo "  $f  $(du -h "$VS_DIR/$f" | cut -f1)"
done
set_output archive "$used"
set_output downloaded true
