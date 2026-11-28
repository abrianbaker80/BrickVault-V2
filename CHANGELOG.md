# Changelog

## M3 receiving

- Receiving lets operators set the selling currency and manual unit price of an unpublished offer. Receipt costs keep their purchase currency. Transfer, stock-loss and withdrawal receipts show the recorded offer price. [Draft offer currency](https://github.com/abrianbaker80/BrickVault-V2/issues/52)

- The movement history keeps its source and destination buttons in place while Workbench refreshes the bin read and capacity status.

- Inventory now finds stock by a bin’s full alternate or former label, including stock on archived shelves. A recount of all matches uses the same search and filters. [Inventory label search](https://github.com/abrianbaker80/BrickVault-V2/issues/48)

- Added optional piece limits for storage bins. Locations shows the room remaining and records policy changes. Putaway, transfers and part-outs check the destination again when stock is committed. [Bin piece limits](https://github.com/abrianbaker80/BrickVault-V2/issues/45)

- Added alternate labels and archiving in Locations. Operators can look up a bin by its old or alternate label, count the stock on an archived shelf and transfer it out. Receiving, transfers and manual part-out require an active destination. [Bin labels and archiving](https://github.com/abrianbaker80/BrickVault-V2/issues/43)

- Added manual part-out in Inventory. Confirm the source and output bins, assign documented costs, and record retained, discarded or missing material. Each output links back to its purchase and source position. [Manual part-out](https://github.com/abrianbaker80/BrickVault-V2/issues/41)

- Added inspection when recording pieces found after a posted stock loss. Workbench tracks saleable and quarantined quantities against their original loss records and restores the receipt cost assigned to those pieces. [Recovery inspection](https://github.com/abrianbaker80/BrickVault-V2/issues/39)

- Added a separate confirmation for a shortage when a later count finds every piece in the same receipt position. Operators can reopen the resolution receipt from count history. [Exact recount resolution](https://github.com/abrianbaker80/BrickVault-V2/issues/37)

- Added quarantine and inspected release for pieces already in a bin. Workbench preserves their receipt cost and shows how many pieces remain available for a new offer, hold or transfer. [Quarantine and inspection](https://github.com/abrianbaker80/BrickVault-V2/issues/33)
- Added the operator's choice of which quarantined pieces were missing when posting a witnessed shortage. The loss receipt records the damaged and ordinary quantities and any selected reductions to draft offers. [Stock-loss decision](https://github.com/abrianbaker80/BrickVault-V2/issues/33#issuecomment-7155121127)
- Added voluntary withdrawal of backing from unpublished draft offers. Operators choose the receipt position, offer and quantity, and can reopen the withdrawal receipt after the offer reaches zero backing. [Draft offer backing](https://github.com/abrianbaker80/BrickVault-V2/issues/35)

- Added receipt-lot history across bins. A transfer shows the units and offer backing moved, and a delivery's cost appears once. [Inventory workflow](https://github.com/abrianbaker80/BrickVault-V2/issues/31)

- Added recount previews, a shared count queue and a receipt for each batch. Operators can freeze selected positions or every match to a filter, then see which targets were queued, changed, already queued or unavailable.

- Added a stock timeline to the Inventory position inspector. Follow transfers, witnessed counts, build holds, offer backing and posted losses to see how a position reached its current quantity. [Position timeline](https://github.com/abrianbaker80/BrickVault-V2/issues/29)

- Added stock-loss posting with operator-selected reductions per unpublished offer. A single receipt records the witnessed loss, the change in backing at that position and the receipt cost assigned to the loss. [Stock count and loss decisions](https://github.com/abrianbaker80/BrickVault-V2/issues/22)

- Added a stock adjustment review to open shortages. Operators can compare the latest count, recorded receipt cost, protected build units and each unpublished offer before posting a loss. [Stock counts and adjustments](https://github.com/abrianbaker80/BrickVault-V2/issues/22)

- Added named project holds on individual receipt positions. A witnessed shortage keeps each hold in place until an operator selects a project, quantity and reason for release. [Build holds](https://github.com/abrianbaker80/BrickVault-V2/issues/23)

- Added witnessed counts with bin confirmation, replayable receipts and a shortage case that protects the affected position. Each recount preserves the prior observation and original receipt cost. [Witnessed counts](https://github.com/abrianbaker80/BrickVault-V2/issues/22)

- Added the Inventory register for finding physical stock across bins, checking its receipt cost and following a selected position’s movement history. [Inventory register](https://github.com/abrianbaker80/BrickVault-V2/issues/19)

- Added confirmed transfers of receipt-layer stock with per-offer backing and per-bin movement history. [Transfer checkpoint](https://github.com/abrianbaker80/BrickVault-V2/issues/18)

- Added a read-only stock breakdown to the location inspector. Each bin shows its receipt layers, original delivery costs and offer backing. [Bin inspection](https://github.com/abrianbaker80/BrickVault-V2/issues/15)

- Added split purchase receiving, bin confirmation, cost coverage and manually priced unpublished offers to Workbench. The staged schema upgrade preserves the original instance. [Receiving and upgrade guide](docs/verification/m3-receiving.md)

## M2 isolated technical foundation

- Protected private request and response fields in gateway diagnostics when a response is interrupted. The service still records the operational warning.

- The Windows backup task retries delayed OneDrive uploads within the same run and verifies the signed receipt and encrypted archive. The server continues to capture nightly. [Hosted backup operations](docs/verification/m2-hosted-backups.md)

- Restricted database cleanup to expired operator sessions. Existing instances can refresh the policy while stopped. A retained client kept its active session and original command receipts across the update and restart. [Foundation runbook](docs/verification/m2-foundation.md)

- Added the authenticated Vue Workbench, ASP.NET Core API and PostgreSQL reference register. Catalog and location commands retain their receipts across retries. Activity links recorded changes to the worker's result.
- Added a local TLS gateway, separate database roles, owner credential rotation, versioned migrations and an encrypted backup with release and data verification on restore. The runbook describes the qualification scope and outstanding production gates. [Foundation runbook](docs/verification/m2-foundation.md) · [M2 issue #6](https://github.com/abrianbaker80/BrickVault-V2/issues/6)
- Recorded the owner's authorization of M2 and direction to continue routine approved work through independent review and protected checkpoints. Major decisions return to the owner. [Decision register](docs/architecture/DECISIONS.md)

- Independent review verified workspace-bound retries, certificate trust scoped to the Workbench browser, database-aware readiness, credential rotation, and recovery into an independent cluster. The production hosting decision remains open. [Review record](https://github.com/abrianbaker80/BrickVault-V2/pull/7#issuecomment-7092744733)

- Added an off-host recovery point containing both the encrypted database and its exact release. Publication verifies a fresh provider download. Recovery restores into a new instance with its own credentials and identity for future backups.

- Added a supervised Linux guest, a private LAN gateway and a workstation-only Firefox launcher. Encrypted nightly backups use an independent controller and the existing storage provider. [Server setup](docs/verification/m2-foundation.md#run-workbench-on-the-private-server) · [Backup operations](docs/verification/m2-hosted-backups.md)

- Verified recovery from a signed cloud backup into a fresh Linux guest. After reboot, Firefox saw the original workspace, audit entries and command receipts. [Full-guest rehearsal](docs/verification/m2-foundation.md#rehearse-a-full-guest-rebuild)

## M1 design and framework comparison accepted

- Added two interactive visual directions with fictional inventory, receiving, fulfillment and recovery records. Both native inventory registers share the same task tests. [M1 review](prototypes/m1/REVIEW.md)
- The owner accepted U01–U03 and selected Workbench navigation and visual design and Vue 3 with TypeScript for the frontend. [Accepted M1 comparison](https://github.com/abrianbaker80/BrickVault-V2/commit/d5a3264dcf3650bbf91fb98a132bb31b74c9fb92) · [M1 issue #4](https://github.com/abrianbaker80/BrickVault-V2/issues/4)

## M0 engineering checkpoint accepted

- Documented the architecture plan, release-scope questions and owner decision register for BrickVault V2.
- Defined a pull-request workflow with a required repository check, checksum-pinned tooling and redacted secret scanning.
- Added project-scoped Architect, Builder and Reviewer roles, a fictional test fixture and a repeatable procedure for verifying independent review, correction and an owner-approval pause.
- Witnessed the Insiders sidebar complete five native assignments, correct the fixture after independent review and pause before the separate README edit. Witnessed revision [b731a22](https://github.com/abrianbaker80/BrickVault-V2/commit/b731a225964b7d102243960f0308c9e248a98fb8).

The owner accepted the engineering checkpoint at [2d88409](https://github.com/abrianbaker80/BrickVault-V2/commit/2d88409b5fb556316888d1282c08968ba5619c70). The checkpoint issue records G03’s post-merge synchronization check. The owner subsequently authorized M1. [M0 issue #1](https://github.com/abrianbaker80/BrickVault-V2/issues/1)
