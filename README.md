# personal-ops

Private monorepo for personal automation and independent Temporal applications. First application: job-radar (freelance matching). The initial commit contains infrastructure only, not application implementation.

- `infrastructure/temporal/`: persistent single-node deployment, operator scripts and runbook.
- `apps/`: independently deployable .NET workers with their own task queues.
- `career/`: career profile schema and documentation; real passport is intentionally ignored until its handling is decided.
- `docs/decisions/`: architectural decisions.

Start with [Temporal deployment](infrastructure/temporal/README.md).
