#!/bin/bash
# setup-uinput.sh
# This script configures udev rules to allow the EchoLink background service 
# to write to /dev/uinput without requiring root privileges.

echo "Setting up udev rules for /dev/uinput..."

# Create a udev rule that assigns the uinput device to the 'input' group
# and gives that group read/write permissions.
echo 'KERNEL=="uinput", GROUP="input", MODE="0660"' | sudo tee /etc/udev/rules.d/99-echolink-uinput.rules > /dev/null

# Reload udev rules and trigger them
echo "Reloading udev rules..."
sudo udevadm control --reload-rules
sudo udevadm trigger

# Add the current user to the 'input' group
echo "Adding user $USER to the 'input' group..."
sudo usermod -aG input $USER

echo ""
echo "===================================================================="
echo "Setup Complete!"
echo "Please log out and log back in (or reboot) for the group changes"
echo "to take effect. EchoLink will then be able to control the mouse."
echo "===================================================================="
