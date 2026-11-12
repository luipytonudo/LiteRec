#!/usr/bin/env bash
# Instala LiteRec para el usuario actual (sin sudo) y lo añade al menú de aplicaciones.
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/share/LiteRec"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor/256x256/apps"

mkdir -p "$DEST" "$APPS" "$ICONS" "$HOME/.local/bin"
cp -r "$SRC"/. "$DEST"/
chmod +x "$DEST/LiteRec"
cp "$SRC/literec.png" "$ICONS/literec.png"
sed "s|@EXEC@|$DEST/LiteRec|" "$SRC/literec.desktop" > "$APPS/literec.desktop"
ln -sf "$DEST/LiteRec" "$HOME/.local/bin/literec"
update-desktop-database "$APPS" >/dev/null 2>&1 || true

echo "LiteRec instalado. Búscalo en el menú de aplicaciones o ejecuta: literec"

if ! command -v gpu-screen-recorder >/dev/null 2>&1 && ! flatpak info com.dec05eba.gpu_screen_recorder >/dev/null 2>&1; then
  echo
  echo "Recomendado: instala gpu-screen-recorder para grabar por GPU (y obligatorio en Wayland):"
  echo "  flatpak install flathub com.dec05eba.gpu_screen_recorder"
  if ! command -v ffmpeg >/dev/null 2>&1; then
    echo "O, en X11, al menos FFmpeg:  sudo apt install ffmpeg   (o el equivalente de tu distro)"
  fi
fi
