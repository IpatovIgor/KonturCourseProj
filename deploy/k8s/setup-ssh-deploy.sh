#!/usr/bin/env bash
set -euo pipefail

if (( EUID != 0 || $# != 1 )); then
    echo "Usage: sudo bash $0 <ed25519-public-key-file>" >&2
    exit 2
fi

read -r key_type key_data _ < "$1"
if [[ "$key_type" != ssh-ed25519 || ! "$key_data" =~ ^[A-Za-z0-9+/]+={0,2}$ ]]; then
    echo "Provide an OpenSSH Ed25519 public key." >&2
    exit 2
fi
ssh-keygen -lf "$1" >/dev/null

for tool in k3s kubectl jq curl flock; do
    command -v "$tool" >/dev/null || { echo "Required command not found: $tool" >&2; exit 2; }
done

directory=$(cd -- "$(dirname -- "$0")" && pwd)
admin=(k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml)
"${admin[@]}" apply -f "$directory/cd-access.yaml"
ca=$("${admin[@]}" config view --raw --flatten --minify \
    -o jsonpath='{.clusters[0].cluster.certificate-authority-data}')
token=$("${admin[@]}" -n habit-tracker create token habit-tracker-cd --duration=168h)
if [[ -z "$ca" || -z "$token" ]]; then
    echo "Could not obtain the CA and the deployment token from k3s." >&2
    exit 1
fi

if ! id habit-deploy >/dev/null 2>&1; then
    adduser --disabled-password --gecos '' habit-deploy
fi
home=$(getent passwd habit-deploy | cut -d: -f6)
group=$(id -gn habit-deploy)
commands=/usr/local/lib/habit-tracker
install -d -m 0755 "$commands"
install -m 0755 "$directory/apply-release.sh" "$directory/receive-release.sh" "$commands/"
install -d -o habit-deploy -g "$group" -m 0700 "$home/.ssh" "$home/.kube"

umask 077
scratch=$(mktemp -d)
trap 'rm -f -- "$scratch/authorized_keys" "$scratch/config"; rmdir -- "$scratch"' EXIT
printf 'restrict,command="%s/receive-release.sh" %s %s\n' "$commands" "$key_type" "$key_data" \
    > "$scratch/authorized_keys"
cat > "$scratch/config" <<EOF
apiVersion: v1
kind: Config
clusters:
- name: habit-tracker
  cluster:
    server: https://127.0.0.1:6443
    certificate-authority-data: $ca
users:
- name: habit-tracker-cd
  user:
    token: $token
contexts:
- name: habit-tracker
  context:
    cluster: habit-tracker
    user: habit-tracker-cd
    namespace: habit-tracker
current-context: habit-tracker
EOF
k3s kubectl --kubeconfig "$scratch/config" -n habit-tracker auth can-i patch deployment/webapi --quiet
k3s kubectl --kubeconfig "$scratch/config" -n habit-tracker get deployments webapi worker notifications frontend
install -o habit-deploy -g "$group" -m 0600 "$scratch/config" "$home/.kube/config"
sudo -H -u habit-deploy env SSH_ORIGINAL_COMMAND=check "$commands/receive-release.sh"
install -o habit-deploy -g "$group" -m 0600 "$scratch/authorized_keys" "$home/.ssh/authorized_keys"
echo "Installed the habit-deploy key and namespace-scoped kubeconfig. Renew the token before expiration."
