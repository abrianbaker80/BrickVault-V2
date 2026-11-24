# Receiving a split purchase

Open **Receiving** in the signed-in Workbench. Use an isolated V2 instance and fictional inventory. Create an eligible catalog reference and two distinct locations in **Catalog** and **Locations**. The purchase, deliveries, placements and offer share PostgreSQL with the reference register.

1. Record a purchase reference, source, currency and expected unit count. Record a purchase charge separately when it still needs allocation.
2. Count each delivery into intake. Record its own reference, quantity and evidenced item cost. Leave the cost unknown when it has no evidence. An unidentified delivery stays in intake until identification and physical putaway.
3. Select the delivery, item and destination. Enter the actual bin code and quantity. Each confirmation retains its source delivery, including when only part of the delivery is ready.
4. Set a manual unit price and select how many free units each placement will back. The preview records an unpublished offer. Backing the offer leaves physical custody unchanged.

The reference case receives eight identified units at a total item cost of USD 8.00 and four units with unknown item cost. Eight go into B-12, three into C-03, and one remains in intake. Of eleven free units, eight have documented item cost. USD 0.60 remains a separate unapplied charge. An internal offer can use five units from B-12 and two from C-03 at USD 1.50 each, leaving four free units withheld. The case exercises an incorrect bin, replay after restart, overcommitted backing and recovery from an encrypted backup. [Issue #13](https://github.com/abrianbaker80/BrickVault-V2/issues/13)

## Stage a schema upgrade

Build the current release from the checkout. Stop the M2 instance and use a new output directory with room for another complete cluster. The command copies the stopped cluster, operator credentials, workspace identity and established certificate authority into private staging. It applies the candidate migrations and database grants, compares the original business records and verifies the new receiving tables are empty. A successful run exposes the stopped candidate at `<output>/instance`. Preserve the original instance and its matching encrypted backup.

```text
python scripts/foundation/manage.py build
python scripts/foundation/manage.py stop --state <old-instance>
python scripts/foundation/manage.py stage-upgrade --state <old-instance> --output <new-private-directory>
python scripts/foundation/manage.py start --state <new-private-directory>/instance
```

For a systemd-managed guest, stop its Workbench unit before staging. An explicit `--release` can select an already-built application directory for the same operating system and architecture. The old and staged instances use the same ports. Start one at a time. Before accepting operator writes, verify sign-in, the original facts and one original command receipt. To roll back before new writes, stop the staged instance and start the original. After new writes, reconcile those changes before switching databases.

## Repeat verification

Run `python scripts/foundation/manage.py verify` after a clean build. The qualification uses the normal private HTTPS API, PostgreSQL and Workbench. The restore check compares the purchase and both deliveries, their placements, offer backing, reference audit and original command receipts. A schema-2 backup still requires its archived release. A schema-3 backup also fingerprints the five receiving tables. [Setup and recovery](m2-foundation.md)
