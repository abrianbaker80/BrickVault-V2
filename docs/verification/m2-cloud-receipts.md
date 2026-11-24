# Recover a signed cloud point

A completed recovery point has an encrypted archive named `<point>.age` and a signed receipt named `<point>.receipt.json`. The receipt authenticates the point name, archive size and SHA-256, source instance, workspace and publication context. The controller signs with OpenSSH Ed25519 in the namespace `brickvault-v2-cloud-receipt-v1`. Google Drive and OneDrive can each supply the pair for recovery.

The private signing key stays on the controller. Keep the **public** trust file and the separately held age decryption identity in recovery custody. The public trust file contains the signing key, source ID and workspace ID. Deliver it through a channel that independently authenticates the controller. When rotating the signing key, retain the earlier public trust files for older points. The scheduled cloud backup contains encrypted data and public evidence. Keep the age identity and the reader's encrypted credential profile outside both sync folders.

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
