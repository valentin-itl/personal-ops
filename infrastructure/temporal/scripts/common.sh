#!/usr/bin/env bash
set -Eeuo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
[[ -s .env ]] || { echo 'Run bash scripts/configure.sh first.' >&2; exit 1; }
dc() { docker compose "$@"; }
pg() { dc exec -T postgres "$@"; }
query() { pg psql -X -U postgres -d "$1" -At -v ON_ERROR_STOP=1 -c "$2"; }
admin() { dc run --rm --no-deps -T admin -c 'exec temporal "$@"' temporal "$@"; }
wait_temporal() {
  for ((i=0;i<24;i++)); do
    if admin operator cluster health --command-timeout 5s; then return 0; fi
    sleep 5
  done
  echo 'Temporal did not become healthy; inspect docker compose logs temporal.' >&2
  return 1
}
