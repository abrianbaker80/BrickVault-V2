# M2 foundation runbook

A single operator can sign in to the local HTTPS Workbench, save catalog and location references and inspect the resulting audit and worker records in PostgreSQL. Use original, fictional reference material. M2 supports a local development instance and the dedicated private server. The server hosts the gateway, API, worker and database. Each workstation needs its own trusted browser connection. Business-data cutover, production recovery objectives and live provider or device operations have separate gates. [Feature requirements T01–T06](../architecture/FEATURE_MATRIX.md#m2--persistent-application-and-operations-foundation) · [M2 checkpoint](https://github.com/abrianbaker80/BrickVault-V2/issues/6)

## Build a clean checkout

Use Windows x64 or Ubuntu 24.04 x64, Git, PowerShell 7.6+, supported Python 3.12+ and Node 24.x (24.14 or later) with npm. Install the missing prerequisites for the platform, then clone the repository. Use the revision under review on issue #6 or the merged revision containing `scripts/foundation/manage.py`. The Python operations CLI uses the standard library. [Repository setup](../../README.md#m0-setup) · [Python downloads](https://www.python.org/downloads/) · [Node 24 downloads](https://nodejs.org/en/download/archive/v24) · [PowerShell on Ubuntu](https://learn.microsoft.com/en-us/powershell/scripting/install/install-ubuntu)

Run the examples in PowerShell at the checkout root as a normal user. On Ubuntu, setup obtains missing build prerequisites using the system's signed apt repositories (`build-essential`, `bison`, `flex`, `zlib1g-dev`, `libssl-dev` and the Firefox profile tool `libnss3-tools`) and may prompt for sudo. It verifies the SHA-256 of the PostgreSQL 18.7 source tarball and builds the tools under `.tools/foundation/pgsql`. On Windows, setup downloads the checksum-pinned 18.7 binary distribution. Each instance initializes its own cluster. [Foundation installer](../../scripts/foundation/setup.py)

Build installs the npm and NuGet locks, generates OpenAPI types and validators, type-checks and builds Vue, and publishes the framework-dependent API and worker with the static site under `.tools/foundation/release`. The hosted Ubuntu check uploads that directory as `brickvault-m2-linux-x64`. Record the workflow run and revision when retaining its release. [M2 workflow](../../.github/workflows/m2-foundation.yml)

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$foundationPython = if ($IsWindows) { 'python' } else { 'python3' }
& $foundationPython --version
node --version
npm --version
$manager = (Resolve-Path ./scripts/foundation/manage.py).Path
function Invoke-Foundation { & $foundationPython $manager @args }

Invoke-Foundation setup
Invoke-Foundation build
Push-Location ./src/workbench
try {
    if ($IsWindows) { npx playwright install firefox }
    else { npx playwright install --with-deps firefox }
} finally { Pop-Location }
Invoke-Foundation verify --port 25440
```

`verify` uses nine consecutive ports. It assigns three each to the source, off-host recovery drill and second-instance restore. Choose an available range below the host's ephemeral TCP port range. Verification exercises authenticated HTTPS, retries, worker recovery, permissions, migrations, xUnit, Firefox Workbench flows, credential rotation and a restore while the source is stopped. Both the HTTPS client and the browser verify the instance certificate. Each test browser profile receives the public CA for that instance. A successful run includes a restore time and per-table counts. Attach the redacted results, OS and revision to the checkpoint. [Qualification entry point](../../scripts/foundation/verify.py) · [Playwright browser installation](https://playwright.dev/docs/browsers)

## Start Workbench

Initialize an empty state directory and a separate owner-only recovery directory outside the checkout. The following example uses generic directories under the current user profile. Use separate protected storage for recovery keys when retaining a backup beyond the lab. Each instance needs three loopback ports.

```powershell
$lab = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'brickvault-foundation-lab'
$state = Join-Path $lab 'source'
$keys = Join-Path $lab 'source-keys'
Invoke-Foundation init --state $state --recovery $keys --port 49420
Invoke-Foundation start --state $state
Invoke-Foundation status --state $state
```

`start` prints the HTTPS origin and `status` reports supervisor and gateway readiness. On Windows, run the command below to open Firefox with a dedicated Workbench profile. The launcher installs the instance's public certificate in that profile. For an existing Firefox installation on Ubuntu, pass the path of its actual executable with `--firefox`. Enter the password from the private state's `owner-login.txt` in the Workbench sign-in form.

```powershell
& $foundationPython ./scripts/foundation/browser.py open --state $state
```

1. Under **Catalog**, create an operator-authored item. Use a fictional number such as `0007`, variant `A` and known color ID `0`. Supply the source, condition, completeness, rights and mapping. A reference can preserve an unknown color or ambiguous mapping. Integration eligibility requires a confirmed mapping, acceptable rights and resolved color, condition and completeness.
2. Under **Locations**, create a fictional location such as `DEMO-01`. Use **Activity → Inspect** to trace the command ID, revision, correlation ID and worker outcome. Sign in independently in a second browser profile, with the instance CA trusted there, and refresh to read the same records.
3. If a save loses its response, refresh the connection and select **Retry command** in **Pending confirmation**. The browser retains the original payload and ID until resolved. The database returns the original receipt on an identical replay. A changed payload, duplicate identity or stale revision produces a conflict. Review the current record before making a new decision. Loading, disconnected and partial reads suspend new commands. A worker with an overdue heartbeat appears stale under **Activity**.
4. After signing in, open `/api/v1/export?scope=reference` at the same origin to export locations and catalog rows whose rights allow export. A mapping can be ambiguous even when export rights are established. Unknown/restricted rights withhold the catalog row. Provider permission or redistribution rights need independent evidence before entering third-party material. Only first-party references are authorized for this milestone.

```powershell
Invoke-Foundation stop --state $state
Invoke-Foundation start --state $state
Invoke-Foundation rotate --state $state
```

`rotate` replaces the operator password, gateway token and session-signing key and restarts a running instance. Sign in again with the new credential. API, worker, migration and cluster administration each have a separate DB role. The API writes references and appends receipts, audit and jobs. The worker claims jobs and records processing results. The migrator owns the schema. Scoped roles, a development profile and loopback connection checks restrict startup. Production requires a separate deployment and secret set.

Sessions expire after eight hours. The worker purges expired session rows. Audit, command and job evidence persists with the instance. Unconfirmed browser commands survive browser restart in the profile. Resolve them before retiring the profile. Restrict runtime files, the browser profile, backups, build archives and diagnostics to the operator. The lab has no customer or shipping records. Their production retention schedule, backup deletion rules and recovery key custody require decisions before collection.

## Run Workbench on the private server

Give the guest a stable private DNS or mDNS name and permit the HTTPS port from authorized workstations in its firewall. Restrict administration to the management network. Install nftables from the signed guest package repository. Install the pinned tools, reviewed Linux release and repository scripts under `/opt/brickvault-v2` as root. Create the dedicated operating system account `brickvault` with a private home at `/var/lib/brickvault-v2`. Generate the age identity on the recovery workstation. Deliver only its public recipient to the guest.

Run these commands in a Linux shell after setting `WORKBENCH_ORIGIN` to the canonical HTTPS origin (including port 49480) and `PUBLIC_RECIPIENT` to the age public recipient. Keep the administrator's code and release files read-only to the service account.

```sh
cd /opt/brickvault-v2
sudo -H -u brickvault python3 scripts/foundation/manage.py init \
  --state /var/lib/brickvault-v2/instance --port 49480 \
  --lan-origin "$WORKBENCH_ORIGIN" --recipient "$PUBLIC_RECIPIENT"
sudo install -m 0644 scripts/foundation/systemd/brickvault-egress.service scripts/foundation/systemd/brickvault-workbench.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now brickvault-workbench.service
sudo -H -u brickvault python3 scripts/foundation/manage.py status --state /var/lib/brickvault-v2/instance
```

Deliver the gateway's public CA from `instance/caddy-store/pki/authorities/local/root.crt` over the authenticated administration channel. Calculate its SHA-256 over the DER certificate and verify it on the workstation. In a private workstation directory, save the CA and a `connection.json` containing `origin`, `ca` (the certificate path) and `caSha256` (the lowercase DER digest). Open Workbench with `python scripts/foundation/browser.py remote --connection <connection.json>`. The launcher trusts this CA within the dedicated Firefox profile. Deliver the operator password separately. Only the HTTPS gateway accepts application traffic from the LAN. PostgreSQL and the API listen on loopback. A dedicated nftables chain limits new connections from the application account to loopback. Established workstation replies and operating system updates continue normally.

Use systemd to start and stop the supervised service. Before a credential rotation or other maintenance requiring a stopped instance, stop the unit, use the management command, then restart the unit and verify readiness and sign-in. Capture a fresh recovery point before a migration or bulk change. [Nightly backup operations](m2-hosted-backups.md) · [Recovery after controller loss](m2-cloud-receipts.md)

## Back up and restore to a second instance

With the source running, record the catalog, location and audit entries to compare. Recovery requires the encrypted archive, its original age decryption identity, and the **exact release directory** from the source instance. The dump manifest stores hashes of every published release file, including the UI. Rebuilding the same commit can yield different bytes. Save the release directory, source revision and dependency/tool pins with the recovery materials. Keep the private age identity separate from the archive.

```powershell
$archive = Join-Path $lab 'reference-demo.age'
$retainedRelease = Join-Path $lab 'release-for-reference-demo'
if (Test-Path -LiteralPath $retainedRelease) { throw 'Choose a new release archive directory.' }
Copy-Item -LiteralPath (Join-Path $state 'release') -Destination $retainedRelease -Recurse
Invoke-Foundation backup --state $state --file $archive

$destination = Join-Path $lab 'restore'
$destinationKeys = Join-Path $lab 'restore-keys'
Invoke-Foundation init --state $destination --recovery $destinationKeys --port 49430
Invoke-Foundation start --state $destination
$sourceKeys = @(Get-ChildItem -LiteralPath $keys -Filter '*.agekey' -File)
if ($sourceKeys.Count -ne 1) { throw 'Select the decryption identity for the source instance.' }
Invoke-Foundation restore --state $destination --file $archive --identity $sourceKeys[0].FullName --target bv_m2_restored
Invoke-Foundation stop --state $destination
Invoke-Foundation activate --state $destination --target bv_m2_restored
Invoke-Foundation start --state $destination
Invoke-Foundation status --state $destination
```

Use a new archive filename and a fresh target database on every restore. Both initializations above copy the same published release. For recovery in a fresh checkout on the same OS/architecture, install the matching tools and place the contents of the retained release at `.tools/foundation/release` before initializing the destination. The destination generates its own operator, gateway, session, DB and TLS credentials. Use the **source** identity to decrypt its backup. The destination identity protects future backups from the recovered instance.

Restore checks the archive, dump and release hashes, restores into a new database, reapplies grants, and compares counts and SHA-256 signatures for schema versions, catalog, locations, command receipts, audit, jobs and effects. Live sessions and worker heartbeat data are excluded. Activation checks the target again while the instance is stopped. The restored workspace retains its original identity so the browser can replay its pending commands. A separately initialized workspace has its own identity. Trust the destination's public CA in a fresh browser profile, open the new origin, sign in with its new password and compare the records and audit. Stop both lab instances with `Invoke-Foundation stop --state $destination` and `Invoke-Foundation stop --state $state` when finished. The drill measures recovery of a logical snapshot. Off-host failover, point-in-time recovery and production RPO/RTO require separate evidence.

## Independent backup storage

An off-host recovery point contains the encrypted PostgreSQL backup and the exact matching release in one age-encrypted archive. The transfer uses the provider account's configured backup root and adds a dedicated `BrickVault-V2/` child. An optional second directory receives the encrypted bytes and their receipt. Keep the trusted receipt and V2 decryption key available independently of the source. Confirm cloud replication of a desktop sync folder separately.

Use a private transport descriptor prepared by the account administrator. Its `remote` field names a configured rclone remote and optional prefix, `config` is the absolute path of its encrypted rclone configuration, and `password_command` is a trusted local credential helper. The CLI runs a checksum-verified rclone 1.75.1 with a temporary encrypted configuration copy. The V2 instance itself needs no provider credential. Keep the descriptor and its helper under administrative control.

Run the following in the PowerShell session from the setup example. Supply dedicated directories for the source, receipts, local encrypted mirror and recovery keys. A finalized receipt appears only after a fresh provider download matches the original ciphertext. Retry an interrupted publication with `publish --resume <point>` and the original arguments. The receipt binds publication retries to the source instance, workspace, mirror, provider route and a fingerprint of the exact encrypted transfer profile. If the profile changes, capture a fresh point. Previously verified points remain recoverable from their trusted receipts. Older receipts without that binding can still verify a recovery.

```powershell
$offhost = (Resolve-Path ./scripts/foundation/offhost.py).Path
$route = Join-Path $lab 'private-transport.json'
$receipts = Join-Path $lab 'trusted-receipts'
$mirror = Join-Path $lab 'encrypted-mirror'
& $foundationPython $offhost publish --state $state --transport $route --receipts $receipts --mirror $mirror
```

For a disaster rehearsal, stop the source, use its independently retained recovery identity and the matching trusted receipt. Use the same OS and architecture as the source, with the foundation tools installed. The release binaries come from the remote archive. The destination generates new credentials. After signing in, compare the expected business records, audit and original command receipt. Retain the destination's new key for future backups.

```powershell
$destination = Join-Path $lab 'offhost-recovery'
$destinationKeys = Join-Path $lab 'offhost-recovery-keys'
Invoke-Foundation stop --state $state
& $foundationPython $offhost recover --state $destination --recovery $destinationKeys --port 49430 --transport $route --receipt $trustedReceipt --identity $sourceIdentity
Invoke-Foundation start --state $destination
```

The qualification command exercises publication, retry and fresh recovery with a local rclone test destination. Issue #6 contains the separate external-storage rehearsal result. The dedicated server has a nightly schedule and freshness monitoring. The timed full-guest rehearsal below covers the M2 reference dataset. Set retention and recovery targets for the production workload before cutover. [M2 checkpoint](https://github.com/abrianbaker80/BrickVault-V2/issues/6)

## Rehearse a full guest rebuild

Use a recovery computer with hardware virtualization, space for a new guest disk and a trusted Ubuntu 24.04 x64 recovery environment. Keep an independent copy of the expected workspace, catalog, locations, audit entries, original command payloads and receipts. Have the source age identity and public receipt trust file available in recovery custody.

1. Start the timer before provisioning the VM or installing missing tools. Verify the OS image against the distributor's signed checksum using its trusted signing key. Boot a fresh Ubuntu 24.04 x64 guest with its own disk, hostname and private HTTPS origin. Limit access to the rehearsal clients and the administration channel. Pin the guest's SSH host key using its hypervisor console.
2. On the guest, install the reviewed foundation scripts, the service account and nftables as described under [Run Workbench on the private server](#run-workbench-on-the-private-server). Run `python3 scripts/foundation/manage.py setup` to install the pinned tools from verified downloads and signed OS packages. Use the same PostgreSQL build and architecture on the recovery workstation and guest. Leave the guest service stopped and its instance path unused.
3. Fetch the encrypted archive and signed sidecar from the provider. Authenticate both against the independent public trust file. On the trusted recovery workstation, run the commands below from the reviewed checkout as the recovery user in an isolated Linux network namespace with loopback enabled. Set `DOWNLOAD` and `POINT` to the downloaded directory and point name, `STATE` and `NEW_KEYS` to separate fresh private paths, and `PORT` to the rehearsal origin's port. Keep both decryption identities in workstation custody. [Provider download and public trust](m2-cloud-receipts.md)

   ```sh
   set -eu
   umask 077
   python3 scripts/foundation/cloud_receipt.py verify \
     --directory "$DOWNLOAD" --point "$POINT" --trust "$TRUST"
   python3 scripts/foundation/offhost.py recover \
     --file "$DOWNLOAD/$POINT.age" --receipt "$DOWNLOAD/$POINT.json" \
     --identity "$SOURCE_IDENTITY" --state "$STATE" --recovery "$NEW_KEYS" --port "$PORT"
   test ! -e "$STATE/pgdata/postmaster.pid"
   test -z "$(find "$STATE" -name '*.agekey' -print -quit)"
   LC_ALL=C .tools/foundation/pgsql/bin/pg_controldata "$STATE/pgdata" |
     grep -Eq '^Database cluster state:[[:space:]]+shut down$'
   ```

4. Transfer only the stopped instance tree, including its exact verified release, over the pinned SSH channel into an unused private staging directory on the guest. Preserve modes and assign ownership to the guest's `brickvault` account. Compare hashes for the transferred files against the stopped source. After they match, rename the staged tree to the instance path used by the unit. As that account, run `python3 scripts/foundation/manage.py configure-lan --state "$STATE_ON_GUEST" --lan-origin "$REHEARSAL_ORIGIN"`. Install the egress and Workbench units and enable and start Workbench using the server procedure above.
5. Pin the rebuilt gateway's public CA in a fresh client profile. Sign in with its new owner credential. Compare the workspace identity, catalog, locations and audit to the separate checkpoint. Replay the original commands with their original IDs and payloads. Confirm identical receipts, unchanged audit counts and settled worker jobs. Record elapsed time to this first authenticated comparison.
6. Reboot the rehearsal VM. Confirm a new boot ID, automatic service readiness and the same CA. Sign in again from the client and repeat the comparisons and replays. Stop the timer when those checks pass and manually power down the rehearsal VM. Record the elapsed time, archive size, guest resources and any image, package, tool or recovery materials available before timing began. A production recovery objective needs measurements at the expected production volume.

## Compatibility and maintenance

| Contract | Supported foundation baseline |
| --- | --- |
| Build and runtime | .NET 10 LTS SDK 10.0.404, Npgsql 10.0.4, PostgreSQL 18.7 baseline, Caddy 2.11.9 and age 1.3.2. The Workbench lockfile pins Vue 3 and the build/test dependencies. |
| Release set | Keep API, worker, Vue assets and migrations from the same build. HTTP v1, schema v2 and `record-reference` message v1 form the supported set. Schema history has immutable checksums. The API and worker reject incompatible schema. Unsupported jobs remain visible as blocked with no processing effect. |
| Upgrade or rollback | In an independent lab, preserve a pre-upgrade backup and release. Exercise the candidate's locked build, transactional migration, restart, same-command replay, older supported clients, unsupported messages and restore. A failed migration rolls back its transaction. Reverting to an incompatible binary requires an isolated restore with its matching release and reconciliation of subsequent facts. |
| Retirement | The Architect owns release compatibility and directs the monthly review, next due **2026-12-22**. Review security releases promptly, check support windows quarterly and retain the prior matching release until rollback and restore are qualified. Record patch pins, review results and any required contract change on the milestone issue. |

.NET 10 LTS is supported through November 14, 2028. PostgreSQL 18 has a published final release date of November 14, 2030. Node 24 is in maintenance LTS through April 30, 2028. The selected lines provide time to qualify patch updates and stage the next major release. Check upstream advisories for the driver, gateway, encryption and frontend tools during each review. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) · [PostgreSQL versioning](https://www.postgresql.org/support/versioning/) · [Node release schedule](https://github.com/nodejs/Release/blob/main/schedule.json)
