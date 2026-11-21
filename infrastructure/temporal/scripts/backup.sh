#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
# Consistent cross-database backup. Stop all external writers/workers first.
mode=${1:-}
[[ $mode == --restart || $mode == --leave-stopped ]] || { echo 'Usage: backup.sh --restart | --leave-stopped (stop external workers/clients first)' >&2; exit 2; }
umask 077
mkdir -p backups
out=$(mktemp -d "$(pwd)/backups/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")
complete=0
cleanup() {
  local rc=$?
  if (( ! complete )); then rm -rf -- "$out"; fi
  if [[ $mode == --restart ]]; then dc up -d temporal ui || rc=1; fi
  exit "$rc"
}
trap cleanup EXIT
dc stop ui temporal
for db in temporal temporal_visibility; do
  pg pg_dump -U postgres --format=custom --no-owner --no-privileges "$db" > "$out/$db.dump"
  # Check archive table of contents, not a substitute for a restore test.
  pg pg_restore --list < "$out/$db.dump" >/dev/null
done
cp compose.yaml "$out/compose.yaml"
( cd "$out"; sha256sum *.dump compose.yaml > SHA256SUMS )
complete=1
echo "Backup: $out (contains private data; encrypt and copy off-provider)."
