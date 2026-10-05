#!/bin/sh
# Builds BwPicker.app for one CPU and zips it: scripts/package-macos.sh <arm64|x64> <version>
# Runs on macOS (CI). The app is ad-hoc signed only (no Developer ID): users open it the first time with right-click → Open.
set -eu

arch="$1"
version="$2"
app="BwPicker.app"

dotnet publish src/BwPicker/BwPicker.csproj -c Release -r "osx-$arch" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version="$version" -o publish

rm -rf pkg && mkdir -p "pkg/$app/Contents/MacOS" "pkg/$app/Contents/Resources"
cp publish/BwPicker publish/*.dylib "pkg/$app/Contents/MacOS/"
chmod 755 "pkg/$app/Contents/MacOS/BwPicker"

# Icon: every size the Finder and menu bar ask for, from the 256 px artwork.
iconset="$(mktemp -d)/bw-picker.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" src/BwPicker/assets/bw-picker.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" src/BwPicker/assets/bw-picker.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "pkg/$app/Contents/Resources/bw-picker.icns"

# LSUIElement: a menu-bar app, no Dock icon. The usage text appears when macOS asks for Accessibility access.
cat > "pkg/$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>com.github.capkz.bwpicker</string>
  <key>CFBundleName</key><string>BwPicker</string>
  <key>CFBundleDisplayName</key><string>BwPicker</string>
  <key>CFBundleExecutable</key><string>BwPicker</string>
  <key>CFBundleIconFile</key><string>bw-picker</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAccessibilityUsageDescription</key><string>BwPicker types the login you choose into the app in front.</string>
</dict>
</plist>
EOF

# Apple silicon refuses unsigned code; an ad-hoc signature is enough to run.
codesign --force --deep --sign - "pkg/$app"
codesign --verify --deep --strict "pkg/$app"

cp LICENSE README.md SECURITY.md pkg/
(cd pkg && ditto -c -k --sequesterRsrc . "../BwPicker-macos-$arch.zip")
echo "Built BwPicker-macos-$arch.zip"
