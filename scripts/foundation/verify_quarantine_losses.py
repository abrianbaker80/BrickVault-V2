"""Post and recover witnessed losses with explicit quarantine record choices."""

from urllib.parse import urlencode
from uuid import uuid4

from verify_receiving import post, purchase_row


def receipt_pages(client, base, posting_id):
    query = {"postingId": posting_id, "quarantine": "true", "quarantineLimit": 1}
    pages = []
    seen = set()
    while True:
        page = client.call(base + "/posted-loss?" + urlencode(query))
        assert page.pop("asOf") and page["complete"] and page["postingId"] == posting_id
        pages.append(page)
        cursor = page["nextQuarantineAfter"]
        if cursor is None:
            return pages
        assert cursor not in seen
        seen.add(cursor)
        query["quarantineAfter"] = cursor


def qualify_quarantine_losses(client, catalog):
    code = "GRADE-COUNT"
    _, location = post(client, "locations", 0, code=code, name="Inspection count shelf")
    evidence = []
    for damaged_missing, cost in ((0, 601), (1, None), (2, 0)):
        _, order = post(client, "receiving/purchases", 0, reference=f"Inspection count {damaged_missing}",
                        source="Fictional purchase", currency="USD", orderedQuantity=6, pendingChargeMinor=None)
        purchase = order["entityId"]
        _, lot = post(client, "receiving/deliveries", order["revision"], purchaseId=purchase, reference="Six pieces",
                      catalogId=catalog, quantity=6, itemCostMinor=cost)
        _, position = post(client, "receiving/putaway", lot["revision"], purchaseId=purchase, deliveryId=lot["entityId"],
                           catalogId=catalog, locationId=location["entityId"], confirmedLocationCode=code, quantity=6)
        pos = position["entityId"]
        catalog_revision = client.call("inventory/positions?positionId=" + pos)["rows"][0]["layer"]["currentCatalog"]["revision"]
        where = dict(purchaseId=purchase, positionId=pos, locationId=location["entityId"], confirmedLocationCode=code)
        _, hold = post(client, "receiving/holds", position["revision"], **where, quantity=1,
                       purpose="Training build", reason="Reserve one piece")
        _, offer = post(client, "receiving/offers", hold["revision"], purchaseId=purchase, catalogId=catalog,
                        unitPriceMinor=165, backing=[{"placementId": pos, "quantity": 3}])
        revision = offer["revision"]
        records = []
        for reason in ("Cracked edge", "Worn surface"):
            _, mark = post(client, "inventory/quarantines", revision, **where, catalogId=catalog, catalogRevision=catalog_revision,
                           quantity=1, reason=reason)
            records.append(mark["entityId"])
            revision = mark["revision"]
        _, witness = post(client, "receiving/counts", revision, **where, countedQuantity=4, reason="Four pieces present")
        loc = location["entityId"]
        base = f"receiving/bins/{loc}/positions/{pos}"
        case = client.call(f"receiving/bins/{loc}/counts?observationId=" + witness["entityId"])["shortages"][0]["id"]
        command = dict(**where, caseId=case, expectedObservationId=witness["entityId"], reason="Verified missing pieces",
                       offerReductions=[{"offerId": offer["entityId"], "quantity": 2 - damaged_missing}] if damaged_missing < 2 else [])
        before = purchase_row(client, purchase)
        for unknown in ({}, {"quarantineAttributions": None}):
            client.call("receiving/stock-losses", "POST", dict(**command, **unknown, commandId=str(uuid4()),
                        expectedRevision=witness["revision"]), expected=409)
        assert purchase_row(client, purchase) == before
        choices = [{"quarantineId": rid, "quantity": 1} for rid in records[:damaged_missing]]
        body, saved = post(client, "receiving/stock-losses", witness["revision"], **command, quarantineAttributions=choices)
        pages = receipt_pages(client, base, saved["entityId"])
        original = pages[0]
        assert original["quarantineAttributionRecorded"]
        assert (original["ledgerQuantityBefore"], original["lostQuantity"], original["ledgerQuantityAfter"],
                original["quarantinedQuantityBefore"], original["quarantinedQuantityAfter"]) == (6, 2, 4, 2, 2 - damaged_missing)
        selected = [line for page in pages for line in page["quarantineAttributions"]]
        assert {line["quarantineId"]: line["quantity"] for line in selected} == {rid: 1 for rid in records[:damaged_missing]}
        assert (original["allocatedItemCostMinor"], original["remainingDeliveryItemCostMinor"]) == (
            (None, None) if cost is None else (cost // 3, cost - cost // 3))
        row = purchase_row(client, purchase)
        balances = row["balances"]
        assert (balances["physicalOnHand"], balances["unavailable"], balances["reserved"], balances["free"],
                balances["backedQuantity"]) == (4, 2 - damaged_missing, 1, 1 + damaged_missing, 1 + damaged_missing)
        inventory_path = f"inventory/positions/{pos}/quarantine?lossAttribution=true"
        inventory = client.call(inventory_path)
        assert not inventory["hasOpenShortage"]
        assert {q["id"]: (q["lostQuantity"], q["releasedQuantity"], q["remainingQuantity"]) for q in inventory["quarantines"]} == {
            rid: (int(rid in records[:damaged_missing]), 0, int(rid not in records[:damaged_missing])) for rid in records}
        if damaged_missing == 0:
            post(client, "inventory/quarantine-releases", saved["revision"], **where, catalogId=catalog, catalogRevision=catalog_revision,
                 quarantineId=records[0], quantity=1, reason="Later inspection", inspectionResult="passed")
        assert receipt_pages(client, base, saved["entityId"]) == pages
        evidence.append((base, purchase, body, saved, pages, purchase_row(client, purchase)))
    verify_recovered_quarantine_losses(client, evidence)
    return evidence


def verify_recovered_quarantine_losses(client, evidence):
    for base, purchase, body, saved, pages, expected in evidence:
        assert client.call("receiving/stock-losses", "POST", body) == saved
        assert receipt_pages(client, base, saved["entityId"]) == pages
        assert purchase_row(client, purchase) == expected
    print("Verified explicit zero, mixed and fully quarantined loss with original receipts, costs and commitments.", flush=True)
