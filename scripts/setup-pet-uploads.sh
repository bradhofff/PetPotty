#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
    echo "Usage: sudo $0 <systemd-app-user> [systemd-app-group]" >&2
    exit 2
fi

app_user="$1"
app_group="${2:-$1}"
upload_root="/var/www/petpotty/uploads"

install -d -o "$app_user" -g "$app_group" -m 755 "$upload_root"
install -d -o "$app_user" -g "$app_group" -m 755 "$upload_root/pets"

echo "Pet upload directory ready at $upload_root/pets (owner $app_user:$app_group, mode 755)."
