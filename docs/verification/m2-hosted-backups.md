# Hosted Workbench backup and recovery

The Workbench guest exports a PostgreSQL snapshot and its matching application release. The guest encrypts the portable recovery point to the operator's age public key. The Proxmox controller checks the source identity, size and SHA-256, uploads the ciphertext through its existing encrypted rclone profile, downloads the cloud object into fresh storage and checks its SHA-256 again. Each point has a unique name in `BrickVault-V2/`. The recovery identity lives on a physically separate machine.

## Commission a controller

1. Install the foundation Python modules under `/opt/brickvault-v2-backup/scripts/foundation` on the controller. Run `python3 -c 'import setup; setup.rclone_program()'` from that directory as root to install the checksum-pinned transfer binary. Create owner-only `/etc/brickvault-v2-backup` and `/var/lib/brickvault-v2-backup`. Keep private credentials and configuration outside the source checkout.
2. Create a dedicated Ed25519 capture key on the controller. Obtain the guest's SSH host public key through its trusted management channel and pin it in a private known-hosts file under a fixed alias. On the guest, authorize only the capture key for its application service account. Use the controller's private source address and the exact installed capture command. For example, the authorized-key options are `from="<controller-IP>",restrict,command="/usr/bin/python3 /opt/brickvault-v2/scripts/foundation/nightly.py stream --state /var/lib/brickvault-v2/instance"`. The application's service account needs permission to read its state, operate the local database and create temporary files in the instance directory.
3. Configure the protected transport JSON with `remote`, `config` and `password_command`. Use the existing Google Drive remote alias, encrypted configuration file and approved credential loader. The controller copies the encrypted profile to a private temporary directory for each transfer. Restrict the descriptor and capture key to the controller service.
4. Create `/etc/brickvault-v2-backup/controller.json` using the settings below. Obtain the VM identity through the hypervisor and the instance and workspace IDs from the freshly initialized guest. Record only the expected identifiers. Keep the instance's private configuration on the guest.
5. To enable a workstation mirror, create a dedicated unprivileged SSH account on the controller with a usable shell, disabled password, and its own forced-command key. Create a root-owned export directory with group read/traverse permissions on storage with room for an independent encrypted copy. The example unit grants writes to `/var/lib/brickvault-v2-mirror-export`. Add the `mirrorExport` setting shown below. The service makes a separate verified copy in this export so damage to exported bytes cannot alter the controller's archive.
6. Install the four `brickvault-v2-backup*` units from `scripts/foundation/systemd`, check them with `systemd-analyze verify`, and reload systemd. Run one capture and validate the resulting point with the commands below. Enable both timers.

| Controller setting | Value |
| --- | --- |
| `vmId`, `guestMac`, `privateNetwork` | Authorized VM number, NIC MAC and private IPv4 CIDR. QEMU guest agent reports its current address. |
| `guestAccount`, `guestKey` | Guest service account and absolute path to the controller's dedicated capture key. |
| `knownHosts`, `hostKeyAlias` | Pinned guest host-key file and its exact alias. |
| `sourceId`, `workspaceId` | Expected source and workspace UUIDs. |
| `receiptSigningKey`, `receiptTrust` | Dedicated OpenSSH Ed25519 private signing key and independently pinned public-key/lineage JSON. At rotation, commission the combined history on the controller and each workstation and retain a copy in recovery custody. [Receipt signing-key rotation](m2-cloud-receipts.md#rotate-the-controller-signing-key) |
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

Use an owner-only local JSON file for the scheduled task. Set `state` to a dedicated private directory, `mirror` to the `BrickVault V2 Backup` folder in the OneDrive Personal sync root, and `sshConfig` to the pinned OpenSSH configuration. Set `receiptTrust` to the public trust file held independently and `oneDriveProfile` to an owner-only directory outside OneDrive. Keep the installed foundation scripts together.

Enroll the reader once from the same Windows account that will run the task. The command opens the personal Microsoft account sign-in page. The reader requests `Files.Read` for downloads and `offline_access` for renewals. The enrollment checks both permissions before opening the page. The OneDrive desktop client handles uploads. The reader uses its own encrypted rclone configuration with a Windows Data Protection API password bound to this Windows user. Restore a lost profile by signing in again. [Microsoft Graph file download permissions](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0) · [rclone OneDrive configuration](https://rclone.org/onedrive/)

```text
python scripts/foundation/onedrive_reader.py connect --profile READER_DIRECTORY
python scripts/foundation/backup_mirror.py sync --config PRIVATE_SETTINGS.json
pwsh ./scripts/foundation/Install-BackupMirror.ps1 -ConfigPath PRIVATE_SETTINGS.json -ClientPath INSTALLED_BACKUP_MIRROR.py
python scripts/foundation/backup_mirror.py status --config PRIVATE_SETTINGS.json
```

The owner-scoped task runs at 03:05 workstation-local time and at sign-in and catches missed starts. It verifies the controller's signature and the archive SHA-256 for every mirrored point. It preserves damaged archives in private quarantine before repair and leaves conflicting receipts for operator review. The client then downloads each pair awaiting verification and the latest pair from Microsoft into fresh private storage.

The desktop sync client can still be uploading when the first readback starts. The reader authenticates the signed receipt and compares its metadata with the controller before requesting the archive. It then checks the archive size and SHA-256. The first pass checks each independent point. For missing provider files or temporary transfer errors, the same process retries only the outstanding points after 1, 3, 7, 15, 30 and 60 minutes. All retries in one invocation share a two-hour window measured from the start of its first provider pass. Each provider command during retries is limited to its remaining time. Credential, signature and integrity failures require attention immediately. A point still unavailable after the window leaves the task failed. [rclone exit codes](https://rclone.org/docs/#list-of-exit-codes)

Windows Task Scheduler separately supports missed starts and up to three restart attempts at 15-minute intervals. Each new process, including one launched at sign-in, consults the saved proofs and has its own retry window. Workstation checks read existing recovery points. The controller creates the scheduled backup once each night.

During a wait, `status` shows `waiting-onedrive`, the number of pending readbacks and the next attempt time. `cloudPoint` and `cloudVerifiedAt` identify the latest authenticated OneDrive point for the enrolled reader, source and workspace. A controller listing containing duplicate points or ending before a previously authenticated controller point requires investigation, including when its upload was pending. Removing older points under retention still permits verification of the current latest point. A newly enrolled reader establishes its own latest point by downloading and verifying provider bytes. Older proof files gain the lineage binding on their next authenticated readback. `cloudFresh` becomes true when the current run completes verification and the point meets the configured freshness window.

On upgrade, the workstation authenticates the last recorded local point against the independently held signing trust and verifies its archive. A controller that still offers the point can supply its signed receipt during catch-up. A previous OneDrive verification also requires its original private readback proof with a matching digest and reader enrollment. If these checks fail, restore the original evidence and trust for that source. When commissioning a separate source or workspace, preserve the old state, archives and trust in custody. Configure a fresh workstation `state` directory and the new independently verified `receiptTrust`. Use the intended account's enrolled reader and synced `BrickVault V2 Backup` folder, then run catch-up and check `cloudFresh` for the new source.

For exports finalized before the controller recorded a separate mirror binding, a controller administrator can assign their independently checked publication-context digests in `priorMirrorExports` to the original normalized absolute export path and reader group as a two-item array. The migration checks the original root-owned export, reader group, receipt and existing ciphertext before copying the signed sidecar. A changed target requires separate authorization.

## Prove recovery on another Linux machine

On a separate supported Linux machine, install the matching foundation tools. Use the public signing-key and lineage file held in recovery custody, together with independent provider access. The following command discovers the newest signed point in the provider and authenticates the returned archive and signed receipt in a fresh private directory. For a manual provider download, the same files can be verified offline. [Signed cloud recovery instructions](m2-cloud-receipts.md)

```sh
python3 scripts/foundation/cloud_receipt.py download \
  --transport "$recovery_transport" --trust "$public_trust" --output "$download"
```

Set `$point` to the authenticated point name and `$identity` to the independently held age identity. Choose a fresh instance directory, a separate recovery-key directory and three available consecutive loopback ports.

```sh
python3 scripts/foundation/offhost.py recover \
  --receipt "$download/$point.json" --file "$download/$point.age" --identity "$identity" \
  --state "$fresh_state" --recovery "$fresh_keys" --port "$first_port"
python3 scripts/foundation/manage.py start --state "$fresh_state"
```

Use the recovered instance's new owner login to compare the expected records and audit evidence. Keep customer data, key material, private routes, receipt values and real host identifiers out of public records. The controller's fault/retry and freshness regression checks run with `python scripts/foundation/verify_nightly.py`.
