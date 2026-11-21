#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
dc config --quiet
dc --profile tools pull
dc up -d --wait postgres
# Refuse partial initialization rather than overwriting existing state.
count_tables() { query "$1" "SELECT count(*) FROM pg_tables WHERE schemaname='public';"; }
a=$(count_tables temporal); b=$(count_tables temporal_visibility)
if [[ $a == 0 && $b == 0 ]]; then
  dc run --rm --no-deps -T admin /scripts/schema.sh
elif [[ $a != 0 && $b != 0 ]]; then
  for db in temporal temporal_visibility; do
    [[ $(query "$db" "SELECT to_regclass('public.schema_version') IS NOT NULL;") == t ]] || { echo "Invalid schema in $db" >&2; exit 1; }
  done
  echo 'Existing schemas detected; no automatic schema upgrades.'
else
  echo 'Partial database initialization detected. Inspect before proceeding.' >&2; exit 1
fi
dc up -d --wait temporal ui
wait_temporal
if ! admin operator namespace describe --namespace default >/dev/null 2>&1; then
  admin operator namespace create --namespace default --retention 7d
fi
admin operator namespace describe --namespace default
echo 'Ready. UI and gRPC are loopback-only.'
