#!/bin/bash
# Builds dist/Envy.dmg, signed with your Developer ID and notarized by Apple, so colleagues can open it without
# warnings. Set VERSION to number the build (default 1.0.0). The file name stays the same for every version, so
# https://github.com/joeystdio/envy/releases/latest/download/Envy.dmg always serves the newest release.
#
# One-time setup: save notarization credentials in your Keychain. It asks for an app-specific password, which you
# make at https://account.apple.com under Sign-In and Security:
#   xcrun notarytool store-credentials notarytool-profile --apple-id you@example.com --team-id R78A8G6R2N
# Set NOTARY_PROFILE to use a profile saved under another name.
set -euo pipefail
cd "$(dirname "$0")/.."

PROFILE="${NOTARY_PROFILE:-notarytool-profile}"
APP="dist/Envy.app"

scripts/build-app.sh
SIGNER="$(codesign -dv --verbose=2 "$APP" 2>&1 | sed -n 's/^Authority=//p' | head -1)"
case "$SIGNER" in
    "Developer ID Application: "*) ;;
    *) echo "Notarizing needs a Developer ID Application certificate; $APP is signed by: ${SIGNER:-ad-hoc}" >&2; exit 1 ;;
esac

VERSION="$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP/Contents/Info.plist")"
DMG="dist/Envy.dmg"

# A disk image holding the app and a shortcut to /Applications to drag it onto.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -quiet -volname Envy -srcfolder "$STAGE" -format UDZO "$DMG"
codesign --sign "$SIGNER" --timestamp "$DMG"

# Apple checks the image and the app inside it. Stapling the ticket on lets Gatekeeper verify it offline.
echo "Uploading $DMG for notarization; this usually takes a few minutes…"
RESULT="$(xcrun notarytool submit "$DMG" --keychain-profile "$PROFILE" --wait --output-format json)" || true
STATUS="$(plutil -extract status raw -o - - <<<"$RESULT" 2>/dev/null || echo "not submitted")"
if [ "$STATUS" != "Accepted" ]; then
    echo "Notarization failed ($STATUS). $RESULT" >&2
    if ID="$(plutil -extract id raw -o - - <<<"$RESULT" 2>/dev/null)"; then
        xcrun notarytool log "$ID" --keychain-profile "$PROFILE" >&2 || true
    fi
    exit 1
fi
xcrun stapler staple -q "$DMG"
spctl --assess --type open --context context:primary-signature --verbose "$DMG"
echo "Ready to share: $DMG (Envy $VERSION)"
echo "Publish it with: gh release create v$VERSION $DMG --title \"Envy $VERSION\" --generate-notes"
