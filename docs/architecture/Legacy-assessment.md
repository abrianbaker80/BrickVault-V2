# BrickVault V1 evidence and migration constraints

The V2 plan draws on a private review of the V1 schema, application behavior, regression specifications and historical release evidence. This public assessment explains what the migration must preserve and how the operator can test it. Store measurements, customer and order records, infrastructure details and private evidence remain in the restricted archive. M0 authorizes repository work. Migration, live reads and deployment require their own approval. [Master plan](MASTER_PLAN.md) · [Inventory and BrickLink contract](Inventory-and-BrickLink.md) · [Feature matrix](FEATURE_MATRIX.md)

## How to interpret the evidence

| Evidence | What it can establish | Required follow-up |
|---|---|---|
| Private V1 schema and implementation review | Data identities, intended write paths, stored precision, structural constraints and cases in which information can be lost. | Inspect the actual schema, enabled writers and data contracts in an authorized, isolated restore. |
| Historical release, restore and reconciliation records | Which controls have been exercised previously and what evidence a comparison can produce. | Create fresh manifests for the exact rehearsal inputs and for the final freeze. |
| Regression specifications | A scenario, invariant or failure condition to carry into the new implementation. | Execute independent V2 tests against a disposable database and stateful provider simulator. |
| Operator and provider observation | What a specific workflow or remote system actually did within an observed interval. | Qualify the approved workstation, physical devices, source coverage and provider operations individually. |

## Stock identity, placement and acquisition

A V1 inventory lot has an internal identity, an optional provider inventory ID and recorded quantity. A lot can have several physical placements. Each placement has its own identity, lot, location and quantity. Location codes, label aliases, picking hints, layout, archive state and eligibility restrictions influence where the operator can put stock and how it can be found later. A primary location aids navigation. It cannot substitute for all placements on a split lot. Per-row uniqueness does not enforce a sum across placements.

Guarded V1 edits lock stock and placements, compare the recorded total to the distribution and protect active order and build holds. Physical counts require an observed distribution, reason and reviewed revision. Stale observations and rejected corrections retain evidentiary value. V2 must preserve these distinctions, reconcile quantities under transaction locks and maintain a single eligibility rule shared by allocation, count, transfer and publication.

Preserve the item type and exact catalog number, including nonnumeric identifiers, variant and color namespace. Keep unknown color separate from inapplicable. Historical condition values, set completeness, packaging and instructions require an explicit field mapping. Public descriptions and private remarks have different audiences. Catalog similarity alone cannot determine whether two sale promises can share an offer.

Some V1 merge paths blend selling price, cost or price paid. Minifigures can require unit-level assessment or await components. An opening cost can carry an identified receipt, a traceable historical weighted basis or an unknown basis. Map the original currency, precision and evidence. A supplier, acquisition layer or historical allocation that cannot be established remains unknown. A sale valuation computed from listing price cannot establish landed cost. Preserve incomplete or quarantined pieces through the initial stock load.

A reviewed marketplace intake can bind new listings to an operator-confirmed location. That mechanism establishes neither the complete physical distribution of existing goods nor their acquisition history. Keep existing bindings, any historical order references and evidence of retained or stockroom lots. MIG02 creates opening custody and obligations from separately reconciled sources.

## Purchasing, pricing and financial evidence

| Legacy evidence | V2 treatment |
|---|---|
| Collection purchases, acquisition dates, vendors, set-analysis estimates and intake/import jobs | Classify their purpose before mapping them. A discovery or import job is a different event from a supplier purchase or physical receipt. Preserve source references, unknown suppliers and unallocated landed costs. |
| Prices, observations, historical decisions, manual overrides and batch repricing manifests | Preserve decimal values, currency, source, observation time, exact selection and per-row disposition. Keep a computed recommendation, saved local decision and confirmed remote price distinguishable. |
| Gross, fee and net amounts, refund events, payment references and manual matches | Preserve the financial facts with their matching uncertainty. Keep estimates, derived totals and absent inputs explicit. A refund cannot prove physical receipt of a return. Importing a payment event must never initiate another payment. |
| Batch transformations and specialist planning | Use defensible opening layers for historical stock. Future purchases, part-outs, returns and corrections conserve quantity and value or record the explicit remainder. Skipped supplier evidence cannot be reconstructed from present listing prices. |

## Orders and fulfillment

1. **Discovery.** V1 can limit a fetched order list and fetch detail and item groups separately. The paginated local order register cannot prove upstream completeness. Validate filed and unfiled scopes, nested item batches, partial reads and a rotating set of open and historical orders. Preserve missing source fields as exceptions.
2. **Notification.** A persisted or coalesced hint can mark the local view stale. It does not establish a complete order change. Deduplicate repeat notices, refetch canonical order detail and compare it to the current accepted revision.
3. **Revisions.** Some legacy refresh paths replace lines before picking and preserve local lines once fulfillment is underway. Preserve stable internal identities, source lot evidence and historical revisions. Detect an item swap that leaves the header totals unchanged, including after picking starts. A line that cannot be matched safely requires review.
4. **Allocation.** Legacy picking calculates available stock from placement-level order allocations. Count and mutation guards can also account for build holds, while the remote comparison subtracts certain unpacked order commitments. Different calculations create a possible double claim. Test simultaneous marketplace demand and an existing build hold against one shared stock model. Preserve the original obligation, allocation and shortage independently.
5. **Packing.** V1 can reduce placement stock, consume reservations and enqueue a remote status operation when packing completes. Packed goods may remain in physical custody. V2 tracks each tote and parcel through partial picks, verified packing and **confirmed handoff**. Bridge each packed-but-unshipped parcel once at migration. An already shipped parcel creates no opening stock or additional deduction.
6. **Shipping and recovery.** Retain local handling, carrier/tracking, marketplace status and notification evidence separately. Prior successful steps survive restart. Reissue only an outstanding step with safe semantics after fresh evidence. A reprint, local unship or audited correction has a different effect from a carrier return. Confirm physical contents before reshelving a canceled pick.
7. **Daily work.** The operator needs a searchable order queue and fulfillment details beside the line items. Preserve exact scope, stable sorting, freshness and re-entry into an interrupted pick. Changing the layout must preserve the ability to locate a parcel, act on the right line and explain what blocks shipping.

## BrickLink synchronization

| Legacy mechanism | Qualification or migration requirement |
|---|---|
| Compare remote lots with local records and known outstanding order units | A missing sale or stale read can change the calculated delta. Record discovery completeness and observed listing identity. Fence an unexplained drop, missing lot or remote-only lot for review. |
| Preview selected fields at a reviewed revision | Preserve the selected identities, quantity, price, field ownership, expiry and before-image. Distinguish omitted optional attributes from intentionally cleared terms. |
| Durable queue, claimed work and per-lot preflight | Preserve the exact operation and local cause, signed quantity delta, live binding and predecessor observation. A fresh preflight leaves room for a subsequent sale or manual edit before the provider applies the change. |
| Ambiguous write and request budget handling | Record the wire attempt and preserve uncertain dispatch across restart. A lost response can follow a successful write. Check the permitted account budget privately and reserve capacity for discovery and confirmation. |
| Queue completion, acknowledgement and fresh observation | Keep the committed business command, transport outcome and independent read-back as separate facts. A matching number alone cannot attribute a possibly applied delta when another operation can produce it. |
| Withdrawals, consolidation and duplicate cleanup | A retained zero-stock lot can remain a valid identity. Direct cleanup can update one lot and delete another outside routine queue dispatch. Inventory every write path and quiesce it during cutover. Never automatically recreate a vanished listing or compensate a possibly applied write. |

## Devices and other operating workflows

V1 contains browser or native interactions for location lookup, direct printing, document labels, camera-assisted identification and scale input. A health endpoint proves that a service answered. A physical test proves whether the selected device produces a legible label, a dependable scan, a fresh weight or a usable image. Preserve a typed entry path, manual catalog confirmation and recovery after disconnection. Document the selected device and real test evidence privately.

Client-side journals can retain a command identity and immutable payload across loss of a response. Preserve unresolved journals and verify their corresponding server receipt. Never automatically replay a historical command in either system. Keep the distinction between inspected rows, explicitly selected rows and an all-inventory action. Errors, confirmed empty results and stale prior successes require separate presentation.

Collection/portfolio, build reservations, set and component part-out, wishlists, specialty images, sourcing, third-party collection and financial integrations are relevant to data retention and owner choices. S01 reviews each relied-on workflow, including the complete purchase-to-batch outcome, identification, direct specialty labels, project holds and multi-order picking. D03 leaves their initial-release disposition provisional. A deferred interface still requires active commitments and blocked stock to survive migration.

## Rehearsal controls

| Control | Required result |
|---|---|
| Source and schema | Restore an authorized snapshot in isolation with outbound writes disabled. Record a restricted manifest for the schema, constraints, indexes, source checksums, journals and representative exceptions. Verify the restored database actually matches. |
| Stable crosswalk | Map source installation, entity and source identity to deterministic target IDs. The snapshot identifies the evidence and its revision. Rehearsing a later snapshot must preserve identity for the same business object. Explain every split, merge, archive or unresolved row. |
| Physical custody and exposure | Reconcile units by lot, variant, location, sale state, hold, tote and parcel. Resolve unlocated and incomplete items explicitly. Verify the opening ledger against independent controls and provider bindings, including stockroom and retained-zero records. |
| Financial control | Compare original decimals, currency totals, fees, source coverage, weighted openings, unknown amounts and residual allocations. Recompute exports and check layer consumption and return reversal without mixing currencies. |
| Open demand | Reconcile each obligation, backed unit, shortage, substitution, pick, packed parcel and already handed-over quantity. Bridge the V1 pack-deduction rule once. A remote shipment flag alone cannot settle physical custody. |
| Outstanding effects | Classify each local journal, queue attempt, external write and acknowledgement. Preserve genuine unknowns under a fence. Derive any future intent from reconciled facts and new authorization. |
| Repeatability and backout | Repeat the import on the same immutable input, compare logical IDs, counts, units, money and exceptions, and rehearse a later source snapshot. Exercise both backout cases, including post-freeze sales, shipment and a lost response. |

## Synthetic regression cases

- An accepted sale reduces sellable stock once. A lower provider quantity that already reflects the sale cannot authorize a positive correction.
- An operator counts fewer units than an active order or build hold. Preserve physical observation, actual backing, unmet demand and the audit. Failure to record the audit rolls back the correction.
- Packing or shipping is submitted twice or loses its response. Persist the command once, recover the parcel and settle tracking separately from the external status update.
- A fetched order changes items with identical totals, repeats a prior revision or arrives late. Apply each observed transition once while preserving the current accepted revision.
- A manual provider edit, pending same-lot operation or stale local revision makes acknowledgement ambiguous. Display the fresh state and retain the unsettled attempt.
- A partial restore or resumed worker encounters an uncertain prior write. Keep publication fenced until the effect is classified and the external frontier reconciles.

These scenarios are acceptance inputs for future milestones. Public M0 covers repository engineering. Device, database, migration, provider and production results require their own receipts. [Feature matrix](FEATURE_MATRIX.md)
