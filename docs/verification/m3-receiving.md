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

## Witness a stock count and review a shortage

Use a fictional identified position in a registered bin. In the signed-in count workflow, choose the position, confirm its bin code and enter the counted quantity and reason. The count records the operator's observation and the ledger facts at the compared revision. A lower count opens a physical shortage for that position and blocks new offer backing and outbound transfers from it. Existing offers retain their original backing. A surplus has unidentified source and remains observation evidence. A later count does not close an open case. [Owner policy and qualification case](https://github.com/abrianbaker80/BrickVault-V2/issues/22)

The isolated backend slice has count observations and physical shortage cases. A count does not post a movement, authorize sale, release an internal hold or create buyer demand. Unpublished offer backing is reported separately and is not order or hold evidence. Qualification of the existing-hold and buyer-obligation case still requires the corresponding authority and commands.

### Transaction and decision design

| Operation | Invariants and version checks |
|---|---|
| Observe and open a case | Validate the signed-in workspace and command digest. Lock the purchase and compare its expected revision before reading the positive position, registered bin and backing. Insert an immutable observation with the counted quantity, confirmed code, reason and compared ledger/backing quantities. Open at most one case per position in the same transaction as the observation, purchase revision, command, audit and job. A later observation advances the case's evidence frontier without changing its opening observation. Retry the same command with the original digest. |
| Post an adjustment | The separate posting command must reference the actual reviewed count, open case, its expected evidence frontier and current purchase/position revision. Lock affected commitments in a stable order and recheck custody and quantities. Only an explicit posting records a ledger movement and the reviewed loss. Preserve original receipt and cost facts. A surplus requires separate unidentified intake evidence and a review before release. Preserve exact documented versus unknown cost and leave any exception to its owner-approved cost policy. |
| Allocate or release an existing hold | Start from stable authoritative hold and buyer-line identities and their revisions. Compare the latest case revision, allocation revisions and actual remaining backed/unbacked quantities. Record the exact owner-selected hold, quantity and decision reason. Hold release/reassignment, any resulting backing change, obligation shortage and audit commit together. A competing stale decision applies nothing, and a retry replays the stored receipt. Existing hold identity and unreleased quantity stay visible. Never automatically select a hold, invent demand, cancel a buyer obligation or report remaining negative units. |
| Confirm resolution | Keep the unbacked remainder open while any required quantity remains unresolved. A count, a posted loss and an allocation decision remain separately identifiable. Reconcile physical, unavailable, reserved and free stock, retained costs and all obligation quantities against their ledgers before closing. |

For the count slice, test a lower count, duplicate command and stale revision, typed bin mismatch, a subsequent unidentified surplus and rejected writes against the affected position. Read the original receipt cost and offer backing back from PostgreSQL. Before accepting the full D09 workflow, qualify two existing build holds, an identified customer requirement, exact partial owner release, a competing decision, exact replay and the unresolved remainder. Use fictional evidence until each real source has separate qualification.

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
