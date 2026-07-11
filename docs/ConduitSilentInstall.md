# Conduit Unattended Installer

Silent, no-UI install of the Conduit sync service for self-serve distribution.
Built on the existing InstallerRuntime embed model (app zip appended to the exe).

## Package layout

```
ConduitInstaller\
    ConduitSetup.exe                  InstallerRuntime + embedded Conduit publish zip
    conduit.provision.sample.json     sidecar template
    redist\SQLEXPR_x64_ENU.exe        SQL Server Express setup (side-by-side, ~260 MB)
```

Built by `Build-ConduitInstaller.ps1` (pass `-ConduitRepo` and optionally
`-SqlExpressSetup`). SQL Express is deliberately **not** embedded in the exe:
its setup must land on disk to run anyway, and embedding would push the exe
past 400 MB and double peak memory in the extractor. "Always bundled" is
satisfied at the distribution-package level.

## Invocation

```
ConduitSetup.exe --silent                       # sidecar conduit.provision.json next to the exe
ConduitSetup.exe --silent --config D:\acme.json # explicit sidecar path
ConduitSetup.exe                                # a sidecar next to the exe also auto-triggers silent mode
```

Must run elevated (the exe manifest requests it; RMM tools must supply an admin
token). Output goes to `%TEMP%\ConduitInstall-<timestamp>.log`, copied to
`<installPath>\install.log` on completion, and mirrored to the parent console
when launched from one.

## Sidecar schema (`conduit.provision.json`)

| Field | Required | Default | Notes |
|---|---|---|---|
| `enrollUrl` | yes | — | Platform URL, **https only** (the enroll code must never cross the wire in cleartext). Probed during preflight (TLS + GET). |
| `enrollCode` | yes | — | Single-use, 15-minute TTL. Cannot be validated before use — generate it right before installing. |
| `mode` | yes | — | `on-prem` (requires domain-joined host) or `cloud-only` (domain check skipped). |
| `tenantSlug` | no | — | Informational; logged only. |
| `installPath` | no | `C:\Program Files\Conduit` | |
| `serviceName` | no | `IdentityCenterConduit` | |
| `serverPort` | no | Conduit default (5500) | Stamped as `Provision:ServerPort`. |
| `adminUsername` | no | Conduit default (`admin`) | The admin **password is never set by the installer** — Conduit generates it and writes it to `%PROGRAMDATA%\Conduit\admin-initial-password.txt`. |
| `sql.connectionString` | no | — | Skips detection + bootstrap. LocalDB rejected. |
| `sql.instanceName` | no | — | Preferred existing local instance. |
| `sql.database` | no | `Conduit` | |
| `sql.allowExpressInstall` | no | `true` | Bootstrap SQL Express only when no usable instance exists. |
| `sql.expressSetupPath` | no | `redist\SQLEXPR*.exe` | Override for the setup exe location. |
| `sql.grantServiceAccess` | no | `true` | On an **existing** instance, grant `NT AUTHORITY\SYSTEM` a login + `dbcreator` so the LocalSystem service can create its DB. |

JSON comments and trailing commas are allowed.

## Install sequence

1. **Preflight (fail fast):** elevation → domain-joined (on-prem only) →
   ASP.NET Core 8 runtime present (payload is framework-dependent) → HTTPS
   probe of the enroll host (DNS / TLS-interception / proxy / 407 failures
   reported distinctly).
2. **SQL resolve:** explicit connection string → else first usable local
   instance (order: configured > default > SQLEXPRESS > CONDUIT > alphabetical;
   LocalDB excluded; connectivity tested with integrated auth) → else silent
   SQL Express install as dedicated instance **CONDUIT** (Windows auth only,
   TCP/named pipes/Browser disabled — local shared-memory access only,
   `NT AUTHORITY\SYSTEM` sysadmin so the service can create its DB).
   Never installs over an existing usable instance.
3. **Stop existing service** (upgrade), snapshot `appsettings*.json`.
4. **Extract** the embedded Conduit publish to the install path.
5. **Stamp the BASE `appsettings.json`** (never the environment file — Conduit's
   SetupService rewrites that wholesale mid-setup): `Provision:ConnectionString`,
   optional `AdminUsername`/`ServerPort`, installer-generated `JwtSecretKey`
   (kept if already stamped), plus `Enroll:Url`/`Enroll:Code`. AdminPassword is
   never written. On upgrade, the pre-existing `appsettings.Production.json` is
   restored verbatim.
6. **Lock down `%PROGRAMDATA%\Conduit`** to Administrators + SYSTEM (inheritance
   cut) **before** first service start — the generated admin password file
   lands there.
7. **Register the "Conduit" event-log source** (needs elevation; the service
   account cannot create it at runtime).
8. **Service:** `sc create` (or `sc config` on upgrade) `binPath=<installPath>\Conduit.Web.exe`,
   auto-start, restart-on-failure (5s/10s/30s, daily reset), then start and wait
   for Running (generous timeout — first start runs DB init + provisioning).
9. **Enrollment outcome:** poll `%PROGRAMDATA%\Conduit\enroll-status.json`
   (only files written after service start count) for ~90 s and report.
   Stale/consumed-code-shaped failures get an explicit "generate a fresh code"
   message.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success — installed, service running, enrollment confirmed (or already enrolled) |
| 2 | Installed and running; enrollment outcome not reported within the poll window (check enroll-status.json / event log) |
| 1 | Unexpected error |
| 10 | Sidecar missing / invalid JSON / failed validation |
| 20 | Not elevated |
| 21 | Not domain-joined (on-prem mode) |
| 22 | Cannot reach enroll host (network/TLS/proxy) |
| 23 | ASP.NET Core 8 runtime missing |
| 30 | No usable SQL instance and Express bootstrap unavailable |
| 31 | SQL Express silent install failed |
| 32 | SQL connect failed |
| 40 | Payload extraction failed |
| 50 | appsettings.json stamping failed |
| 60 | ProgramData ACL lock-down failed (install aborts — password would be world-readable) |
| 61 | Event-log source registration failed |
| 70 | Service create/config failed |
| 71 | Service did not reach Running |
| 80 | Installed OK, but enrollment reported Failed (stale code, network, tenant mismatch) |

## Design decisions

- **SQL instance name `CONDUIT`** — avoids colliding with dead `SQLEXPRESS`
  remnants a broken uninstall may leave in the registry, and makes ownership
  obvious in server inventories.
- **Framework-dependent Conduit publish** (plan of record; single-file
  rejected) — hence the ASP.NET Core runtime preflight.
- **LocalSystem service account** — on the bootstrapped CONDUIT instance,
  SYSTEM is sysadmin via setup; on a reused existing instance, the installer
  grants SYSTEM `dbcreator` only (flag: `sql.grantServiceAccess`). A dedicated
  low-privilege service account is the GA hardening follow-up.
- **No Programs-and-Features registration yet** — the existing UninstallWindow
  is IIS/IdentityCenter-shaped; registering an uninstall entry that runs the
  wrong engine would be worse than none. Follow-up item.

## Not provable without a real machine (see NEEDS-LIVE-SMOKE)

SQL Express silent install, sc.exe service lifecycle under a real SCM, ACL
behavior with the real service account, the domain check on a domain-joined
box, and the end-to-end enroll handshake have **not** been exercised — unit
tests cover argument construction, parsing, merge, and skip logic only.
