# Hosted Workbench backup and recovery

The Workbench guest exports a PostgreSQL snapshot and its matching application release. The guest encrypts the portable recovery point to the operator's age public key. The Proxmox controller checks the source identity, size and SHA-256, uploads the ciphertext through its existing encrypted rclone profile, downloads the cloud object into fresh storage and checks its SHA-256 again. Each point has a unique name in `BrickVault-V2/`. The recovery identity lives on a physically separate machine.

## Commission a controller

1. Install the foundation Python modules under `/opt/brickvault-v2-backup/scripts/foundation` on the controller. Run `python3 -c 'import setup; setup.rclone_program()'` from that directory as root to install the checksum-pinned transfer binary. Create owner-only `/etc/brickvault-v2-backup` and `/var/lib/brickvault-v2-backup`. Keep private credentials and configuration outside the source checkout.
2. Create a dedicated Ed25519 capture key on the controller. Obtain the guest's SSH host public key through its trusted management channel and pin it in a private known-hosts file under a fixed alias. On the guest, authorize only the capture key for its application service account. Use the controller's private source address and the exact installed capture command. For example, the authorized-key options are `from="<controller-IP>",restrict,command="/usr/bin/python3 /opt/brickvault-v2/scripts/foundation/nightly.py stream --state /var/lib/brickvault-v2/instance"`. The application's service account needs permission to read its state, operate the local database and create temporary files in the instance directory.
3. Configure the protected transport JSON with `remote`, `config` and `password_command`. Use the existing Google Drive remote alias, encrypted configuration file and approved credential loader. The controller copies the encrypted profile to a private temporary directory for each transfer. Restrict the descriptor and capture key to the controller service.
4. Create `/etc/brickvault-v2-backup/controller.json` using the settings below. Obtain the VM identity through the hypervisor and the instance and workspace IDs from the freshly initialized guest. Record only the expected identifiers. Keep the instance's private configuration on the guest.
5. To enable a workstation mirror, create a dedicated unprivileged SSH account on the controller with a usable shell, disabled password, and its own forced-command key. Create a root-owned export directory with group read/traverse permissions on the same filesystem as controller state. The example unit grants writes to `/var/lib/brickvault-v2-mirror-export`. Add the `mirrorExport` setting shown below. The service hard-links the verified encrypted archive into this export.
6. Install the four `brickvault-v2-backup*` units from `scripts/foundation/systemd`, check them with `systemd-analyze verify`, and reload systemd. Run one capture and validate the resulting point with the commands below. Enable both timers.

| Controller setting | Value |
| --- | --- |
| `vmId`, `guestMac`, `privateNetwork` | Authorized VM number, NIC MAC and private IPv4 CIDR. QEMU guest agent reports its current address. |
| `guestAccount`, `guestKey` | Guest service account and absolute path to the controller's dedicated capture key. |
| `knownHosts`, `hostKeyAlias` | Pinned guest host-key file and its exact alias. |
| `sourceId`, `workspaceId` | Expected source and workspace UUIDs. |
| `transport`, `controllerState` | Absolute paths to the protected transport descriptor and private controller state. |
| `maxBytes`, `reserveBytes` | Numeric archive size cap and free disk reserve. Capturing requires room for two copies in addition to the reserve. |
| `freshnessHours` | Maximum permitted capture age, e.g. 30 hours for the nightly schedule. |
| `mirrorExport` | Optional object with absolute `directory` and the dedicated reader's `group`. |

```sh
systemctl start brickvault-v2-backup.service
systemctl start brickvault-v2-backup-freshness.service
systemctl enable --now brickvault-v2-backup.timer brickvault-v2-backup-freshness.timer
systemctl list-timers --all 'brickvault-v2-backup*'
journalctl -u brickvault-v2-backup -u brickvault-v2-backup-freshness
```

Capture runs daily at 02:15 America/Chicago, or 03:15 on the spring clock-change Sunday. The persistent calendar timer runs missed work after boot. The hourly freshness check reads the last capture time and provider verification. It exits unsuccessfully for an overdue capture or a failed latest attempt. The controller keeps the last success and current stage in its private `status.json`. An interrupted publication retains the encrypted point and retries against its original source and provider. The controller's service retries until its start limit. After resolving a fault, use `systemctl reset-failed brickvault-v2-backup.service` followed by `systemctl start brickvault-v2-backup.service`. Review space and retained points as they grow. This profile keeps published points until the operator applies a separate retention decision.

### Before a change

Run the following as the controller operator before a migration, upgrade or other planned change. It captures and verifies a separate point even when today's timer has already completed. Use the printed point as the change's recovery reference. If another capture holds the lock, let it finish and rerun the command. The scheduled run still deduplicates completed work from the same day.

```sh
python3 /opt/brickvault-v2-backup/scripts/foundation/nightly.py run --before-change \
  --config /etc/brickvault-v2-backup/controller.json
```

## Workstation catch-up

Give the Windows workstation dedicated Ed25519 credentials and a pinned SSH configuration for the alias `brickvault-v2-backup-mirror`. The remote account can read final V2 ciphertext and receipts in the export directory. Restrict the authorized key to the private workstation network and the forced command `/usr/bin/python3 /opt/brickvault-v2-backup/scripts/foundation/backup_mirror.py serve --directory /var/lib/brickvault-v2-mirror-export`.

Use an owner-only local JSON file with `state` (a dedicated private directory), `mirror` (an empty or previously initialized V2 directory in the OneDrive sync root) and `sshConfig` (the private OpenSSH configuration path). Run `python scripts/foundation/backup_mirror.py sync --config <private-file>` once. Register the installed client with `pwsh ./scripts/foundation/Install-BackupMirror.ps1 -ConfigPath <private-file> -ClientPath <installed-backup_mirror.py>`. The owner-scoped task runs at 03:05 workstation-local time and at sign-in, catches missed starts and retries temporary failures. Each run retrieves missing points, checks each new file against its trusted receipt, and records the latest locally verified point under `state/status.json`. Run `python scripts/foundation/backup_mirror.py status --config <private-file>` to see the latest attempt, last verified point and its capture freshness. A failed attempt preserves the last success and returns a failing status, even when that point is still fresh. OneDrive reports its cloud synchronization separately.

## Prove recovery on another Linux machine

Use a fresh controller directory for every download. The actual archive is fetched again from the configured cloud destination and checked against the controller's independently retained receipt.

```sh
python3 /opt/brickvault-v2-backup/scripts/foundation/nightly.py download \
  --config /etc/brickvault-v2-backup/controller.json --output "$fresh_download"
```

Transfer the downloaded archive and receipt to private recovery storage. On a separate supported Linux machine, install the matching foundation tools. Set `$point` to the downloaded point name, `$download` to its directory and `$identity` to the independently held age identity. Choose a fresh instance directory, a separate recovery-key directory and three available consecutive loopback ports.

```sh
python3 scripts/foundation/offhost.py recover \
  --receipt "$download/$point.json" --file "$download/$point.age" --identity "$identity" \
  --state "$fresh_state" --recovery "$fresh_keys" --port "$first_port"
python3 scripts/foundation/manage.py start --state "$fresh_state"
```

Use the recovered instance's new owner login to compare the expected records and audit evidence. Keep customer data, key material, private routes, receipt values and real host identifiers out of public records. The controller's fault/retry and freshness regression checks run with `python scripts/foundation/verify_nightly.py`.
