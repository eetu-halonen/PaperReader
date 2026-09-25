#!/usr/bin/env bash
# Builds the Linux desktop app and adds it to the application menu (and "Open with" for PDFs).
# Usage: src/PaperReader.Desktop/install.sh     (from the repository root)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$repo/dist/linux-x64"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
"$dotnet" publish "$here" -c Release -r linux-x64 --self-contained -o "$out"

apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor/scalable/apps"
mkdir -p "$apps" "$icons"
cp "$here/paper-reader.svg" "$icons/paper-reader.svg"
cat > "$apps/paper-reader.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Paper Reader
Comment=Listen to research papers; the math appears on screen
Exec="$out/paper-reader" %f
Icon=paper-reader
Terminal=false
Categories=Education;
MimeType=application/pdf;
StartupWMClass=paper-reader
DESKTOP
update-desktop-database "$apps" 2>/dev/null || true
echo "Installed: $out/paper-reader (menu entry: Paper Reader)"
for tool in pdftoppm ffplay; do
    command -v "$tool" >/dev/null || echo "Missing $tool: sudo apt install $([ "$tool" = pdftoppm ] && echo poppler-utils || echo ffmpeg)"
done
