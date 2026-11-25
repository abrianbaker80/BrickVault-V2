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

Open **Inventory** and search for an item number, catalog name, purchase or delivery reference, bin code, build-hold ID or active project name. Filter by item type or bin. The matching position count and total units cover the whole result, including subsequent pages. Each row represents one physical position from a specific delivery. Open the row to inspect its current catalog identity, recorded revision, quantity, offer backing, bin and movement history.

A receipt cost applies to the entire delivery in its recorded currency. Two positions can show the same receipt cost when that delivery was split. An explicit zero and an unknown cost have different meanings. A bookmarked position remains addressable by ID after every unit moves out of its bin. [Inventory register qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/19)

### Read the position timeline

Open the position in **Inventory**. **All activity** lists events for that position from newest to oldest. The position balance and the events on each page come from the same database snapshot. The earliest stock entry identifies the original putaway or the transfer that brought the pieces into this bin. A transfer identifies the other position. Draft-offer backing and build reservations affect how many pieces are committed. A witnessed count records what the operator saw. A posted loss reduces the physical balance. Open its receipt to see the exact offer reductions and the reason for the posting. Choose **Transfers only** to trace the route between bins.

Each event carries the purchase revision that recorded it. This orders entries even when an older record has no timestamp. For example, a putaway of ten pieces followed by a count of five still represents ten on hand until the loss is posted. A five-piece loss then takes the position to five on hand. The count and the posting link to separate receipts. The position remains available by exact ID if its balance later reaches zero. [Position timeline qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/29)

### Follow a receipt lot across bins

From an Inventory position, open **Inspect receipt lot**. A lot follows one delivery, including every original putaway and subsequent transfer from it. Compare the quantity still awaiting putaway, the quantity in positions and any posted loss against the delivery receipt. The lot shows its acquisition cost once, with the original currency and any cost allocated to a posted loss. An unknown cost stays unknown.

Use the positions list to inspect a bin, including a position that has reached zero. A transfer appears once in lot activity and identifies its source, destination and moved backing. It keeps the lot-wide physical balance constant. If the purchase changes while you are paging through the lot, reload the first pages to see the new revision.

### Queue and complete a recount

In Inventory, select individual positions or use all matches for the active filters. Review the preview before confirming. The preview freezes the position IDs and revisions across pages. Positions that start matching after the preview belong to a new preview. The batch receipt shows which targets entered the queue, which changed, which already had a pending recount and which were unavailable.

Open the recount queue to locate a position, verify its bin and record the witnessed quantity through the existing stock-count workflow. The receipt links the queued position to that observation. If all units move out before the count, the queue identifies the depleted position and its history. Queuing a task preserves physical stock, offer backing and build holds. [Recount and lot qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/31)

## Witness a stock count and review a shortage

Use a fictional identified position in a registered bin. In the signed-in count workflow, choose the position, confirm its bin code and enter the counted quantity and reason. The count records the operator's observation and the ledger facts at the compared revision. A lower count opens a physical shortage for that position and blocks new offer backing and outbound transfers from it. Existing offers retain their original backing. A surplus has unidentified source and remains observation evidence. A later count does not close an open case. [Owner policy and qualification case](https://github.com/abrianbaker80/BrickVault-V2/issues/22)

The isolated backend slice has count observations and physical shortage cases. Position history is paged. The open case summary and direct receipt lookup remain available independently of which observation page is shown. A count does not post a movement, authorize sale, release an internal hold or create buyer demand. Unpublished offer backing is reported separately and is not order or hold evidence. The build-hold workflow below covers explicit release of an existing internal reservation. Buyer-obligation qualification awaits trusted order intake.

### Transaction and decision design

| Operation | Invariants and version checks |
|---|---|
| Observe and open a case | Validate the signed-in workspace and command digest. Lock the purchase and compare its expected revision before reading the positive position, registered bin and backing. Insert an immutable observation with the counted quantity, confirmed code, reason and compared ledger/backing quantities. Open at most one case per position in the same transaction as the observation, purchase revision, command, audit and job. A later observation advances the case's evidence frontier without changing its opening observation. Keep a resolved case and its closing command for audit, then open a new case for a later independent shortage. Retry the same command with the original digest. |
| Post an adjustment | The separate posting command must reference the actual reviewed count, open case, its expected evidence frontier and current purchase/position revision. Lock affected commitments in a stable order and recheck custody and quantities. Only an explicit posting records a ledger movement and the reviewed loss. Preserve original receipt and cost facts. A surplus requires separate unidentified intake evidence and a review before release. Preserve exact documented versus unknown cost and leave any exception to its owner-approved cost policy. |
| Allocate or release an existing hold | Start from stable authoritative hold and buyer-line identities and their revisions. Compare the latest case revision, allocation revisions and actual remaining backed/unbacked quantities. Record the exact owner-selected hold, quantity and decision reason. Hold release/reassignment, any resulting backing change, obligation shortage and audit commit together. A competing stale decision applies nothing, and a retry replays the stored receipt. Existing hold identity and unreleased quantity stay visible. Never automatically select a hold, invent demand, cancel a buyer obligation or report remaining negative units. |
| Confirm resolution | Keep the unbacked remainder open while any required quantity remains unresolved. A count, a posted loss and an allocation decision remain separately identifiable. Reconcile physical, unavailable, reserved and free stock, retained costs and all obligation quantities against their ledgers before closing. |

For the count slice, test a lower count, duplicate command and stale revision, typed bin mismatch, a subsequent unidentified surplus and rejected writes against the affected position. Read the original receipt cost and offer backing back from PostgreSQL. Before accepting the full D09 workflow, qualify two existing build holds, an identified customer requirement, exact partial owner release, a competing decision, exact replay and the unresolved remainder. Use fictional evidence until each real source has separate qualification.

## Reserve pieces for a project or release a hold

In **Locations**, open a bin and choose **Build holds** for the receipt layer. Check the ledger quantity, existing offer backing, project reservations and quantity available for a new hold. Choose **Create build hold**, name the project, confirm the physical bin code, and record the quantity and reason.

If a count shows fewer pieces than expected, follow **Review build holds for this shortage** from the count receipt. Compare the latest count with the ledger and select the named project's **Release pieces** button. Enter how many pieces to release and why. The project's release history links the decision to the witnessed count. The other projects retain their reservations. The shortage stays open for a separate ledger adjustment. Customer-order demand and allocation require trusted order intake in M4.

In an isolated workspace, receive ten pieces at a documented cost, back an unpublished offer with two, then hold three for one project and four for another. Count five. Verify that both reservations still show in full and new reservations are blocked. Release two pieces from the second project. Check that the first still holds three, the second holds two, the offer still backs two and the ledger still shows ten. Retry the saved release and verify its original receipt, then repeat the reads after encrypted restore. [Build hold qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/23)

## Review the effect of a count shortage

Open the count receipt and expand **Review stock adjustment**. Confirm the latest witnessed count and current ledger quantity. Check how many units would remain after a loss, how many are reserved for projects and which unpublished offers use this position. The receipt cost covers the entire delivery in its own currency. Unknown cost and a documented zero display separately.

For example, if a receipt position has six units, one reserved for a project and five backing two unpublished offers, a count of three leaves capacity for two units of offer backing. The review shows that three units of backing need a decision. Open another page of offers to identify all affected commitments. If another operator records a newer count, reload the review to see the revised quantities. Opening this review leaves the stock ledger, commitments and recorded costs intact.

## Post a witnessed stock loss

Open **Review stock adjustment** from the witnessed count. Compare the physical bin, receipt layer, latest count and project reservations. If project reservations by themselves exceed the pieces counted, first release the exact quantity you choose from the named hold. Return to the loss screen and review the new quantities.

Enter a reduction for each draft offer you want to change. Leave other offers at zero. For example, two listings backed by three and two blue plates need one unit of reduction after a count of four. Reducing the second listing by one leaves their backing at three and one. If there is enough stock for all existing offers, the required reduction is zero. Confirm the physical bin and enter the reason. One confirmation posts the loss, applies those choices and closes that physical shortage. A competing count or stock change requires a fresh review.

Reopen the posted receipt and compare the stock quantities, each selected offer and its remaining backing. The receipt identifies the witnessed count, posting reason and original delivery. Known item cost is allocated from that delivery in currency minor units; a documented zero remains zero and an unknown cost remains unknown. Pending purchase charges retain their separate status. Posting leaves the original delivery quantity and genuine intake quantity intact. Lost units do not become available for another putaway.

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

Run `python scripts/foundation/manage.py verify` after a clean build. The qualification uses the normal private HTTPS API, PostgreSQL and Workbench. The restore check compares the purchase and both deliveries, their placements, offer backing, reference audit and original command receipts. A schema-2 backup still requires its archived release. Schema-3 backups also fingerprint the five receiving tables. Schema-4 backups add position identities, transfer events and transferred offer backing. Schema 5 adds count observations and shortage cases. Schema 6 adds named build holds and their release history. Schema 7 adds posted stock losses and the chosen offer reductions. Schema 8 adds frozen recount previews, their target positions, queued batches and per-target receipts. Each archive requires its matching release. [Setup and recovery](m2-foundation.md)
