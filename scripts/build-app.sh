#!/bin/bash
# Builds dist/Envy.app. Pass --install to also copy it to /Applications.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-1.0.0}"
BUNDLE_ID="dev.envy.Envy"
APP="dist/Envy.app"

swift build -c release --product Envy
BIN="$(swift build -c release --show-bin-path)/Envy"

[ -f Resources/AppIcon.icns ] || swift scripts/make-icon.swift Resources

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Envy"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Envy</string>
    <key>CFBundleDisplayName</key><string>Envy</string>
    <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
    <key>CFBundleExecutable</key><string>Envy</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Signs with your Developer ID certificate when you have one, so the Keychain keeps trusting Envy across rebuilds.
# Without one the signature is ad-hoc, and macOS asks again for Keychain access after each rebuild.
# Set CODESIGN_IDENTITY (a name or SHA-1 from `security find-identity -v -p codesigning`, or - for ad-hoc) to choose.
IDENTITY="${CODESIGN_IDENTITY-$(security find-identity -v -p codesigning | awk '/"Developer ID Application: /{print $2; exit}')}"
if [ -z "$IDENTITY" ] || [ "$IDENTITY" = "-" ]; then
    codesign --force --options runtime --timestamp=none --sign - "$APP"
    echo "Built $APP (ad-hoc signature)"
else
    codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
    echo "Built $APP (signed by $(codesign -dv --verbose=2 "$APP" 2>&1 | sed -n 's/^Authority=//p' | head -1))"
fi

if [ "${1-}" = "--install" ]; then
    rm -rf /Applications/Envy.app
    cp -R "$APP" /Applications/
    echo "Installed /Applications/Envy.app"
fi
