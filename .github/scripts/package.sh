#!/usr/bin/env bash
# Packages a Release build of BetterMods as the mod zip and checks it.
#
# Usage: package.sh <build-output-dir> <dest-dir>
#   <build-output-dir>  the BetterMods OutputPath for Release: bin/Release/Mods/bettermods
#   <dest-dir>          where bettermods_<version>.zip is written
# Run from the repository root (reads modinfo.json, LICENSE, THIRD-PARTY-NOTICES.md).
#
# Fails when an expected file is missing, when BetterModsHook.dll leaked into the output, when the
# startup hook is not embedded in BetterMods.dll, or when a built binary carries a local absolute
# path (i.e. the Release PathMap did not apply).
#
# Writes to GITHUB_OUTPUT (when set): version, zip-name, zip-path, artifact-name.
set -euo pipefail

out="${1:?usage: package.sh <build-output-dir> <dest-dir>}"
dest="${2:?usage: package.sh <build-output-dir> <dest-dir>}"

fail() {
  echo "::error::$*"
  exit 1
}

version="$(jq -er '.version' modinfo.json)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] \
  || fail "modinfo.json version '$version' is not x.y.z or x.y.z-suffix"
zip_name="bettermods_${version}.zip"

build_files=(modinfo.json BetterMods.dll BetterMods.deps.json BetterMods.pdb)
repo_files=(LICENSE THIRD-PARTY-NOTICES.md)

[ -d "$out" ] || fail "Build output folder '$out' does not exist"

# A BetterModsHook.dll at the mod root would be loaded by the game's mod loader and fail the mod;
# the hook must only exist embedded inside BetterMods.dll.
leaked="$(find "$out" -iname 'BetterModsHook*' -print)"
[ -z "$leaked" ] || fail "BetterModsHook leaked into the mod output: $leaked"

for f in "${build_files[@]}"; do
  [ -f "$out/$f" ] || fail "Missing from the build output: $out/$f"
done
for f in "${repo_files[@]}"; do
  [ -f "$f" ] || fail "Missing from the repository root: $f"
done
cmp -s "$out/modinfo.json" modinfo.json || fail "$out/modinfo.json differs from modinfo.json - stale build output?"

# The startup hook ships as a manifest resource of BetterMods.dll.
LC_ALL=C grep -a -q -F 'BetterMods.Startup.BetterModsHook.dll' "$out/BetterMods.dll" \
  || fail "BetterMods.dll does not embed the startup hook resource"

# PathMap check. NUL bytes are dropped first so UTF-16 strings (the user-string heap) are matched
# as well as UTF-8 ones (the PDB path in the debug directory).
needles=(-e '/home/runner' -e 'C:\Users')
if [ -n "${GITHUB_WORKSPACE:-}" ]; then needles+=(-e "$GITHUB_WORKSPACE"); fi
for f in BetterMods.dll BetterMods.pdb; do
  hits="$(LC_ALL=C tr -d '\000' < "$out/$f" | LC_ALL=C grep -a -o -i -F "${needles[@]}" | sort -u || true)"
  [ -z "$hits" ] || fail "$f contains local absolute paths (PathMap did not apply): $(printf '%s' "$hits" | tr '\n' ' ')"
done

mkdir -p "$dest"
zip_path="$dest/$zip_name"
rm -f "$zip_path"
files=()
for f in "${build_files[@]}"; do files+=("$out/$f"); done
files+=("${repo_files[@]}")
# -j: every file at the zip root; -X: no extra file attributes.
zip -q -j -X "$zip_path" "${files[@]}"

expected="$(printf '%s\n' "${build_files[@]}" "${repo_files[@]}" | sort)"
actual="$(unzip -Z1 "$zip_path" | sort)"
if [ "$expected" != "$actual" ]; then
  fail "Unexpected zip contents. Expected: $(echo $expected) Got: $(echo $actual)"
fi

echo "Packaged $zip_path:"
unzip -l "$zip_path"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "version=$version"
    echo "zip-name=$zip_name"
    echo "zip-path=$zip_path"
    echo "artifact-name=bettermods_${version}"
  } >> "$GITHUB_OUTPUT"
fi
