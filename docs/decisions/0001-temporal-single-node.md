# ADR 0001: Persistent single-node Temporal

Status: accepted initial direction.

Use one Temporal cluster on one low-cost VPS, SQL persistence and visibility in PostgreSQL, server and UI via Docker Compose, loopback access through SSH, independent worker apps and task queues. Prefer reproducibility and tested recovery over HA in this phase. Keep secrets and backup archives outside Git. No Elasticsearch, Kubernetes, or separate brokers without evidence of need. The single host is a failure domain; external backups and tested restore are required before critical workloads. Start with 4 history shards for low volume. Scale workers independently first, or migrate to another provider using the backup/restore runbook. Dedicated application database roles and application backups are added when stateful workers arrive.
