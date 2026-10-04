#!/bin/sh
# Installs BwPicker for the current user from an unpacked release zip: the app goes to ~/.local/share/BwPicker
# (where its updater can replace it), with an entry in the applications menu. Run it from the unpacked folder:
#   sh install-linux.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dest="$data/BwPicker"

if pgrep -x BwPicker >/dev/null 2>&1; then
    echo "BwPicker is running. Quit it from its tray menu (Exit), then run this again." >&2
    exit 1
fi

mkdir -p "$dest" "$data/applications"
install -m 755 "$here/BwPicker" "$dest/BwPicker"
for lib in "$here"/*.so; do install -m 644 "$lib" "$dest/"; done
install -m 644 "$here/bw-picker.png" "$dest/bw-picker.png"

cat > "$data/applications/bwpicker.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=BwPicker
Comment=Type or copy Bitwarden logins with Ctrl+Alt+B
Exec="$dest/BwPicker"
Icon=$dest/bw-picker.png
Terminal=false
Categories=Utility;Security;
EOF

echo "Installed to $dest."

# Optional helpers: report what's missing instead of installing packages behind the user's back.
missing=""
if [ "${XDG_SESSION_TYPE:-}" = "wayland" ]; then
    command -v wl-copy >/dev/null 2>&1 || missing="$missing wl-clipboard"
else
    command -v xclip >/dev/null 2>&1 || missing="$missing xclip"
    ldconfig -p 2>/dev/null | grep -q 'libXtst.so.6' || missing="$missing libxtst6"
fi
if [ -n "$missing" ]; then
    echo "For copying and typing, also install:$missing (e.g. sudo apt install$missing)."
fi
if [ "${XDG_SESSION_TYPE:-}" = "wayland" ]; then
    echo "On Wayland, add a keyboard shortcut in your desktop settings (e.g. Ctrl+Alt+B) that runs: \"$dest/BwPicker\" --pick"
fi

nohup "$dest/BwPicker" >/dev/null 2>&1 &
echo "BwPicker is starting; look for its icon in the tray."
