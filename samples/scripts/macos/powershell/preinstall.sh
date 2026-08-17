#!/bin/bash
# Pre-install script for PowerShell 7 on macOS.
# Referenced from samples/manifests/Microsoft/Microsoft.PowerShell/*/powershell-macos-*.yaml
# via Scripts.PreInstall (doc/01-manifest-schema.md §5.4.2, AppType: pkg only).
LOGFILE="/var/log/powershell_preinstall.log"
exec > >(tee -i "$LOGFILE") 2>&1

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') - $1"; }

log "Starting PowerShell pre-install script"

# 1. Remove any Homebrew-installed PowerShell so it does not conflict with the Intune-managed install.
if command -v brew &>/dev/null; then
    if brew list --cask powershell &>/dev/null; then
        log "Removing Homebrew PowerShell cask..."
        brew uninstall --cask powershell 2>/dev/null
    fi
    if brew list powershell &>/dev/null; then
        log "Removing Homebrew PowerShell formula..."
        brew uninstall powershell 2>/dev/null
    fi
fi

# 2. Remove a stale pwsh symlink from a previous manual install.
if [ -L "/usr/local/bin/pwsh" ]; then
    log "Removing existing pwsh symlink..."
    rm -f /usr/local/bin/pwsh
fi

# 3. Check free disk space (minimum 500 MB).
AVAILABLE_KB=$(df / | tail -1 | awk '{print $4}')
if [ "$AVAILABLE_KB" -lt 500000 ]; then
    log "ERROR: Not enough disk space (${AVAILABLE_KB}KB available)"
    exit 1
fi

log "Pre-install script completed successfully"
exit 0
