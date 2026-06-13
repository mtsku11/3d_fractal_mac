#!/usr/bin/env bash
# build-app.sh — build, bundle, sign, and optionally notarize Parsec.app
#
# Usage:
#   ./packaging/build-app.sh                        # build only (no signing)
#   ./packaging/build-app.sh --sign "Developer ID Application: Your Name (TEAMID)"
#   ./packaging/build-app.sh --sign "..." --notarize --apple-id you@example.com \
#                             --team-id TEAMID --keychain-profile myprofile
#
# Prerequisites:
#   brew install dotnet@9 openal-soft
#   Xcode Command Line Tools (for codesign, xcrun)
#
# After signing + notarizing, run:
#   xcrun stapler staple Parsec.app
#   spctl -a -t exec -vvv Parsec.app   # verify
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$REPO_ROOT/dist"
APP_BUNDLE="$OUT_DIR/Parsec.app"
MACOS_DIR="$APP_BUNDLE/Contents/MacOS"
RESOURCES_DIR="$APP_BUNDLE/Contents/Resources"
PUBLISH_DIR="$REPO_ROOT/src/Parsec.App/bin/Release/net9.0/osx-arm64/publish"

SIGN_IDENTITY=""
NOTARIZE=false
APPLE_ID=""
TEAM_ID=""
KEYCHAIN_PROFILE=""

# --- Argument parsing ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --sign)         SIGN_IDENTITY="$2"; shift 2 ;;
        --notarize)     NOTARIZE=true; shift ;;
        --apple-id)     APPLE_ID="$2"; shift 2 ;;
        --team-id)      TEAM_ID="$2"; shift 2 ;;
        --keychain-profile) KEYCHAIN_PROFILE="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

echo "==> Building self-contained osx-arm64 publish..."
dotnet publish "$REPO_ROOT/src/Parsec.App/Parsec.App.csproj" \
    -c Release -r osx-arm64 --self-contained \
    -p:NoWarn=CS0436 \
    --output "$PUBLISH_DIR"

echo "==> Creating app bundle at $APP_BUNDLE..."
rm -rf "$APP_BUNDLE"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Copy all published files into Contents/MacOS
cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"

# Info.plist goes in Contents/
cp "$SCRIPT_DIR/Info.plist" "$APP_BUNDLE/Contents/"

# --- Bundle libopenal-soft dylib ---
echo "==> Locating and bundling libopenal..."
OPENAL_SRC=""
# Look in Cellar first for the exact versioned dylib, then opt symlink
for candidate in \
    "$(ls /opt/homebrew/Cellar/openal-soft/*/lib/libopenal.dylib 2>/dev/null | sort -V | tail -1)" \
    /opt/homebrew/opt/openal-soft/lib/libopenal.dylib \
    /opt/homebrew/lib/libopenal.dylib \
    /usr/local/opt/openal-soft/lib/libopenal.dylib \
    /usr/local/lib/libopenal.dylib; do
    if [[ -f "$candidate" ]]; then
        OPENAL_SRC="$candidate"
        break
    fi
done

if [[ -z "$OPENAL_SRC" ]]; then
    echo "WARNING: libopenal.dylib not found — sonification audio will be unavailable."
    echo "         Install with: brew install openal-soft"
else
    echo "  bundling $OPENAL_SRC"
    cp "$OPENAL_SRC" "$MACOS_DIR/libopenal.dylib"
    # Fix the install name so the loader can find it at @executable_path/libopenal.dylib
    install_name_tool -id "@executable_path/libopenal.dylib" "$MACOS_DIR/libopenal.dylib"
fi

# --- macOS requires the executable bit on the main binary ---
chmod +x "$MACOS_DIR/parsec-app"

echo "==> App bundle created: $APP_BUNDLE"

# --- Code signing ---
if [[ -n "$SIGN_IDENTITY" ]]; then
    echo "==> Signing with identity: $SIGN_IDENTITY"
    ENTITLEMENTS="$SCRIPT_DIR/entitlements.plist"

    # Sign all dylibs first, then the main binary, then the bundle
    find "$APP_BUNDLE" -name "*.dylib" | while read -r dylib; do
        codesign --force --sign "$SIGN_IDENTITY" \
            --options runtime \
            --entitlements "$ENTITLEMENTS" \
            "$dylib"
    done

    # Sign the main executable
    codesign --force --sign "$SIGN_IDENTITY" \
        --options runtime \
        --entitlements "$ENTITLEMENTS" \
        "$MACOS_DIR/parsec-app"

    # Sign the entire bundle
    codesign --force --deep --sign "$SIGN_IDENTITY" \
        --options runtime \
        --entitlements "$ENTITLEMENTS" \
        "$APP_BUNDLE"

    echo "==> Verifying signature..."
    codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"

    if $NOTARIZE; then
        if [[ -z "$KEYCHAIN_PROFILE" && ( -z "$APPLE_ID" || -z "$TEAM_ID" ) ]]; then
            echo "ERROR: --notarize requires either --keychain-profile or both --apple-id and --team-id"
            exit 1
        fi

        echo "==> Zipping bundle for notarization..."
        ZIP_PATH="$OUT_DIR/Parsec.zip"
        ditto -c -k --keepParent "$APP_BUNDLE" "$ZIP_PATH"

        echo "==> Submitting to Apple notarization service..."
        if [[ -n "$KEYCHAIN_PROFILE" ]]; then
            xcrun notarytool submit "$ZIP_PATH" \
                --keychain-profile "$KEYCHAIN_PROFILE" \
                --wait
        else
            xcrun notarytool submit "$ZIP_PATH" \
                --apple-id "$APPLE_ID" \
                --team-id "$TEAM_ID" \
                --password "@keychain:AC_PASSWORD" \
                --wait
        fi

        echo "==> Stapling notarization ticket..."
        xcrun stapler staple "$APP_BUNDLE"

        echo "==> Verifying notarization..."
        spctl -a -t exec -vvv "$APP_BUNDLE"
        echo "==> Notarization complete."

        # Clean up zip
        rm -f "$ZIP_PATH"
    fi
else
    echo "(Skipping code signing — pass --sign \"Developer ID Application: ...\" to sign)"
fi

echo ""
echo "Done. App bundle: $APP_BUNDLE"
echo ""
echo "Quick test (unsigned):"
echo "  open $APP_BUNDLE"
echo ""
echo "To distribute: sign, notarize, then staple:"
echo "  ./packaging/build-app.sh --sign \"Developer ID Application: Name (TEAM)\" \\"
echo "      --notarize --keychain-profile myprofile"
