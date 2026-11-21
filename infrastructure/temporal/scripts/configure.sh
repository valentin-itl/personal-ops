#!/usr/bin/env bash
set -Eeuo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
umask 077
if [[ -e .env ]]; then echo '.env already exists; not replacing credentials.'; exit 0; fi
command -v openssl >/dev/null
( set -o noclobber; printf 'POSTGRES_PASSWORD=%s\nTEMPORAL_DB_PASSWORD=%s\n' "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" > .env )
echo 'Created .env (keep a secure copy outside Git).'
