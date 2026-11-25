"""Qualify inspection, cost restoration and replay for pieces recovered against a posted loss."""

from uuid import uuid4

from verify_receiving import post, purchase_row
from verify_recounts import read_rows

RECOVER = "receiving/stock-loss-recoveries"


def receive(client, catalog, code, quantity, cost):
    _, location = post(client, "locations", 0, code=code, name="Recovery inspection shelf")
    _, buy = post(client, "receiving/purchases", 0, reference=code, source="Fictional supplier",
                  currency="USD", orderedQuantity=quantity, pendingChargeMinor=None)
    _, delivery = post(client, "receiving/deliveries", buy["revision"], purchaseId=buy["entityId"],
                       reference="Inspected pieces", catalogId=catalog, quantity=quantity, itemCostMinor=cost)
    _, placed = post(client, "receiving/putaway", delivery["revision"], purchaseId=buy["entityId"],
                     deliveryId=delivery["entityId"], catalogId=catalog, locationId=location["entityId"],
                     confirmedLocationCode=code, quantity=quantity)
    where = dict(purchaseId=buy["entityId"], locationId=location["entityId"], positionId=placed["entityId"],
                 confirmedLocationCode=code)
    return where, delivery["entityId"], placed["revision"]


def post_loss(client, where, revision, counted, attributions=None):
    _, count = post(client, "receiving/counts", revision, **where, countedQuantity=counted,
                    reason="Confirmed the identified units still in this bin")
    fields = {} if attributions is None else {"quarantineAttributions": attributions}
    return post(client, "receiving/stock-losses", count["revision"], **where, caseId=count["entityId"],
                expectedObservationId=count["entityId"], offerReductions=[], reason="Confirmed missing units", **fields)


def inspected(source, saleable, quarantine):
    return dict(quarantineId=source, saleableQuantity=saleable, quarantineQuantity=quarantine)


def recovery(client, where, catalog, catalog_revision, revision, loss, allocations):
    return post(client, RECOVER, revision, **where, postingId=loss["entityId"], catalogId=catalog,
                catalogRevision=catalog_revision, confirmedIdentity=True,
                reason="Identified pieces against the original receipt and inspected condition", allocations=allocations)


def loss_path(where, posting_id):
    return f"receiving/bins/{where['locationId']}/positions/{where['positionId']}/posted-loss/{posting_id}"


def exact_receipt(client, body, saved):
    path = loss_path(body, body["postingId"]) + "/recovery-receipt?commandId=" + body["commandId"]
    page = client.call(path)
    assert page.pop("asOf") and page["complete"]
    assert page["commandId"] == body["commandId"] and page["postingId"] == body["postingId"]
    assert page["positionId"] == body["positionId"] and page["purchaseId"] == body["purchaseId"]
    assert saved["kind"] == "stock-loss-recovery" and page["id"] == saved["entityId"]
    assert (page["revision"], page["correlationId"]) == (saved["revision"], saved["correlationId"])
    assert page["reason"] == body["reason"] and page["confirmedLocationCode"] == body["confirmedLocationCode"]
    assert page["location"]["id"] == body["locationId"] and page["recordedCatalog"]["id"] == body["catalogId"]
    assert page["nextAfter"] is None
    lines = page["allocations"]
    def quantities(rows):
        return {row["quarantineId"]: (row["saleableQuantity"], row["quarantineQuantity"]) for row in rows}
    assert quantities(lines) == quantities(body["allocations"]) and len(lines) == len(body["allocations"])
    assert page["saleableQuantity"] == sum(row["saleableQuantity"] for row in lines)
    assert page["quarantineQuantity"] == sum(row["quarantineQuantity"] for row in lines)
    assert page["recoveredQuarantineId"] == (saved["entityId"] if page["quarantineQuantity"] else None)
    values = [row["restoredItemCostMinor"] for row in lines]
    if page["restoredItemCostMinor"] is None:
        assert all(value is None for value in values)
    else:
        assert sum(values) == page["restoredItemCostMinor"]
    by_id = client.call(loss_path(body, body["postingId"]) + "/recovery-receipt?recoveryId=" + saved["entityId"])
    assert by_id.pop("asOf") and by_id == page
    return page


def check_delivery(client, where, delivery, *, on_hand, missing, gross, recovered, gross_cost, restored, net_cost):
    purchase = purchase_row(client, where["purchaseId"])
    layer = next(row for row in purchase["deliveries"] if row["id"] == delivery)
    lot = client.call(f"inventory/receipt-lots/{delivery}?withdrawals=true&quarantine=true&closures=true&recoveries=true")
    totals = dict(grossLostQuantity=gross, recoveredQuantity=recovered,
                  grossAllocatedItemCostMinor=None if gross_cost is None else str(gross_cost),
                  restoredItemCostMinor=None if restored is None else str(restored))
    assert layer["recoveryTotals"] == lot["recoveryTotals"] == totals
    assert layer["lostQuantity"] == lot["lostQuantity"] == missing
    assert purchase["balances"]["physicalOnHand"] == lot["currentOnHandQuantity"] == on_hand
    assert layer["allocatedLostItemCostMinor"] == lot["allocatedLostItemCostMinor"] == net_cost
    original_cost = layer["itemCostMinor"]
    assert layer["remainingItemCostMinor"] == lot["remainingItemCostMinor"] == (
        None if original_cost is None else original_cost - net_cost
    )
    return purchase


def qualify_rounding(client, catalog, catalog_revision):
    where, delivery, rev = receive(client, catalog, "RECOVER-COST", 3, 100)
    _, loss_a = post_loss(client, where, rev, 2)
    _, loss_b = post_loss(client, where, loss_a["revision"], 0)
    first = recovery(client, where, catalog, catalog_revision, loss_b["revision"], loss_b, [inspected(None, 0, 1)])
    body, saved = first
    purchase = check_delivery(client, where, delivery, on_hand=1, missing=2, gross=3, recovered=1,
                              gross_cost=100, restored=33, net_cost=67)
    assert purchase["balances"]["unavailable"] == 1
    client.call("receiving/current?holds=true&losses=true&quarantine=true", expected=426)
    client.call(f"inventory/receipt-lots/{delivery}?withdrawals=true&quarantine=true&closures=true", expected=426)
    review = client.call(loss_path(where, loss_b["entityId"]) + "/recovery-review")
    assert (review["originalLostQuantity"], review["recoveredQuantity"], review["remainingQuantity"]) == (2, 1, 1)
    assert (review["originalAllocatedItemCostMinor"], review["restoredItemCostMinor"], review["remainingLostItemCostMinor"]) == (67, 33, 34)
    assert review["recoveries"][0]["id"] == saved["entityId"] and len(review["sources"]) == 1
    quarantine_id = saved["entityId"]
    _, release = post(client, "inventory/quarantine-releases", saved["revision"], **where, catalogId=catalog,
                      catalogRevision=catalog_revision, quarantineId=quarantine_id, quantity=1,
                      reason="Surface inspected again", inspectionResult="passed")
    second = recovery(client, where, catalog, catalog_revision, release["revision"], loss_b, [inspected(None, 1, 0)])
    purchase = check_delivery(client, where, delivery, on_hand=2, missing=1, gross=3, recovered=2,
                              gross_cost=100, restored=67, net_cost=33)
    assert purchase["balances"]["unavailable"] == 0

    _, lost_again = post_loss(client, where, second[1]["revision"], 0)
    check_delivery(client, where, delivery, on_hand=0, missing=3, gross=5, recovered=2,
                   gross_cost=167, restored=67, net_cost=100)
    denied = {**body, "commandId": str(uuid4()), "expectedRevision": lost_again["revision"]}
    client.call(RECOVER, "POST", denied, expected=409)
    third = recovery(client, where, catalog, catalog_revision, lost_again["revision"], loss_a, [inspected(None, 1, 0)])
    check_delivery(client, where, delivery, on_hand=1, missing=2, gross=5, recovered=3,
                   gross_cost=167, restored=100, net_cost=67)
    _, final_loss = post_loss(client, where, third[1]["revision"], 0)
    final = check_delivery(client, where, delivery, on_hand=0, missing=3, gross=6, recovered=3,
                           gross_cost=200, restored=100, net_cost=100)
    assert final["revision"] == final_loss["revision"]
    parent = loss_path(where, final_loss["entityId"]).rsplit("/", 1)[0]
    prior = parent + "?quarantine=true&postingId=" + final_loss["entityId"]
    client.call(prior, expected=426)
    assert client.call(prior + "&recoveries=true")["priorDeliveryLossQuantityMode"] == "active"
    return [(*row, exact_receipt(client, *row)) for row in (first, second, third)], final


def qualify_mixed(client, catalog, catalog_revision, code, cost):
    where, delivery, rev = receive(client, catalog, code, 8, cost)
    _, hold = post(client, "receiving/holds", rev, **where, quantity=1, purpose="Build", reason="Reserve one")
    _, offer = post(client, "receiving/offers", hold["revision"], purchaseId=where["purchaseId"], catalogId=catalog,
                    unitPriceMinor=117, backing=[{"placementId": where["positionId"], "quantity": 1}])
    ids = []
    rev = offer["revision"]
    for quantity in (2, 1):
        _, q = post(client, "inventory/quarantines", rev, **where, catalogId=catalog, catalogRevision=catalog_revision,
                    quantity=quantity, reason="Surface damage")
        ids.append(q["entityId"])
        rev = q["revision"]
    sources = [dict(quarantineId=qid, quantity=qty) for qid, qty in zip(ids, (2, 1))]
    _, loss = post_loss(client, where, rev, 4, sources)
    _, other_case = post(client, "receiving/counts", loss["revision"], **where, countedQuantity=3,
                         reason="A later independent count found a shortage")
    before = purchase_row(client, where["purchaseId"])
    body, saved = recovery(client, where, catalog, catalog_revision, other_case["revision"], loss,
                           [inspected(ids[0], 1, 0), inspected(None, 0, 1)])
    final = check_delivery(client, where, delivery, on_hand=6, missing=2, gross=4, recovered=2,
                           gross_cost=cost, restored=cost, net_cost=cost)
    balances = final["balances"]
    assert (balances["unavailable"], balances["reserved"], balances["backedQuantity"]) == (1, 1, 1)
    assert final["offers"] == before["offers"]
    query = f"receiving/bins/{where['locationId']}/counts?positionId={where['positionId']}"
    assert [case["id"] for case in client.call(query)["shortages"]] == [other_case["entityId"]]
    for invalid in ([inspected(None, 1, 0)], [inspected(ids[0], 2, 0)], [inspected(str(uuid4()), 1, 0)]):
        client.call(RECOVER, "POST", {**body, "commandId": str(uuid4()), "expectedRevision": saved["revision"],
                    "allocations": invalid}, expected=(400, 409))
    assert purchase_row(client, where["purchaseId"]) == final
    return [(body, saved, exact_receipt(client, body, saved))], final


def qualify_recoveries(client, catalog):
    catalog_revision = next(row["revision"] for row in client.call("workspace")["catalog"] if row["id"] == catalog)
    cases = [qualify_rounding(client, catalog, catalog_revision),
             qualify_mixed(client, catalog, catalog_revision, "RECOVER-UNKNOWN", None),
             qualify_mixed(client, catalog, catalog_revision, "RECOVER-ZERO", 0)]
    verify_recovered_inspections(client, cases)
    return cases


def verify_recovered_inspections(client, cases):
    for commands, purchase in cases:
        for body, saved, evidence in commands:
            assert client.call(RECOVER, "POST", body) == saved
            assert exact_receipt(client, body, saved) == evidence
        assert purchase_row(client, purchase["id"]) == purchase
    print("Verified inspected recoveries, named quarantine sources, original loss cents, later losses and exact replay.", flush=True)


def qualify_repopulated_recount(client, catalog):
    where, _, revision = receive(client, catalog, "RECOVER-QUEUE", 5, 500)
    _, loss = post_loss(client, where, revision, 3)
    position = where["positionId"]
    old_batch = queue_selected(client, position)
    _, destination = post(client, "locations", 0, code="RECOVER-MOVED", name="Fictional transfer bin")
    _, moved = post(client, "receiving/transfers", loss["revision"], purchaseId=where["purchaseId"],
                    sourcePlacementId=position, sourceLocationId=where["locationId"],
                    confirmedSourceLocationCode=where["confirmedLocationCode"],
                    destinationLocationId=destination["entityId"], confirmedDestinationLocationCode="RECOVER-MOVED",
                    quantity=3, reason="Moved the three remaining pieces", backing=[])
    assert recount_row(client, old_batch)["taskState"] == "depleted"
    catalog_revision = next(row["revision"] for row in client.call("workspace")["catalog"] if row["id"] == catalog)
    _, restored = recovery(client, where, catalog, catalog_revision, moved["revision"], loss, [inspected(None, 1, 0)])
    assert recount_row(client, old_batch)["taskState"] == "depleted"
    assert all(row["target"]["positionId"] != position for row in read_rows(client, "inventory/recounts/queue"))
    new_batch = queue_selected(client, position)
    assert recount_row(client, new_batch)["outcome"] == "queued"
    assert recount_row(client, new_batch)["taskState"] == "pending"
    _, count = post(client, "receiving/counts", restored["revision"], **where, countedQuantity=1,
                    reason="Witnessed the piece that returned to this position")
    evidence = old_batch, new_batch, count["entityId"]
    verify_repopulated_recount(client, evidence)
    return evidence


def queue_selected(client, position):
    preview = dict(previewId=str(uuid4()), scope="selected", positionIds=[position],
                   q=None, itemType=None, locationId=None)
    result = client.call("inventory/recounts/previews", "POST", preview, expected=(200, 201))
    assert result["complete"] and result["previewId"] == preview["previewId"]
    body = dict(commandId=str(uuid4()), previewId=result["previewId"])
    receipt = client.call("inventory/recounts/batches", "POST", body, expected=(200, 201))
    assert receipt["kind"] == "recount" and receipt["entityId"] == result["previewId"]
    return body, receipt


def recount_row(client, batch):
    body, saved = batch
    assert client.call("inventory/recounts/batches", "POST", body) == saved
    row, = read_rows(client, "inventory/recounts/batches/" + saved["entityId"])
    return row


def verify_repopulated_recount(client, evidence):
    original, replacement, observed_id = evidence
    old = recount_row(client, original)
    new = recount_row(client, replacement)
    assert (old["outcome"], old["taskState"], old["observationId"]) == ("queued", "depleted", None)
    assert (new["outcome"], new["taskState"], new["observationId"]) == ("queued", "witnessed", observed_id)
    print("Verified old depleted recount stays terminal and the new batch owns the post-recovery witness.", flush=True)
