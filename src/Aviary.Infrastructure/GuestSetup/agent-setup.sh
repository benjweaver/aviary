# Aviary guest SSH setup. Served once by Aviary and run as the desktop user: no root, no inbound ports.
# The guest keeps an idle outbound connection to Aviary (10.0.2.2 is the host's loopback in QEMU user
# networking); each one Aviary pairs with an ssh client becomes a key-only sshd session for this user.
set -u
port=__PORT__
done_url=http://10.0.2.2:$port/done/__TOKEN__
d=$HOME/.aviary-ssh
report() { curl -fsS "$done_url/$1" >/dev/null 2>&1 || wget -qO- "$done_url/$1" >/dev/null 2>&1; }
fail() { report "error/$1"; echo "Aviary SSH setup failed: $2" >&2; exit 1; }

sshd=$(command -v sshd || true)
for candidate in /usr/sbin/sshd /usr/bin/sshd /usr/local/sbin/sshd; do
    [ -n "$sshd" ] && break
    [ -x "$candidate" ] && sshd=$candidate
done
[ -n "$sshd" ] || fail no-sshd "OpenSSH server is not installed. Install it (e.g. openssh-server or openssh) and run this again."
bash=$(command -v bash) || fail no-bash "bash is required."

umask 077
mkdir -p "$d" || fail no-home "Could not create $d."
printf '%s\n' '__AUTHORIZED_KEY__' > "$d/authorized_keys"
printf '%s' '__HOST_KEY_B64__' | base64 -d > "$d/host" || fail no-base64 "base64 is required."
cat > "$d/agent.sh" <<EOF
#!${bash}
# Keeps one idle outbound connection to Aviary; a paired connection starts sshd for this user only.
while :; do
  "$bash" -c 'exec 3<>/dev/tcp/10.0.2.2/$port || exit 1
    printf "AVIARY-AGENT\n" >&3
    IFS= read -r -N1 -u 3 _ || exit 1
    setsid "$sshd" -i -h "\$1/host" -o AuthorizedKeysFile="\$1/authorized_keys" -o UsePAM=no -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no -o PidFile=none <&3 >&3 2>>"\$1/sshd.log" &' _ "$d" 2>/dev/null || sleep 2
done
EOF
chmod 700 "$d/agent.sh"

if command -v systemctl >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; then
    mkdir -p "$HOME/.config/systemd/user"
    cat > "$HOME/.config/systemd/user/aviary-ssh.service" <<EOF
[Unit]
Description=Aviary guest SSH (outbound to host)
[Service]
ExecStart=$bash $d/agent.sh
Restart=always
RestartSec=2
[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload && systemctl --user enable aviary-ssh >/dev/null 2>&1 && systemctl --user restart aviary-ssh || fail no-service "Could not start the systemd user service."
else
    # No systemd user session: start now and again at each desktop login.
    mkdir -p "$HOME/.config/autostart"
    printf '[Desktop Entry]\nType=Application\nName=Aviary SSH\nExec=%s %s/agent.sh\nNoDisplay=true\n' "$bash" "$d" > "$HOME/.config/autostart/aviary-ssh.desktop"
    nohup "$bash" "$d/agent.sh" >/dev/null 2>&1 &
fi

# Optional: stop the desktop locking itself, so screenshots and typed input reach a usable desktop. Per user, no root.
if [ "__KEEP_UNLOCKED__" = 1 ]; then
    for kw in kwriteconfig6 kwriteconfig5; do
        if command -v "$kw" >/dev/null 2>&1; then
            "$kw" --file kscreenlockerrc --group Daemon --key Autolock false
            "$kw" --file kscreenlockerrc --group Daemon --key LockOnResume false
            break
        fi
    done
    if command -v gsettings >/dev/null 2>&1; then
        gsettings set org.gnome.desktop.screensaver lock-enabled false 2>/dev/null
        gsettings set org.gnome.desktop.session idle-delay 0 2>/dev/null
    fi
fi

report "ok/$(id -un)" || fail no-report "Could not report back to Aviary."
echo "Aviary SSH is ready for $(id -un). Remove ~/.aviary-ssh and the aviary-ssh service or autostart entry to revoke it."
