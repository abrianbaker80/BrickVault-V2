# Bricqer workflow review for BrickVault V2

Public product research for the design review

The design opportunity for this store is to follow a piece from acquisition to a specific bin, sale, tote and shipment, then explain its margin. The independent V2 design can borrow the operational sequence and keep the physical and marketplace evidence visible at every handoff.

## Evidence and scope

The architecture review covered all thirteen articles in guide order, their linked illustrations and sampled frames from the official demonstrations. The observations below come from the written guides and visible examples. The review did not establish the spoken narration or independently measure vendor performance. Each entry links to material hosted by its publisher. The acceptance scenarios use synthetic examples. [Guide sequence](https://www.bricqer.com/guides)

## Section-by-section decisions

### 1. Purchases & batches

**Observed.** The purchase form captures date, condition, seller, description and acquisition price. A purchase can feed several batches, each with its own publication timing. The purchase screen groups lot counts, quantity sold and remaining, cost, proceeds and break-even indicators.

**Proposed disposition.** Adopt purchase → batch → received stock. Carry an explicit acquisition cost or unknown value. Suggest the purchase condition on each received item and allow inspection to correct it. A batch can be partly received and partly held. Base realized profit on actual sales, expenses and the cost of the units shipped. Make scheduled release a later convenience. [Guide](https://www.bricqer.com/guides/creating-purchases-and-batches) · [Purchase statistics](https://www.bricqer.com/gfx/guides/purchase-statistics.png) · [Demonstration at 1:28](https://www.youtube.com/watch?v=W0SNqne6oP4&t=88s)

### 2. Adding new items

**Observed.** The guide walks through keyboard entry, a filterable picture catalog, ranked camera matches, resumable set part-outs, BSX/order CSV/inventory XML import and BrickMonkey import. It links identification to price, location and batch release. The publish dialog offers repricing or scheduling before the background task begins.

**Proposed disposition.** Adopt quick catalog entry, condition and color confirmation, quantity, putaway and explicit release preview. A suggested identity, bin or imported row needs confirmation before it creates sellable stock. Qualify assisted identification and import formats individually. Preserve a part-out draft if scoped. Defer BrickMonkey, simultaneous staff editing and scheduled campaigns. [Guide](https://www.bricqer.com/guides/adding-new-inventory) · [Catalog grid](https://www.bricqer.com/gfx/guides/visual-editor.png) · [Demonstration at 1:10](https://www.youtube.com/watch?v=gs2jakhyEuI&t=70s)

### 3. Managing inventory

**Observed.** Search supports item identifiers, wildcard locations, color, category and quantity/price bounds. Results show item thumbnails and quantities. The detail example links external lot IDs to status. Saved filters can be reused. A bulk operation can affect the entire matching result, including prices, locks and locations.

**Proposed disposition.** Adopt a searchable lot table with a separate inspector, physical placement breakdown and BrickLink lot link. Freeze bulk target IDs, count and versions before confirmation. Distinguish selected rows from every matching row. Preview quantity or price impact and report per-target results. a changing search must never expand a pending command. [Guide](https://www.bricqer.com/guides/inventory-management) · [Search results](https://www.bricqer.com/gfx/guides/inventory-search.png) · [Demonstration at 1:30](https://www.youtube.com/watch?v=uhpxE5Wdj74&t=90s)

### 4. Managing orders

**Observed.** The queue groups payment, picking, packing and pickup work and shows a global shortage alert. Detail combines customer, shipping, payment, lines, files and messages. It documents automatic invoicing, off-site payment links, custom invoice/paid actions, pick/pack transitions and cancellation with restock.

**Proposed disposition.** Adopt actionable queues, shortage prioritization and an order timeline. Record original BrickLink totals, payment state and external changes. Surface tax, fee and shipping components as captured. A canceled picked order requires bin return or another recorded disposition before sellable stock rises. Defer customer messaging, payment collection and custom fiscal workflows unless required by the operation. [Guide](https://www.bricqer.com/guides/order-management) · [Order detail](https://www.bricqer.com/gfx/guides/orders-detail.png) · [Demonstration at 2:06](https://www.youtube.com/watch?v=MHypM9FhDUo&t=126s)

### 5. Picking orders

**Observed.** The operator can choose one or several orders. Each receives a letter and color to identify a physical basket. The handheld view presents location, piece, expected quantity and per-order progress. Filters can limit locations or condition. Quantity entry and autofill are configurable, and partial lines remain visible.

**Proposed disposition.** Adopt one-order picking first. A pick moves inventory to a named tote against an allocation. Keep visible item, color, condition, source bin and remaining count. Permit short picks with a reason and recovery task. Add multi-order waves only after verifying that one bin visit can apportion the same piece to two named totes without double counting or cross-packing. [Guide](https://www.bricqer.com/guides/picking-orders) · [Handheld picker](https://www.bricqer.com/gfx/blog/multi-picker.png) · [Demonstration at 2:52](https://www.youtube.com/watch?v=yJEyh-Y2idE&t=172s)

### 6. Packing & shipping

**Observed.** The packing guide checks recipient identity, address and postage. Unsupported address verification sends the order to operator review. It documents label creation, shipment notification, feedback, costs and archiving, plus a manual-postage path. The screen contrasts the supplied address with the checked candidate.

**Proposed disposition.** Adopt a single-order pack station. Verify tote, order, lines and packed quantities before selecting postage. Retain both original address and approved correction. Record the shipment, tracking, label identity, reprint, handoff and marketplace confirmation separately. Protect against duplicate postage charges after a timeout. Qualify the owner's actual carrier and printer and approved media. [Guide](https://www.bricqer.com/guides/packing-and-shipping) · [Address check](https://www.bricqer.com/gfx/guides/shipping-check-address.png) · [Demonstration at 1:15](https://www.youtube.com/watch?v=WK1_eCc8o3A&t=75s)

### 7. Local order pickup

**Observed.** A shipping method identifies pickup orders. A notification can include instructions and a barcode. An operator scans the barcode with a camera/reader or opens the queue entry, records handoff and payment, and files the order. The pickup guide and order guide describe slightly different readiness wording.

**Proposed disposition.** Defer a dedicated pickup workflow pending actual use. Preserve the incoming BrickLink fulfillment method and surface unsupported pickup orders for review. If enabled later, define packed-ready and collected separately and require an explicit physical handoff. Opening hours, automated emails, feedback and retail barcode issuance add little value without local traffic. [Guide](https://www.bricqer.com/guides/local-order-pickup) · [Pickup finalization](https://www.bricqer.com/gfx/guides/order-pickup-finalize.png) · [Demonstration at 1:01](https://www.youtube.com/watch?v=p6XRRFsiPCg&t=61s)

### 8. Creating a local order

**Observed.** The operator can select a customer or walk-in, shipping method and charge, then search or scan stock, adjust price/quantity, set payment and print an invoice or open the picker. The detail illustration explicitly separates available, reserved and total units.

**Proposed disposition.** Exclude seller-created sales orders from the proposed first release. BrickLink originates sales in this business. Model owner withdrawals, samples and corrections as reasoned stock movements. If local selling is later approved, use the existing reservation and publication rules and a distinct order origin. [Guide](https://www.bricqer.com/guides/creating-a-local-order) · [Quantity and price](https://www.bricqer.com/gfx/guides/storefront-item-details.png) · [Demonstration at 1:30](https://www.youtube.com/watch?v=9SAfIJYoCw4&t=90s)

### 9. Storefront

**Observed.** This is Bricqer's retail POS. It defaults to a walk-in customer and pickup, provides scan/search and a customer-facing secondary display, supports custom receipts and POS pricing, and can accept terminal payment. The demo shows the second display and touch device.

**Proposed disposition.** Exclude POS, shopper display, receipts, retail pricing, terminal integration and a separate public shop. BrickLink remains the storefront. The focused touch interaction is relevant to an internal pick station, where operational warnings must remain visible. [Guide](https://www.bricqer.com/guides/storefront) · [Customer display](https://www.bricqer.com/gfx/guides/storefront-customer-display.png) · [Demonstration at 0:58](https://www.youtube.com/watch?v=kaHeI93eET8&t=58s)

### 10. Automatic coupons

**Observed.** A campaign sets the trigger, platform and optional stop date. Eligibility can depend on spend and order cadence. Discounts can have caps, expiry and product exclusions. The settings view includes issued/used counts and exports.

**Proposed disposition.** Defer outbound coupon campaigns. If a BrickLink order includes a discount, ingest the observed financial effect for reconciliation and realized profitability. Campaign automation would introduce additional external writes and policy decisions. [Guide](https://www.bricqer.com/guides/coupon-codes) · [Campaign settings](https://www.bricqer.com/gfx/guides/automatic-coupons.png) · [Demonstration at 0:52](https://www.youtube.com/watch?v=RNkivgekwUQ&t=52s)

### 11. Pricing formulas

**Observed.** A formula may use sold or listed guide statistics, product attributes, sales history and acquisition cost. The operator can override and lock individual prices. A background refresh supports all lots and shows progress. Per-market overrides are illustrated.

**Proposed disposition.** Adopt decimal prices, source/observation date, versioned rules, manual override/lock, preview and per-lot confirmation. Represent missing market data explicitly. An unconfirmed external price remains visible as pending. Defer unrestricted formula scripting and cross-market modifiers. Reserve request capacity for orders before large repricing runs. [Guide](https://www.bricqer.com/guides/pricing-formulas) · [Price refresh progress](https://www.bricqer.com/gfx/guides/update-sales-prices.png) · [Demonstration at 0:34](https://www.youtube.com/watch?v=l_2n-MXKpjg&t=34s)

### 12. Exporting data

**Observed.** The inventory export distinguishes a single sale-lot row with combined locations from individual purchase/batch/location rows with separate costs. Formats include BSX, CSV and JSON with differing detail. Order exports select dates, financial files or document archives. The API guide describes another programmatic access path.

**Proposed disposition.** Adopt two explicit export granularities and dated order financials. Include currency, date basis, stable IDs, units, unknown-cost counts and a reconciled control total. Make CSV a baseline. Add BSX, spreadsheet-specific formats or accounting connectors only for a defined consumer. A presentation export cannot restore allocations, sync receipts or audit history. [Guide](https://www.bricqer.com/guides/export-inventory-and-orders) · [Inventory export options](https://www.bricqer.com/gfx/guides/inventory-export.png) · [Demonstration at 0:44](https://www.youtube.com/watch?v=1h_-k4q6-V4&t=44s)

### 13. Consolidation

**Observed.** Physical consolidation puts new pieces into an existing bin. Separate placements let equivalent items stay in multiple bins and are pooled for sale. The vendor’s illustration shows how several placement quantities support one public listing. The picker can source a sale from more than one location.

**Proposed disposition.** Adopt separate placement, cost/provenance and listing identities. Preserve distinct customer-visible grade, description and listing constraints. Offer an existing bin or another eligible bin, record a confirmed transfer if moved, and allocate exact placements when an order arrives. No automatic merging of existing marketplace lots by catalog key alone. [Guide](https://www.bricqer.com/guides/inventory-management-consolidated-and-unconsolidated) · [Four physical placements and one lot](https://www.bricqer.com/gfx/blog/inventory-multi-location.png) · [Demonstration at 2:06](https://www.youtube.com/watch?v=Yhng8WYTWvM&t=126s)

## Linked workflows and implications

- The part-out article documents multiple sets, supplier catalog choices, choosing an existing location and reopening a shared session. Saving a single operator's unfinished counts is useful. Multi-user coordination does not serve the stated business. [Part-outs](https://www.bricqer.com/news/collaborate-on-partouts)
- The storage-strategy article explains why split placements reduce walking during intake and increase work during picking. V2 can show placement choices and expected picking impact. A single global consolidation policy would mask that tradeoff. [Storage strategies](https://www.bricqer.com/news/inventory-management-strategies-consolidation)
- The export guide points to Bricqer's programmatic access article. Revocable credentials and a documented data model are relevant to portability. This provides no evidence that V2 needs a public developer API or that BrickLink offers the same capabilities. The external Markdown guide and SendCloud service are generic references for optional messaging and carriers. [Bricqer API guide](https://www.bricqer.com/guides/using-the-api)

## Acceptance scenarios drawn from the research

| Walkthrough | Evidence the operator should be able to see |
|---|---|
| Receive equivalent parts from two purchases into one drawer and another drawer | The location view gives physical counts. The lot inspector explains batch quantities and costs. A listing shows only eligible stock. A delayed or partial batch release cannot publish the whole purchase. |
| Process two BrickLink orders that include the same part from a split bin | Each allocation links to its order and placement. A pick visit shows exact amounts for each labeled tote. A short pick on one line leaves the other order and both order totals intact. |
| Cancel after picking, or discover a missing part while packing | Preserve the original order revision, tote and movement. Require a disposition for each piece that left the bin. The operator can see when an actual return or verified adjustment affects available units. |
| Lose connectivity after sending a new price or quantity | Display intended, observed and confirmed values with timestamps. Link the uncertain call to recovery. A partial batch remains partly pending. A queued operation count cannot imply publication. |
| Derive the margin on a shipped order | Trace the sale lines to the supplying purchase layers, carrier charge, marketplace fees and discount. Report unknown cost or missing fees separately and identify the date basis for realized results. |
| Print twice or switch workstations halfway through shipping | The saved shipment keeps the same order, label and tracking identity. Reprinting cannot create a second charge. A label receipt and a captured handoff each have independent evidence. |

The inspections establish product patterns and screen behavior as documented. Correctness of V2 quantity transitions, actual hardware behavior and BrickLink protocol semantics require the separate acceptance work in the master plan.
