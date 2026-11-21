# Temporal deployment

Target: Ubuntu 24.04 LTS, 2 vCPU, 4 GB RAM, Docker Engine + Compose v2, Git, OpenSSL and curl. Install Docker using https://docs.docker.com/engine/install/ubuntu/ (avoid running a random installation script). Use a non-root SSH account with key authentication. Docker group membership is root-equivalent. Keep Ubuntu patched, restrict SSH in the host firewall, and enable 2 GB swap if absent. Do not expose the Temporal API, UI or PostgreSQL publicly; the Temporal service here has no application auth/TLS. Only trusted workers may join the network. Docker-published ports bind to loopback; keep Docker current.

## First installation

Run from this directory:

```bash
bash scripts/configure.sh
bash scripts/bootstrap.sh
bash scripts/verify.sh
```

Bootstrap downloads pinned images, starts PostgreSQL, initializes two schemas via the matching admin image if empty, starts Temporal/UI, and creates the default namespace (7-day closed-workflow retention). History shards are set to 4 at creation; this setting cannot be changed by editing Compose after initialization. Retention is not a workflow execution time limit. PostgreSQL initializes independent admin and Temporal login credentials from `.env`. Do not edit existing passwords in `.env` without rotating the actual database roles. Store `.env` securely offsite or regenerate credentials on a fresh restore target. No application database or app secret management implemented yet.

Connect from the workstation (substitute actual SSH host/user):

```bash
ssh -N -L 8080:127.0.0.1:8080 -L 7233:127.0.0.1:7233 user@server
```

Open http://localhost:8080 and connect SDK/CLI to `localhost:7233`. A Docker-based worker on the same server can join the external Docker network `personal-ops-temporal` and connect to `temporal:7233`. Use a dedicated task queue per app, e.g. `job-radar`. Workflows must be deterministic; use activities for I/O, with idempotent side effects. Do not attach untrusted containers to this network. No public HTTP reverse proxy required.

## Routine operations

```bash
docker compose ps
docker compose logs -f temporal
docker stats --no-stream
bash scripts/verify.sh
docker compose stop
docker compose up -d --wait postgres temporal ui
```

Never use `docker compose down -v` or prune the database volume. Stop is non-destructive. `bootstrap.sh` does not upgrade existing schemas. Before changing any image versions, review corresponding release and schema upgrade instructions, take a backup and test restoration. Run the same-version restore before performing a version upgrade. Use current PostgreSQL minor security releases after validation. Do not combine a provider migration and a Temporal upgrade.

## Consistent backup and migration

Stop all workers and any clients that submit workflow changes, and wait for in-flight activities to finish if possible. Backups take a maintenance window and pause the server, so the two SQL dumps are from a quiescent cluster. On the source:

```bash
bash scripts/backup.sh --restart       # routine backup, server restarts after dump
# OR
bash scripts/backup.sh --leave-stopped  # migration; prevent dual writes
```

Resume external workers after routine backup. The script does not stop app containers outside this Compose project. The output directory contains both custom-format dumps, checksums and Compose image/config reference. It is private but NOT encrypted. Encrypt before transfer or offsite storage, e.g. using age with a recipient whose private key is independently backed up:

```bash
tar -C backups -czf - BACKUP_DIRECTORY_NAME | age -r AGE_PUBLIC_RECIPIENT -o temporal-backup.tar.gz.age
```

Copy the encrypted archive to independent storage. Do not commit dumps. Delete plaintext copies after verifying an encrypted offsite copy. No backup scheduler or remote storage destination is configured by this starter. Establish those before workflows become important; recommended initial RPO 24h, RTO a few hours (targets, not guarantees). Backup contains Temporal state only, not future application databases/files/secrets. Extend procedure to quiesce and capture those before deployment of stateful apps.

On the fresh target, checkout the matching repository revision, install Docker, generate new `.env` with `configure.sh`, and securely transfer/decrypt the backup to an absolute path. **Do not run bootstrap first.**

```bash
bash scripts/configure.sh
bash scripts/restore.sh /absolute/path/to/backup-directory
bash scripts/verify.sh
```

The restore script refuses nonempty schema databases, reuses the predefined Temporal role as owner, and starts same-version services after import. It does not blindly restore role passwords or source secrets. Verify schedules and sample open workflow history; reconnect compatible workers and clients only when ready. Missed schedules follow catch-up and overlap policies. Interrupted activities may retry: design them for idempotency. Never run source and restored target with the same active workloads; retain source stopped as rollback until validation, and avoid reverting after new writes without a coordinated data transfer.

If restore partially fails, inspect output and provision another clean target; do not delete source data. A successful dump is NOT proof of recoverability. The acceptance milestone is a separate clean-host restore with a running/sleeping workflow, a scheduled workflow and successful continuation. CI is a basic infrastructure smoke check, not a full workflow recovery test.

## References

- https://github.com/temporalio/samples-server/tree/main/compose
- https://docs.temporal.io/self-hosted-guide/deployment
- https://docs.temporal.io/self-hosted-guide/visibility
- https://docs.temporal.io/cli/operator
