#!/bin/zsh

set -euo pipefail

script_dir=${0:A:h}
repo_root=${script_dir:h}
project="$repo_root/KyttoMCP.xcodeproj"
dist="$repo_root/dist"

: "${DEVELOPER_ID_APPLICATION:?Set DEVELOPER_ID_APPLICATION to the full Developer ID Application certificate name.}"
: "${NOTARY_PROFILE:?Set NOTARY_PROFILE to an xcrun notarytool keychain profile.}"

development_team=${DEVELOPMENT_TEAM:?Set DEVELOPMENT_TEAM to your Apple Developer Team ID.}
work=$(mktemp -d "${TMPDIR:-/tmp}/kyttomcp-beta.XXXXXX")
trap 'rm -rf "$work"' EXIT

archive="$work/KyttoMCP.xcarchive"
notary_zip="$work/KyttoMCP-notary.zip"

xcodebuild \
  -project "$project" \
  -scheme KyttoMCP \
  -configuration Release \
  -destination 'generic/platform=macOS' \
  -archivePath "$archive" \
  archive \
  CODE_SIGN_STYLE=Manual \
  DEVELOPMENT_TEAM="$development_team" \
  CODE_SIGN_IDENTITY="$DEVELOPER_ID_APPLICATION" \
  OTHER_CODE_SIGN_FLAGS=--timestamp

app="$archive/Products/Applications/KyttoMCP.app"
test -d "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ditto -c -k --keepParent "$app" "$notary_zip"
xcrun notarytool submit "$notary_zip" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=2 "$app"

version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")
build=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist")
mkdir -p "$dist"
artifact="$dist/KyttoMCP-${version}-${build}-beta.zip"
rm -f "$artifact"
ditto -c -k --keepParent "$app" "$artifact"

echo "Beta artifact ready: $artifact"
