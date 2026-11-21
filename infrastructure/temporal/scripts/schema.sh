#!/bin/sh
set -eu
# Used only for explicit initialization; never on routine startup or restore.
for db in temporal temporal_visibility; do
  kind=temporal
  [ "$db" = temporal ] || kind=visibility
  temporal-sql-tool --plugin postgres12 --ep "$POSTGRES_SEEDS" -u "$POSTGRES_USER" -p "$DB_PORT" --db "$db" setup-schema -v 0.0
  temporal-sql-tool --plugin postgres12 --ep "$POSTGRES_SEEDS" -u "$POSTGRES_USER" -p "$DB_PORT" --db "$db" update-schema -d "/etc/temporal/schema/postgresql/v12/$kind/versioned"
done
