#!/bin/sh
set -eu
[ "$(id -u)" = 0 ] || { echo "Run this script with sudo inside the guest."; exit 1; }
if id aviary-agent >/dev/null 2>&1 && [ ! -f /etc/aviary-agent-managed ]; then
  echo "aviary-agent already exists and is not managed by Aviary. No account changes made."; exit 1
fi
had_server=0
if command -v sshd >/dev/null 2>&1; then had_server=1; fi
if command -v apt-get >/dev/null; then apt-get update; apt-get install -y openssh-server
elif command -v dnf >/dev/null; then dnf install -y openssh-server
elif command -v pacman >/dev/null; then pacman -S --needed --noconfirm openssh
elif command -v apk >/dev/null; then apk add openssh
else echo "Install OpenSSH server using this distribution's package manager, then configure the supplied public key manually."; exit 1
fi
if ! id aviary-agent >/dev/null 2>&1; then
  if command -v useradd >/dev/null; then useradd -m -s /bin/sh aviary-agent
  else adduser -D -s /bin/sh aviary-agent; fi
  password=$(head -c 32 /dev/urandom | base64)
  printf 'aviary-agent:%s\n' "$password" | chpasswd
  unset password
  touch /etc/aviary-agent-managed
fi
home=$(getent passwd aviary-agent | cut -d: -f6)
[ -n "$home" ] || exit 1
install -d -m 700 -o aviary-agent -g "$(id -gn aviary-agent)" "$home/.ssh"
printf '%s\n' '__PUBLIC_KEY__' > "$home/.ssh/authorized_keys"
chmod 600 "$home/.ssh/authorized_keys"
chown aviary-agent:"$(id -gn aviary-agent)" "$home/.ssh/authorized_keys"
if [ "$had_server" = 0 ]; then
  cp /etc/ssh/sshd_config /etc/ssh/sshd_config.before-aviary-install
  { printf 'AllowUsers aviary-agent\nPasswordAuthentication no\n'; cat /etc/ssh/sshd_config.before-aviary-install; } > /etc/ssh/sshd_config
fi
if ! grep -q '^# Aviary agent access$' /etc/ssh/sshd_config; then
  cp /etc/ssh/sshd_config /etc/ssh/sshd_config.before-aviary
  printf '\n# Aviary agent access\nMatch User aviary-agent\n    AuthenticationMethods publickey\n    PasswordAuthentication no\n' >> /etc/ssh/sshd_config
fi
ssh-keygen -A
mkdir -p /run/sshd
if ! sshd -t; then cp /etc/ssh/sshd_config.before-aviary /etc/ssh/sshd_config; echo "SSH validation failed; previous configuration restored."; exit 1; fi
if command -v systemctl >/dev/null; then
  systemctl enable --now sshd 2>/dev/null || systemctl enable --now ssh
  systemctl restart sshd 2>/dev/null || systemctl restart ssh
elif command -v rc-service >/dev/null; then rc-update add sshd; rc-service sshd restart
else echo "Configuration saved. Start sshd with your distribution's service manager."; exit 1; fi
echo "SSH configured for aviary-agent (standard user, public key only). Check your guest firewall if the connection is blocked."
