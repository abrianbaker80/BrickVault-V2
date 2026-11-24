# Recover a signed cloud point

A completed recovery point has an encrypted archive named `<point>.age` and a signed receipt named `<point>.receipt.json`. The receipt authenticates the point name, archive size and SHA-256, source instance, workspace and publication context. The controller signs with OpenSSH Ed25519 in the namespace `brickvault-v2-cloud-receipt-v1`. Google Drive and OneDrive can each supply the pair for recovery.

The private signing key stays on the controller. Keep the **public** trust file and the separately held age decryption identity in recovery custody. At commissioning, independently authenticate the controller's Ed25519 public key, source ID and workspace ID. Put those values in the trust file as `publicKey`, `sourceId` and `workspaceId`. Keep the age identity and the reader's encrypted credential profile outside both sync folders.

## Rotate the controller signing key

1. Let pending publications finish under the outgoing key. Choose the first UTC capture second for the incoming key after the last old-key capture. The timestamp prefix of a recovery point gives its capture second.
2. Obtain the incoming public key through a channel that independently authenticates the controller. Confirm the commissioned source and workspace IDs. Use those three fields to create `NEW_PIN.json` in protected custody. Keep the existing trust file for the full retention period.
3. Prepare a combined trust file in a fresh location. Supply the cutover in `YYYYMMDDTHHMMSSZ` format. The command preserves earlier signing periods and checks that the incoming pin belongs to the same source and workspace.

   ```text
   python scripts/foundation/cloud_receipt.py rotate-trust --trust CURRENT_TRUST.json --new-pin NEW_PIN.json --cutover CUTOVER_UTC --output NEXT_TRUST.json
   ```

4. Distribute the combined file through the independently authenticated commissioning channel to the controller, workstation mirror and recovery custody. Configure each reader's `receiptTrust` and use the combined file for recovery `--trust`. Switch the controller to the new private signing key before its first capture at or after the cutover.
5. Verify a retained point and the first new point, then run workstation catch-up. A workstation enrolled after a rotation needs the complete authenticated trust history. A separately commissioned source or workspace gets its own trust configuration.

The top-level pin applies from the last cutover onward. The optional `previous` array holds the earlier public keys and their expected source and workspace IDs, oldest first. Each entry has an exclusive `before` UTC timestamp. The signed point identifier determines exactly one signing period. A retired key remains trusted for captures dated within its authorized historical period, including backdated signatures from that key. If a retired key is suspected compromised, compare the point and archive digest with independently retained evidence before recovery.

## Download from either provider

On a clean machine with this release, Python and OpenSSH, choose the latest complete capture within the expected recovery window. A failed authentication or a missing file warrants investigation before selecting an older point.

For Google Drive, use the `BrickVault-V2` folder. A recovery host with an independent encrypted rclone transport can discover the newest signed point and download both objects into a new private directory.

```text
python scripts/foundation/cloud_receipt.py download --transport TRANSPORT.json --trust TRUST.json --output NEW_DOWNLOAD_DIR
```

For OneDrive Personal, use the `BrickVault V2 Backup` folder. The Windows backup owner's enrolled read-only reader fetches both objects directly from Microsoft into an empty directory outside the sync root. It authenticates the signature with the separately held trust file and verifies the downloaded ciphertext size and SHA-256.

```text
python scripts/foundation/onedrive_reader.py download --profile READER_DIRECTORY --trust TRUST.json --point POINT --output NEW_DOWNLOAD_DIR
```

From either provider's website, an owner can also download the two objects into a fresh private directory and verify them offline. This path works when the original controller or workstation is unavailable.

```text
python scripts/foundation/cloud_receipt.py verify --trust TRUST.json --directory DOWNLOAD_DIR --point POINT
```

Successful verification emits the trusted `<point>.json`. Supply it with `--receipt`, the downloaded archive with `--file`, and the separately held age decryption identity with `--identity` to `offhost.py recover`.
