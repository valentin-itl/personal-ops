#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
dc ps
admin operator cluster health
admin operator namespace describe --namespace default
for db in temporal temporal_visibility; do
  echo "Schema: $db"
  pg psql -X -U postgres -d "$db" -v ON_ERROR_STOP=1 -c 'SELECT * FROM schema_version;'
done
curl --fail --silent --show-error http://127.0.0.1:8080/ >/dev/null
echo 'Basic health checks passed.'
