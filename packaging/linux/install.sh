#!/bin/sh
# Installs sshelfy for the current user: the program in ~/.local/bin, plus a menu entry and icon.
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove it again (your hosts and vault in ~/.config/sshelfy stay)
# Set PREFIX to install somewhere else, e.g. PREFIX=/usr/local sudo -E ./install.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
prefix=${PREFIX:-$HOME/.local}
bin="$prefix/bin/sshelfy"
desktop="$prefix/share/applications/sshelfy.desktop"
icon="$prefix/share/icons/hicolor/256x256/apps/sshelfy.png"

refresh_menu() {
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true
    fi
}

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$bin" "$desktop" "$icon"
    refresh_menu
    echo "sshelfy removed from $prefix."
    exit 0
fi

mkdir -p "$(dirname "$bin")" "$(dirname "$desktop")" "$(dirname "$icon")"
cp "$here/sshelfy" "$bin"
chmod 755 "$bin"
cp "$here/sshelfy.png" "$icon"
# The menu entry points at the installed program, so it works even when ~/.local/bin is not on PATH.
sed "s|^Exec=.*|Exec=$bin|" "$here/sshelfy.desktop" > "$desktop"
refresh_menu

echo "sshelfy installed to $bin and added to the application menu."
