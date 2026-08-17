#!/bin/bash
# Post-install script for PowerShell 7 on macOS.
# Referenced from samples/manifests/Microsoft/Microsoft.PowerShell/*/powershell-macos-*.yaml
# via Scripts.PostInstall (doc/01-manifest-schema.md §5.4.2, AppType: pkg only). Per Graph's
# documented behavior, a non-zero exit here is not reported - the app install still shows
# "success" - so this script only logs diagnostics rather than gating on its own success.
LOGFILE="/var/log/powershell_postinstall.log"
exec > >(tee -i "$LOGFILE") 2>&1

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') - $1"; }

log "Starting PowerShell post-install script"

# 1. Confirm pwsh installed correctly.
if [ -x "/usr/local/bin/pwsh" ]; then
    INSTALLED_VERSION=$(/usr/local/bin/pwsh --version 2>/dev/null)
    log "PowerShell installed: $INSTALLED_VERSION"
else
    log "WARNING: pwsh not found at /usr/local/bin/pwsh"
fi

# 2. Add /usr/local/bin to PATH via /etc/paths.d so pwsh is reachable in new login shells.
if [ ! -f "/etc/paths.d/powershell" ]; then
    echo "/usr/local/bin" > /etc/paths.d/powershell
    log "Added /usr/local/bin to PATH via /etc/paths.d/powershell"
fi

log "Post-install script completed"
exit 0
