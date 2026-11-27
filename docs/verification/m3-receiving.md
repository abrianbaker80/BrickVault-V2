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

Open **Inventory** and search for an item number, catalog name, purchase or delivery reference, bin code, build-hold ID or active project name. Enter a complete registered alternate or former label to find stock still in that bin, including after archive. Text search also matches other stock details. Choose the bin filter to limit results to one shelf, and use the item type filter to narrow them further. **All matching positions** in a recount uses the same filters across every page. The matching position count and total units cover the whole result, including subsequent pages. Each row represents one physical position from a specific delivery. Open the row to inspect its current catalog identity, recorded revision, quantity, offer backing, bin and movement history.

A receipt cost applies to the entire delivery in its recorded currency. Two positions can show the same receipt cost when that delivery was split. An explicit zero and an unknown cost have different meanings. A bookmarked position remains addressable by ID after every unit moves out of its bin. [Inventory register qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/19)

### Read the position timeline

Open the position in **Inventory**. **All activity** lists events for that position from newest to oldest. The position balance and the events on each page come from the same database snapshot. The earliest stock entry identifies the original putaway or the transfer that brought the pieces into this bin. A transfer identifies the other position. Draft-offer backing and build reservations affect how many pieces are committed. A witnessed count records what the operator saw. A posted loss reduces the physical balance. Open its receipt to see the exact offer reductions and the reason for the posting. Choose **Transfers only** to trace the route between bins.

Each event carries the purchase revision that recorded it. This orders entries even when an older record has no timestamp. For example, a putaway of ten pieces followed by a count of five still represents ten on hand until the loss is posted. A five-piece loss then takes the position to five on hand. The count and the posting link to separate receipts. The position remains available by exact ID if its balance later reaches zero. [Position timeline qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/29)

### Follow a receipt lot across bins

From an Inventory position, open **Inspect receipt lot**. A lot follows one delivery, including every original putaway and subsequent transfer from it. Compare the quantity still awaiting putaway, the quantity in positions and any posted loss against the delivery receipt. The lot shows its acquisition cost once, with the original currency and any cost allocated to a posted loss. An unknown cost stays unknown.

Use the positions list to inspect a bin, including a position that has reached zero. A transfer appears once in lot activity and identifies its source, destination and moved backing. It keeps the lot-wide physical balance constant. If the purchase changes while you are paging through the lot, reload the first pages to see the new revision.

### Queue and complete a recount

In Inventory, select individual positions or use all matches for the active filters. Review the preview before confirming. The preview freezes the position IDs and revisions across pages. Positions that start matching after the preview belong to a new preview. The batch receipt shows which targets entered the queue, which changed, which already had a pending recount and which were unavailable.

Open the recount queue to locate a position, verify its bin and record the witnessed quantity through the existing stock-count workflow. The receipt links the queued position to that observation. If all units move out before anyone counts them, the task leaves the pending queue. Its batch receipt identifies the depleted position and links to its history. Queuing a task preserves physical stock, offer backing and build holds. [Recount and lot qualification](https://github.com/abrianbaker80/BrickVault-V2/issues/31)

## Quarantine pieces found after putaway

Open the affected receipt position and confirm its physical bin, item, quantity and reason. Quarantine keeps the pieces in that bin and removes them from the quantity available for new offers, project holds and ordinary transfers. The receipt identifies the original delivery and the catalog revision used by the operator.

For example, a bin contains eight pieces from one delivery. Two are reserved for a build and three back an unpublished offer. Quarantining two of the remaining pieces leaves eight physically present, two unavailable and two reserved. Four are eligible for sale, including the three already allocated to the draft offer. That leaves one piece for a new commitment.

After inspecting one of the two pieces, reopen its quarantine record, confirm its identity and bin, and release the one that passed. One piece stays in quarantine and two are now available for a new commitment. Both events remain in the position timeline and receipt-lot history. The original delivery quantity, currency and cost basis stay attached to the pieces. [Quarantine and inspection](https://github.com/abrianbaker80/BrickVault-V2/issues/33)

## Inspect pieces recovered after a posted loss

Open the original posted loss for the receipt position, including a position whose balance fell to zero. Confirm the physical bin and identity. For each source, enter the quantities you inspected as saleable and the quantities still damaged or uncertain. Record your inspection findings. The recovery receipt links the original loss and any new quarantine record. Check the physical balance, the still-missing quantity and the original receipt cost. A later inspection can release eligible pieces from the new quarantine. [Recovery inspection](https://github.com/abrianbaker80/BrickVault-V2/issues/39)

A three-piece delivery costing 100 cents has separate one- and two-piece losses of 33 and 67 cents. Two partial recoveries of the second loss must return the full 67 cents. Losing those pieces again consumes 67 cents. Qualify mixed recovery against named quarantine records and the ordinary missing quantity, over-recovery rejection, exact replay, a later open shortage and renewed access to the receipts after restore. Distinguish an unknown cost from a documented zero. When a recovery changes the stock in an open shortage, record a fresh count before reviewing a loss or closure. A recount task that ended when its position emptied stays depleted. Queue a new recount to witness the recovered stock.

## Close a shortage after finding all pieces

If the ledger shows eight pieces and a count finds six, record the short count against the receipt position. Count the same pieces again. If all eight are present and their identity and condition match, open the latest count, review the shortage, confirm the bin and give a reason. The resolution receipt links the opening count and matching recount. Check it again from count history after refreshing Workbench. A lower or higher count keeps the case open. An earlier posted loss retains its own receipt. [Exact recount resolution](https://github.com/abrianbaker80/BrickVault-V2/issues/37)

The qualification uses a position with eight units, including one in quarantine, two held for a build and three backing an unpublished offer. It checks the blocked hold after the exact count, the separately confirmed resolution, eligibility for a new hold, and a later independent shortage. A restored workspace must return the original receipt on retry.

## Withdraw backing from a draft offer

Choose the receipt position and the internal draft offer that uses its pieces. Check the current allocation and the amount it would retain. Confirm the physical bin, enter the quantity to withdraw, and record a reason. The receipt identifies the offer, the position and the units withdrawn.

For example, eight pieces are in a bin. Two are held for a build and three of the six saleable pieces back a draft offer. Withdrawing one leaves the same eight pieces in the bin and two backing the offer. Four saleable pieces are now withheld from offers. Withdrawing the last unit from a draft retains the offer's identity, price and contribution history. When a count has opened a shortage, review the witnessed count and its offer reductions through the shortage workflow. [Draft backing](https://github.com/abrianbaker80/BrickVault-V2/issues/35)

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

If the position has quarantined pieces, identify how many missing pieces came from each quarantine record, or confirm that all missing pieces were ordinary stock. Leave the shortage open when their condition is unknown. Check the quantities that will remain available and the proposed changes to any draft offers before confirming. Reopen the loss receipt to see the recorded choice.

For example, a receipt position holds six pieces. Two are quarantined, one is held for a build, and three back a draft offer. You count four. If one missing piece was quarantined, one stays in quarantine. After the build hold, the bin supports two pieces for the offer. If both missing pieces were quarantined, all three pieces backing the offer can stay allocated. In either case the physical loss is two. [Owner decision](https://github.com/abrianbaker80/BrickVault-V2/issues/33#issuecomment-7155121127)

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

Run `python scripts/foundation/manage.py verify` after a clean build. The qualification uses the normal private HTTPS API, PostgreSQL and Workbench. The restore check compares the purchase and both deliveries, their placements, offer backing, reference audit and original command receipts. A schema-2 backup still requires its archived release. Schema-3 backups also fingerprint the five receiving tables. Schema-4 backups add position identities, transfer events and transferred offer backing. Schema 5 adds count observations and shortage cases. Schema 6 adds named build holds and their release history. Schema 7 adds posted stock losses and the chosen offer reductions. Schema 8 adds frozen recount previews, their target positions, queued batches and per-target receipts. Schema 9 records voluntary withdrawals from internal draft-offer backing. Schema 10 adds quarantine, inspected releases and the operator’s allocation of missing pieces to quarantine records. Schema 11 adds the separate exact recount resolution receipt. Schema 12 adds loss cost assignments, recovery receipts and the inspected quantities from each source. Schema 13 adds manual part-outs, derived output layers, physical remainders and the cost already consumed before a subsequent loss. Schema 14 adds the registered physical labels and the history of changes to each bin. The restore check reopens the archive receipt, verifies its original stock counts and replays the outgoing transfer. Schema 15 adds the bin piece-limit history and the per-bin records that serialize concurrent changes in occupancy. Each archive requires its matching release. [Setup and recovery](m2-foundation.md)

## Part out a received item

In **Inventory**, open the source position and choose **Record a manual part-out**. Confirm the source bin and the number of units disassembled. For each identified output, select the catalog identity, enter the number found, confirm its destination bin and choose whether to stock it or retain it in quarantine. Record discarded or missing material with a description and quantity.

Assign documented amounts to whole output lines in the purchase currency and enter any unallocated amount explicitly. For example, one unit from a three-unit, USD 1.00 receipt can carry USD 0.33. Assign USD 0.18 to a pair of axles and USD 0.11 to a figure, then retain USD 0.04 as unallocated. An unknown input basis stays unknown. An evidenced zero remains zero.

Open the resulting receipt from either position's timeline. It identifies the original cost layer, each output position, its disposition and any physical or monetary remainder. Receiving keeps the original supplier quantities and shows derived layers separately. An identified output kept in quarantine requires a passed inspection before it can back an offer. [Manual part-out](https://github.com/abrianbaker80/BrickVault-V2/issues/41)

## Use an alternate bin label or retire a bin

In **Locations**, look up the label on the bin. Check its canonical code and location name before adding an alternate label. Each physical label identifies one bin across active and archived locations. From schema 14 onward, renaming a bin keeps its old code as an alternate. On upgrade, only the code that belonged to each bin at the time of migration becomes its opening label. Earlier receipts retain the label captured when the work was performed.

To archive a bin, review its physical quantity and occupied stock positions, confirm a registered label from the bin and enter the reason. If stock changes before confirmation, review the updated counts. The archive receipt records what was present at confirmation, even after those pieces move.

Stock can stay on an archived shelf. Find it in Inventory to record a count or transfer pieces to an active bin. New putaway, inbound transfers and manual part-out outputs require active destinations. [Bin labels and archiving](https://github.com/abrianbaker80/BrickVault-V2/issues/43)

## Set a piece limit for a bin

Open the bin in **Locations** to see its physical quantity and remaining room. Enter a positive whole number and the reason for the limit. Leave a bin unconfigured when no piece limit has been established. Workbench preserves each change and shows the occupancy at the time it was recorded.

For example, a bin holding three kits with a four-piece limit has room for one more piece. Disassembling one kit on that shelf and returning two axles to the same bin fills it. An axle retained for inspection still occupies one space. When several outputs go to the same bin, Workbench counts them together.

If a placement is rejected because another operator filled the bin, review the reported quantity and choose a destination with enough room. A smaller limit can leave more pieces on the shelf than the limit allows. The stock remains available for counting and outgoing transfers. Inspecting a previously lost piece records where it was found, including when that shelf is already full. [Bin piece limits](https://github.com/abrianbaker80/BrickVault-V2/issues/45)
