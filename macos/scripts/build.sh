#!/bin/bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
package_dir="$(cd -- "$script_dir/.." && pwd)"
dist_dir="$package_dir/dist"
app_name="Open With Cleaner Desktop Suoh Edition"
version="0.1.0"
archive_name="open-with-cleaner-desktop-suoh-edition-${version}-macos-universal.zip"

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "Building the macOS app requires macOS with Xcode Command Line Tools." >&2
    exit 1
fi

mkdir -p "$dist_dir" "$package_dir/.build"
stage_dir="$(mktemp -d "$package_dir/.build/package.XXXXXX")"
trap 'rm -rf -- "$stage_dir"' EXIT

swift build --package-path "$package_dir" --configuration release \
    --arch arm64 --arch x86_64 --product OpenWithCleanerMac
bin_dir="$(swift build --package-path "$package_dir" --configuration release \
    --arch arm64 --arch x86_64 --show-bin-path)"

app_dir="$stage_dir/$app_name.app"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
cp "$bin_dir/OpenWithCleanerMac" "$app_dir/Contents/MacOS/OpenWithCleanerMac"
chmod 755 "$app_dir/Contents/MacOS/OpenWithCleanerMac"
cp "$package_dir/Resources/Info.plist" "$app_dir/Contents/Info.plist"
plutil -lint "$app_dir/Contents/Info.plist"

iconset_dir="$stage_dir/AppIcon.iconset"
xcrun swift "$script_dir/make-icon.swift" "$iconset_dir"
iconutil --convert icns --output "$app_dir/Contents/Resources/AppIcon.icns" "$iconset_dir"

# An ad hoc signature preserves bundle integrity. It is not Developer ID signing
# and does not claim Apple notarization.
codesign --force --sign - --timestamp=none --options runtime "$app_dir"
codesign --verify --deep --strict --verbose=2 "$app_dir"
lipo -verify_arch arm64 x86_64 "$app_dir/Contents/MacOS/OpenWithCleanerMac"

# Keep an unpacked app for CI's launch check as well as the distributable ZIP.
ditto "$app_dir" "$dist_dir/$app_name.app"
cp "$package_dir/README.md" "$stage_dir/使用说明.md"
ditto -c -k --sequesterRsrc --keepParent "$app_dir" "$dist_dir/$archive_name"
# Add the usage file without changing the app bundle's signature.
(cd "$stage_dir" && /usr/bin/zip -q "$dist_dir/$archive_name" '使用说明.md')
(cd "$dist_dir" && shasum -a 256 "$archive_name" > SHA256SUMS.txt)
printf 'Built %s\n' "$dist_dir/$archive_name"
