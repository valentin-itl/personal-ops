#!/bin/sh
set -eu
# PostgreSQL runs this once for an empty data directory.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v pw="$TEMPORAL_DB_PASSWORD" <<'SQL'
CREATE ROLE temporal LOGIN PASSWORD :'pw';
CREATE DATABASE temporal OWNER temporal;
CREATE DATABASE temporal_visibility OWNER temporal;
SQL
