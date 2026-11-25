"""Confirm a later exact recount without writing off or receiving inventory."""

from copy import deepcopy
from uuid import uuid4

from verify_receiving import post, purchase_row


def closure_receipt(client, path):
    record = client.call(path)
    assert record.pop("asOf") and record["complete"] and record["scope"] == "shortage-closure"
    return record


def qualify_shortage_closure(client, catalog):
    code = "EXACT-RECOUNT"
    _, location = post(client, "locations", 0, code=code, name="Exact recount shelf")
    location_id = location["entityId"]
    _, order = post(client, "receiving/purchases", 0, reference="Recount purchase", source="Fictional supplier",
                    currency="USD", orderedQuantity=8, pendingChargeMinor=None)
    purchase = order["entityId"]
    _, lot = post(client, "receiving/deliveries", order["revision"], purchaseId=purchase, reference="Eight pieces",
                  catalogId=catalog, quantity=8, itemCostMinor=803)
    _, position = post(client, "receiving/putaway", lot["revision"], purchaseId=purchase, deliveryId=lot["entityId"],
                       catalogId=catalog, locationId=location_id, confirmedLocationCode=code, quantity=8)
    pos = position["entityId"]
    where = dict(purchaseId=purchase, positionId=pos, locationId=location_id, confirmedLocationCode=code)
    revision = client.call(f"inventory/positions?positionId={pos}")["rows"][0]["layer"]["currentCatalog"]["revision"]
    _, marked = post(client, "inventory/quarantines", position["revision"], **where, catalogId=catalog,
                     catalogRevision=revision, quantity=1, reason="Cracked edge")
    hold_fields = dict(**where, quantity=2, purpose="Bridge build", reason="Set aside identified pieces")
    _, hold = post(client, "receiving/holds", marked["revision"], **hold_fields)
    _, offer = post(client, "receiving/offers", hold["revision"], purchaseId=purchase, catalogId=catalog,
                    unitPriceMinor=139, backing=[{"placementId": pos, "quantity": 3}])

    endpoint = "receiving/shortage-closures"
    count_path = f"receiving/bins/{location_id}/counts"
    _, short = post(client, "receiving/counts", offer["revision"], **where, countedQuantity=6,
                    reason="Two pieces missing from this bin")
    case = client.call(count_path + "?observationId=" + short["entityId"])["shortages"][0]["id"]
    receipt_path = f"receiving/bins/{location_id}/positions/{pos}/shortages/{case}/closure"
    client.call(receipt_path, expected=404)
    fields = dict(**where, caseId=case, reason="Verified the receipt identity, condition, and all eight pieces")

    def reject(witness, *, expected_revision=None):
        client.call(endpoint, "POST", dict(**fields, commandId=str(uuid4()), expectedObservationId=witness["entityId"],
                    expectedRevision=expected_revision or witness["revision"]), expected=409)

    reject(short)
    _, surplus = post(client, "receiving/counts", short["revision"], **where, countedQuantity=9,
                      reason="Extra unidentified piece in the tray")
    reject(surplus)
    _, exact = post(client, "receiving/counts", surplus["revision"], **where, countedQuantity=8,
                    reason="Separated the extra piece and verified this receipt position")
    reject(surplus, expected_revision=exact["revision"])
    client.call("receiving/holds", "POST", dict(**hold_fields, commandId=str(uuid4()),
                expectedRevision=exact["revision"]), expected=409)
    before = purchase_row(client, purchase)
    body, saved = post(client, endpoint, exact["revision"], **fields, expectedObservationId=exact["entityId"])
    record = closure_receipt(client, receipt_path)
    assert saved["kind"] == "shortage-closure" and saved["revision"] == exact["revision"] + 1
    assert record["commandId"] == body["commandId"] and record["id"] == saved["entityId"]
    assert record["openingObservation"]["id"] == short["entityId"] and record["recountObservation"]["id"] == exact["entityId"]
    assert (record["ledgerQuantity"], record["quarantinedQuantity"], record["heldQuantity"], record["offerBackedQuantity"]) == (8, 1, 2, 3)
    assert record["receipt"]["itemCostMinor"] == 803 and record["reason"] == fields["reason"]
    after = purchase_row(client, purchase)
    expected = deepcopy(before)
    expected["revision"] = saved["revision"]
    placement = expected["deliveries"][0]["placements"][0]
    assert placement["id"] == pos and placement["freeToBackQuantity"] == 0
    placement["freeToBackQuantity"] = 2
    assert after == expected
    assert not client.call(f"inventory/positions/{pos}/quarantine?lossAttribution=true")["hasOpenShortage"]

    # Newly eligible stock can support a new hold, then a fresh shortage has its own case.
    _, next_hold = post(client, "receiving/holds", saved["revision"], **{**hold_fields, "quantity": 1, "purpose": "Roof build"})
    _, later = post(client, "receiving/counts", next_hold["revision"], **where, countedQuantity=7,
                    reason="A later independent shortage")
    other_case = client.call(count_path + "?observationId=" + later["entityId"])["shortages"][0]["id"]
    assert other_case != case
    evidence = (body, saved, receipt_path, record, count_path, purchase_row(client, purchase), other_case)
    verify_recovered_closure(client, evidence)
    return evidence


def verify_recovered_closure(client, evidence):
    body, saved, path, record, counts, purchase, other_case = evidence
    assert client.call("receiving/shortage-closures", "POST", body) == saved
    assert closure_receipt(client, path + "?commandId=" + body["commandId"]) == record
    client.call(path + "?commandId=" + str(uuid4()), expected=404)
    for name in ("openingObservation", "recountObservation"):
        history = client.call(counts + "?closures=true&observationId=" + record[name]["id"])
        assert history["complete"] and [c["caseId"] for c in history["closures"]] == [record["caseId"]]
    assert purchase_row(client, body["purchaseId"]) == purchase
    current = client.call(counts + "?positionId=" + body["positionId"])
    assert any(case["id"] == other_case for case in current["shortages"])
    print("Verified exact recount closure, unchanged stock and commitments, scoped receipt replay and the later independent shortage.", flush=True)
