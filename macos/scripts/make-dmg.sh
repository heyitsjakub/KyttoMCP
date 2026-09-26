#!/bin/zsh
# Builds the shipping macOS DMG: Release configuration, ad-hoc signed.
#
# Refuses to produce a DMG unless the built app passes every check below.
# The defect this exists to prevent shipped in 1.0.5: an Apple Development
# signature with com.apple.security.get-task-allow, which leaves the app's
# memory open to any local process. It is invisible from the filename, the
# version string and the checksum, so it has to be checked here.
#
# Note: `spctl --assess` is deliberately NOT checked. An ad-hoc signed app
# always fails Gatekeeper assessment; that is expected until notarization.

set -euo pipefail

script_dir=${0:A:h}
repo_root=${script_dir:h}
project="$repo_root/KyttoMCP.xcodeproj"
derived="$repo_root/build/ReleaseDMG"
dist="$repo_root/dist"

# An *archive* build is what strips com.apple.security.get-task-allow. A plain
# `xcodebuild build` keeps it even when signing ad-hoc, which is how it reached
# the published 1.0.5. Do not change this back to `build`.
#
# ENABLE_HARDENED_RUNTIME is YES in the project and must stay YES there: it is
# what we want the day this is signed with a real Developer ID, and Apple will
# not notarize without it. It is turned off *here*, on the ad-hoc path only,
# because the hardened runtime enables library validation, and library
# validation requires the app and its embedded KyttoCore.framework to share a
# Team ID. An ad-hoc signature has no Team ID at all, so the two cannot match
# and dyld refuses to map the framework -- the app dies at launch, before a
# single line of Swift runs. That is the defect that shipped in 1.0.5:
#   Library not loaded: @rpath/KyttoCore.framework/Versions/A/KyttoCore
#   ... (non-platform) have different Team IDs
# Delete this argument the moment a Developer ID certificate is in use.
echo "==> 1/5  Archiving Release, ad-hoc signed"
rm -rf "$derived"
archive="$derived/KyttoMCP.xcarchive"
xcodebuild \
  -project "$project" \
  -scheme KyttoMCP \
  -configuration Release \
  -destination 'generic/platform=macOS' \
  -derivedDataPath "$derived" \
  -archivePath "$archive" \
  archive \
  CODE_SIGN_STYLE=Manual \
  CODE_SIGN_IDENTITY="-" \
  DEVELOPMENT_TEAM="" \
  PROVISIONING_PROFILE_SPECIFIER="" \
  ENABLE_HARDENED_RUNTIME=NO

app="$archive/Products/Applications/KyttoMCP.app"
if [[ ! -d "$app" ]]; then
  echo "FAIL  the app was not produced at $app"
  exit 1
fi

echo
echo "==> 2/5  Verifying the signature"
fail=0

# Universal is what a correct archive build produces and what ships. 1.0.5 went
# out arm64-only because the broken build path inherited ONLY_ACTIVE_ARCH from
# this machine, not because anyone chose it. If that ever changes deliberately,
# update the published system requirements in the same change.
archs=$(lipo -archs "$app/Contents/MacOS/KyttoMCP")
if [[ "$archs" == *arm64* && "$archs" == *x86_64* ]]; then
  echo "  ok    architecture: universal ($archs)"
else
  echo "  FAIL  architecture is '$archs', expected a universal x86_64 + arm64 build"
  fail=1
fi

# Capture before matching: with `set -o pipefail`, `grep -q` exits on its first
# match, codesign takes SIGPIPE, and the pipeline reports failure even when the
# text was found. That produced a false "not ad-hoc signed" on the first run.
sig=$(codesign -dvvv "$app" 2>&1 || true)
ents=$(codesign -d --entitlements - "$app" 2>/dev/null || true)

if print -r -- "$sig" | grep -q 'flags=.*adhoc'; then
  echo "  ok    ad-hoc signed"
else
  echo "  FAIL  not ad-hoc signed:"
  print -r -- "$sig" | grep -E '^Authority|flags' | sed 's/^/          /'
  fail=1
fi

if print -r -- "$ents" | grep -q 'get-task-allow'; then
  echo "  FAIL  com.apple.security.get-task-allow is present — this is a debug signature"
  fail=1
else
  echo "  ok    no get-task-allow entitlement"
fi

if [[ -e "$app/Contents/embedded.provisionprofile" ]]; then
  echo "  FAIL  an embedded provisioning profile is present"
  fail=1
else
  echo "  ok    no embedded provisioning profile"
fi

if codesign --verify --strict --verbose=2 "$app" >/dev/null 2>&1; then
  echo "  ok    signature verifies"
else
  echo "  FAIL  the signature does not verify"
  fail=1
fi

if (( fail )); then
  echo
  echo "Refusing to build a DMG. Nothing in dist/ was touched."
  exit 1
fi

# The check that was missing when 1.0.5 shipped. Everything above inspects the
# signature and says it is fine -- and it was fine; `codesign --verify --deep
# --strict` passes on the broken build. What failed was dyld refusing to load
# the framework under that signature, which nothing can see without actually
# starting the process. So start it. A GUI window may flash past; that is the
# test working.
echo
echo "==> 3/5  Launching the app"

launch_log=$(mktemp "${TMPDIR:-/tmp}/kytto-launch.XXXXXX")
"$app/Contents/MacOS/KyttoMCP" >"$launch_log" 2>&1 &
launch_pid=$!

# Long enough for dyld to finish mapping and for an early crash to land. The
# 1.0.5 failure is immediate -- it never reaches main() -- but a slower one is
# worth catching too.
sleep 5

if kill -0 "$launch_pid" 2>/dev/null; then
  echo "  ok    the app launched and was still running after 5s"
  kill "$launch_pid" 2>/dev/null || true
  wait "$launch_pid" 2>/dev/null || true
  rm -f "$launch_log"
else
  echo "  FAIL  the app exited on launch:"
  sed 's/^/          /' "$launch_log" | head -20
  rm -f "$launch_log"
  echo
  echo "Refusing to build a DMG. Nothing in dist/ was touched."
  exit 1
fi

echo
echo "==> 4/5  Building the DMG"
version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")
staging=$(mktemp -d "${TMPDIR:-/tmp}/kytto-dmg.XXXXXX")
trap 'rm -rf "$staging"' EXIT

ditto "$app" "$staging/KyttoMCP.app"
ln -s /Applications "$staging/Applications"

mkdir -p "$dist"
dmg="$dist/KyttoMCP-${version}.dmg"
rm -f "$dmg"
hdiutil create \
  -srcfolder "$staging" \
  -volname "KyttoMCP $version" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -quiet \
  "$dmg"

echo
echo "==> 5/5  Done"
echo
bytes=$(stat -f%z "$dmg")
printf '  File:     %s\n' "${dmg:t}"
# LC_ALL=C so the decimal separator is a dot — "4.3 MB", not the "4,3" some
# locales print.
printf '  Size:     %s MB\n' "$(LC_ALL=C awk -v b="$bytes" 'BEGIN{printf "%.1f", b/1000000}')"
printf '  SHA-256:  %s\n' "$(shasum -a 256 "$dmg" | cut -d' ' -f1)"
echo
echo "Publish that size and checksum alongside the release."
