# Receiving a split purchase

Open **Receiving** in the signed-in Workbench. Use an isolated V2 instance and fictional inventory. Create an eligible catalog reference and two distinct locations in **Catalog** and **Locations**. The purchase, deliveries, placements and offer share PostgreSQL with the reference register.

1. Record a purchase reference, source, currency and expected unit count. Record a purchase charge separately when it still needs allocation.
2. Count each delivery into intake. Record its own reference, quantity and evidenced item cost. Leave the cost unknown when it has no evidence. An unidentified delivery stays in intake until identification and physical putaway.
3. Select the delivery, item and destination. Enter the actual bin code and quantity. Each confirmation retains its source delivery, including when only part of the delivery is ready.
4. Set a manual unit price and select how many free units each placement will back. The preview records an unpublished offer. Backing the offer leaves physical custody unchanged.

The reference case receives eight identified units at a total item cost of USD 8.00 and four units with unknown item cost. Eight go into B-12, three into C-03, and one remains in intake. Of eleven free units, eight have documented item cost. USD 0.60 remains a separate unapplied charge. An internal offer can use five units from B-12 and two from C-03 at USD 1.50 each, leaving four free units withheld. The case exercises an incorrect bin, replay after restart, overcommitted backing and recovery from an encrypted backup. [Issue #13](https://github.com/abrianbaker80/BrickVault-V2/issues/13)

## Inspect a storage bin

In **Locations**, open the bin to see its confirmed stock. Each placement retains its purchase and delivery. The inspector shows the whole delivery’s documented cost and currency, or marks the cost unknown. One delivery may occupy several bins. Adding its displayed cost across placements would count the same evidence more than once. Backed and unbacked quantities reconcile to the on-hand count. The amount eligible for new backing also depends on the recorded catalog revision and its current eligibility.

## Transfer stock between bins

Open the source bin in **Locations** and select the receipt layer that physically moved. Choose the destination and enter both bin codes, the number moved and the reason. When the pieces back an offer, specify how many units of each offer move with them. The sum of the chosen backing and the unbacked units must fit the layer at the source. A transfer retains the recorded catalog revision, original delivery and cost basis.

Check the source and destination after saving. Transfers appear in both bins’ movement history with the same command receipt. A later move from the destination keeps the original delivery. The purchase's physical, quarantined, reserved and free totals and its offer quantities stay consistent across the move. [Transfer qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/18)

## Locate a stock position

Open **Inventory** and search for an item number, catalog name, purchase or delivery reference, bin code or record ID. Filter by item type or bin. The matching position count and total units cover the whole result, including subsequent pages. Each row represents one physical position from a specific delivery. Open the row to inspect its current catalog identity, recorded revision, quantity, offer backing, bin and movement history.

A receipt cost applies to the entire delivery in its recorded currency. Two positions can show the same receipt cost when that delivery was split. An explicit zero and an unknown cost have different meanings. A bookmarked position remains addressable by ID after every unit moves out of its bin. [Inventory register qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/19)

## Stage a schema upgrade

Build the current release from the checkout. Stop the source instance and use a new output directory with room for another complete cluster. The command copies the stopped cluster, operator credentials, workspace identity and established certificate authority into private staging. It applies the candidate migrations and database grants, compares the original business records and verifies the opening stock and offer backing in both directions against the receiving records. Existing positions retain their IDs. Transferring stock requires schema 4. A successful run exposes the stopped candidate at `<output>/instance`. Preserve the original instance and its matching encrypted backup.

```text
python scripts/foundation/manage.py build
python scripts/foundation/manage.py stop --state <old-instance>
python scripts/foundation/manage.py stage-upgrade --state <old-instance> --output <new-private-directory>
python scripts/foundation/manage.py start --state <new-private-directory>/instance
```

For a systemd-managed guest, stop its Workbench unit before staging. An explicit `--release` can select an already-built application directory for the same operating system and architecture. The old and staged instances use the same ports. Start one at a time. Before accepting operator writes, verify sign-in, the original facts and one original command receipt. To roll back before new writes, stop the staged instance and start the original. After new writes, reconcile those changes before switching databases.

## Repeat verification

Run `python scripts/foundation/manage.py verify` after a clean build. The qualification uses the normal private HTTPS API, PostgreSQL and Workbench. The restore check compares the purchase and both deliveries, their placements, offer backing, reference audit and original command receipts. A schema-2 backup still requires its archived release. Schema-3 backups also fingerprint the five receiving tables. Schema-4 backups add position identities, transfer events and transferred offer backing. Each archive requires its matching release. [Setup and recovery](m2-foundation.md)
