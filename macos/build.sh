#!/bin/bash
# build.sh — na Macu postaví Regula.app (Intel + Apple Silicon) a Regula-<verzia>.dmg zo Sources/*.swift.
#   ./build.sh             → build/Regula.app + build/Regula-<verzia>.dmg
#   ./build.sh --rychlo    → len pre tento Mac, bez DMG (pri vývoji)
#   ./build.sh --install   → aj do /Applications
# Treba Command Line Tools (swiftc). Ikona: AppIcon.png z make_icon.py (na ThinkPade).
set -euo pipefail
cd "$(dirname "$0")"
VERZIA=1.0
BUILD=build
APP="$BUILD/Regula.app"
rm -rf "$BUILD" && mkdir -p "$BUILD"

ARCH=(x86_64 arm64)
[[ " $* " == *" --rychlo "* ]] && ARCH=("$(uname -m)")
echo "== kompilácia (${ARCH[*]})"
for arch in "${ARCH[@]}"; do
    swiftc -O -swift-version 5 -parse-as-library -target "$arch-apple-macos13.0" \
        -o "$BUILD/Regula-$arch" Sources/*.swift
done
lipo -create -output "$BUILD/Regula" "${ARCH[@]/#/$BUILD/Regula-}"

echo "== balík"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BUILD/Regula" "$APP/Contents/MacOS/Regula"
if [[ -f AppIcon.png ]]; then
    ICONSET="$BUILD/Regula.iconset"
    mkdir -p "$ICONSET"
    for velkost in 16 32 128 256 512; do
        sips -z $velkost $velkost AppIcon.png --out "$ICONSET/icon_${velkost}x${velkost}.png" >/dev/null
        sips -z $((velkost * 2)) $((velkost * 2)) AppIcon.png --out "$ICONSET/icon_${velkost}x${velkost}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Regula.icns"
fi
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Regula</string>
  <key>CFBundleDisplayName</key><string>Regula</string>
  <key>CFBundleIdentifier</key><string>sk.daniel.regula</string>
  <key>CFBundleExecutable</key><string>Regula</string>
  <key>CFBundleIconFile</key><string>Regula</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERZIA</string>
  <key>CFBundleVersion</key><string>$VERZIA</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHumanReadableCopyright</key><string>regula.is-my.app</string>
</dict>
</plist>
PLIST

echo "== podpis (ad-hoc, bez Apple ID)"
codesign --force -s - "$APP"
codesign --verify "$APP"
lipo -info "$APP/Contents/MacOS/Regula"

if [[ " $* " != *" --rychlo "* ]]; then
    echo "== disk image"
    STAGE="$BUILD/dmg"
    mkdir -p "$STAGE"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    hdiutil create -quiet -volname "Regula $VERZIA" -srcfolder "$STAGE" -ov -format UDZO "$BUILD/Regula-$VERZIA.dmg"
    ls -la "$BUILD/Regula-$VERZIA.dmg"
fi

if [[ " $* " == *" --install "* ]]; then
    pkill -x Regula 2>/dev/null || true
    rm -rf /Applications/Regula.app
    cp -R "$APP" /Applications/
    echo "== nainštalované do /Applications/Regula.app"
fi
