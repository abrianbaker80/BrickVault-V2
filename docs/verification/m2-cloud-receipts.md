# Recover a signed cloud point

The controller signs a receipt after it uploads an encrypted point and verifies its downloaded bytes. The signed receipt records the point, archive size and SHA-256, source instance, workspace and publication context. It uses the OpenSSH Ed25519 signature namespace `brickvault-v2-cloud-receipt-v1`. The controller uploads the signed receipt using immutable copy and verifies the provider's returned copy. A completed point has two provider objects named `<point>.age` and `<point>.receipt.json`.

Keep the private signing key on the controller. Before relying on the backups, copy the **public** trust file through an independently authenticated channel to recovery custody. It contains `publicKey` (an OpenSSH Ed25519 public key with its type and base64 fields), `sourceId` and `workspaceId`. Preserve that trusted copy independently of both the controller and the backup provider. Recovery also requires the separately held age decryption identity. When rotating the controller signing key, deliver its new public key over the independently authenticated channel. Keep older trust files for points made with earlier keys.

## When the controller is unavailable

Use a clean machine with this release, Python and OpenSSH `ssh-keygen`. Download the latest pair from the `BrickVault-V2` provider folder using independent provider access. Use the capture timestamp at the start of the point identifier to check its age against the expected recovery window. If a newer point fails authentication or is missing one of its objects, record that failure before choosing an older recovery point.

For a manual provider download, place the two files in the same private directory. Supply the separately held trust file. This command runs entirely offline.

```text
python scripts/foundation/cloud_receipt.py verify --trust TRUST.json --directory DOWNLOAD_DIR --point POINT
```

Alternatively, a recovery host with its own authenticated and encrypted rclone transport can discover the newest published signed point, download both files, and verify them in a new directory.

```text
python scripts/foundation/cloud_receipt.py download --transport TRANSPORT.json --trust TRUST.json --output NEW_DOWNLOAD_DIR
```

Use `--point POINT` with the download command to select a specific capture. Both commands authenticate the receipt against the independently held key and expected lineage, then check the actual archive size and SHA-256. On success they produce the trusted `<point>.json` used by `offhost.py recover`. Supply it with `--receipt`, the matching encrypted archive with `--file`, and the age identity with `--identity` during the restore.
