# High availability runbook — active/passive hMailServer

This runbook describes a supported, operationally simple **active/passive**
high-availability topology for hMailServer. It deliberately contains **no
clustering code**: hMailServer runs as a single active node at a time, and
failover is performed by external infrastructure (a shared database, shared
message storage, a floating virtual IP, and a health check). This keeps the mail
server itself simple and avoids split-brain message corruption.

For an active/active or sharded design you would need a different storage and
locking model; that is out of scope here.

---

## 1. Topology

```
                 ┌─────────────────────────────┐
   clients ─────►│  Virtual IP (VIP) / load     │
   (SMTP/IMAP/   │  balancer with health check  │
    POP/REST)    └──────────────┬───────────────┘
                                │  (routes to whichever node is "ready")
                ┌───────────────┴───────────────┐
                │                                │
        ┌───────▼────────┐              ┌────────▼───────┐
        │  Node A (active)│              │ Node B (passive)│
        │  hMailServer    │              │  hMailServer    │
        │  service RUNNING│              │  service STOPPED │
        └───────┬─────────┘              └────────┬────────┘
                │                                  │
                └──────────────┬───────────────────┘
                               │
              ┌────────────────▼─────────────────┐
              │  Shared database (MSSQL/MySQL/PG) │
              │  Shared message store (DataDirectory) │
              └───────────────────────────────────┘
```

Exactly **one** node runs the hMailServer service at any time. Both nodes are
configured identically and point at the **same** database and the **same**
message-store directory.

---

## 2. Shared components

### 2.1 Shared database

* Use an external database server (Microsoft SQL Server, MySQL, or PostgreSQL),
  **not** the built-in SQL Compact / internal database, which is local-only.
* Both nodes use the same `hMailServer.INI` `[Database]` connection settings.
* The database server should itself be made highly available (e.g. SQL Server
  Always On / failover cluster, managed RDS/Cloud SQL with HA), or hosted on the
  same shared-storage layer as the message store.

### 2.2 Shared message store

* hMailServer stores message files under `DataDirectory`. Both nodes must see the
  **same** directory on shared storage (SAN/NAS/clustered file system, or a
  cloud file share). The database row for each message references its on-disk
  path; the database and the message store must therefore stay consistent with
  each other, so keep them on the same failover boundary.
* Ensure both nodes' service accounts have identical read/write access to the
  share.

### 2.3 Virtual IP / load balancer

* Clients connect to a floating VIP (or a load balancer / DNS name) rather than a
  node's real address.
* The VIP must point at the node that is **ready** (see health checks below).
* Only ever direct traffic to one node at a time. If you use a load balancer that
  can see both nodes, gate routing strictly on the readiness probe so the passive
  node (service stopped → probe fails/refused) never receives traffic.

---

## 3. Health checks and readiness gating

hMailServer exposes Kubernetes-style probes on the metrics listener. Enable it on
**both** nodes:

```
[Settings]
MetricsServerPort=8080
MetricsServerBindAddress=0.0.0.0    ; reachable by the load balancer / health checker
ShutdownDrainSeconds=30             ; let in-flight sessions finish on a graceful stop
```

Probes (HTTP):

| Path       | Meaning                                                                 | Use for                          |
|------------|-------------------------------------------------------------------------|----------------------------------|
| `/livez`   | Process is alive (200 whenever the listener is up).                     | Liveness restarts.               |
| `/readyz`  | 200 only when the server is `Running` **and** the database is connected. Returns **503** while the server is starting, stopping, or draining. | **VIP / load-balancer routing.** |
| `/healthz` | JSON: `status`, server `state`, `database`.                            | Dashboards / debugging.          |

**Configure the VIP/load balancer health check against `/readyz`.** Because the
passive node's service is stopped, its `/readyz` connection is refused (unhealthy)
and it will never be routed to. During a graceful stop, the active node flips
`/readyz` to 503 *before* tearing down listeners, so the load balancer drains it
cleanly.

---

## 4. Normal operation

* **Active node (A):** hMailServer service set to *Automatic* and **running**.
* **Passive node (B):** hMailServer service set to *Manual* (or *Disabled*) and
  **stopped**. Keep the binaries and `hMailServer.INI` in sync with A (same
  version, same settings, same database + data directory).
* Apply configuration changes on the active node; because configuration lives in
  the shared database, B picks them up automatically when it becomes active. Keep
  the `hMailServer.INI` (which holds the database connection + local settings) in
  sync manually or via your configuration-management tooling.

---

## 5. Planned failover (maintenance)

1. **Drain A:** stop the hMailServer service on A. With `ShutdownDrainSeconds`
   set, A first reports `/readyz` = 503 (the load balancer stops sending new
   connections) and waits up to the drain window for active SMTP/IMAP/POP
   sessions to finish before shutting down.
2. **Verify A is down:** `/readyz` on A is refused.
3. **Move the VIP** to B (or let the load balancer health check do it).
4. **Start B:** start the hMailServer service on B. Wait until `/readyz` on B
   returns 200.
5. **Confirm:** send a test message through the VIP and confirm delivery; check
   `/healthz` reports `database: up` and `state: running`.

Reverse the steps to fail back.

---

## 6. Unplanned failover (node A fails)

1. The load balancer health check against `/readyz` on A fails; stop routing to A
   (automatic if the LB is health-check driven).
2. **Fence A** to guarantee it cannot still be writing to the shared store: power
   it off / isolate it from the storage and database network. This prevents
   split-brain (two active nodes writing the same message store).
3. **Move the VIP** to B.
4. **Start** the hMailServer service on B and wait for `/readyz` = 200.
5. When A is repaired, bring it back as the new passive node (service stopped)
   before any future failover.

> **Split-brain safety:** never let both nodes run the service against the shared
> store at the same time. Always fence the failed node before starting the
> standby. The shared database is the source of truth for message metadata; two
> active writers can corrupt mailbox state.

---

## 7. Validation checklist

* [ ] Both nodes use the same external database and the same `DataDirectory` on
      shared storage.
* [ ] `MetricsServerPort` is enabled and reachable by the health checker on both
      nodes; the VIP health check targets `/readyz`.
* [ ] `ShutdownDrainSeconds` is set so planned failovers drain gracefully.
* [ ] Passive node's service is stopped and set to Manual/Disabled.
* [ ] A documented fencing step exists for unplanned failover.
* [ ] A test message sent through the VIP is delivered after a planned failover in
      both directions.

---

## 8. What hMailServer provides vs. what you provide

| Provided by hMailServer                                  | Provided by your infrastructure          |
|----------------------------------------------------------|-------------------------------------------|
| `/livez` / `/readyz` / `/healthz` readiness gating       | Virtual IP / load balancer + health check |
| Graceful shutdown drain (`ShutdownDrainSeconds`)         | Shared database with its own HA           |
| Shared-database + shared-store single-active design      | Shared message storage (SAN/NAS/cloud)    |
| `/metrics` for alerting on the active node               | Fencing of a failed node                  |
