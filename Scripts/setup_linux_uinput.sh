#!/bin/bash
# Setup permissions for /dev/uinput on Linux for EchoLink Trackpad/Keyboard support.

set -e

echo "[EchoLink uinput] Checking /dev/uinput permissions..."

# 1. Ensure uinput module is loaded
if ! lsmod | grep -q "^uinput"; then
    echo "Loading uinput kernel module..."
    sudo modprobe uinput || echo "[WARNING] Could not modprobe uinput. It might be built-in or restricted."
fi

# 2. Check if current user has write access
if [ -w "/dev/uinput" ]; then
    echo "[EchoLink uinput] Already has write access to /dev/uinput."
    exit 0
fi

echo "Requesting permission to setup udev rule for /dev/uinput..."

# 3. Create udev rule to allow current user access without root in the future
# This creates a 'uinput' group, adds the current user to it, and tells udev to give that group access.
RULE_FILE="/etc/udev/rules.d/99-echolink-uinput.rules"

if [ ! -f "$RULE_FILE" ]; then
    echo 'KERNEL=="uinput", GROUP="uinput", MODE="0660", OPTIONS+="static_node=uinput"' | sudo tee "$RULE_FILE" > /dev/null
    echo "Udev rule created at $RULE_FILE"
fi

# 4. Create group and add user if not already present
if ! getent group uinput > /dev/null; then
    sudo groupadd uinput
    echo "Group 'uinput' created."
fi

if ! groups "$USER" | grep -q "\buinput\b"; then
    sudo usermod -aG uinput "$USER"
    echo "User '$USER' added to 'uinput' group."
    echo "[IMPORTANT] You may need to LOG OUT and LOG IN again for group changes to take effect."
fi

# 5. Immediate fix for the current session (temporary chmod)
sudo chmod 660 /dev/uinput
sudo chgrp uinput /dev/uinput

echo "[EchoLink uinput] Setup complete. Current session updated."
