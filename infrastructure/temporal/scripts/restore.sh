#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
[[ $# == 1 ]] || { echo 'Usage: restore.sh /absolute/path/to/backup-directory' >&2; exit 2; }
backup=$(realpath -- "$1")
( cd "$backup"; sha256sum -c SHA256SUMS )
cmp -s compose.yaml "$backup/compose.yaml" || { echo 'Use the original compose.yaml and pinned image versions to restore first.' >&2; exit 1; }
# Never restore into an existing installation, even if Temporal is stopped.
[[ -z $(dc ps --status running -q temporal ui) ]] || { echo 'Temporal/UI running. Restore only on a fresh target.' >&2; exit 1; }
dc up -d --wait postgres
for db in temporal temporal_visibility; do
  [[ $(query "$db" "SELECT count(*) FROM pg_tables WHERE schemaname='public';") == 0 ]] || { echo "$db is not empty; refusing restore." >&2; exit 1; }
done
for db in temporal temporal_visibility; do
  pg pg_restore -U postgres --role=temporal --no-owner --no-privileges --exit-on-error --single-transaction -d "$db" < "$backup/$db.dump"
done
dc up -d --wait temporal ui
wait_temporal
admin operator namespace describe --namespace default
echo 'Restored. Verify histories, schedules and application data before starting workers.'
