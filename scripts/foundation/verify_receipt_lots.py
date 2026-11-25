"""Trace split receipts and posted losses across positions before and after encrypted recovery."""

from urllib.parse import urlencode


def read_lot(client, delivery_id, *, quarantine=False, withdrawals=False):
    path = f"inventory/receipt-lots/{delivery_id}"
    limits = {"positionLimit": 1, "eventLimit": 2}
    if quarantine:
        limits["quarantine"] = "true"
    if withdrawals:
        limits["withdrawals"] = "true"
    first = client.call(path + "?" + urlencode(limits))
    assert first["complete"] and first["scope"] == "receipt-lot" and first["deliveryId"] == delivery_id
    variable = {"asOf", "positions", "events", "nextPositionAfter", "nextEventAfter"}
    header = {key: value for key, value in first.items() if key not in variable}

    def collect(field, next_key, after):
        page = first
        rows = list(page[field])
        seen = set()
        while cursor := page[next_key]:
            assert cursor not in seen
            seen.add(cursor)
            page = client.call(path + "?" + urlencode({**limits, after: cursor}))
            assert page["asOf"] and {k: v for k, v in page.items() if k not in variable} == header
            rows.extend(page[field])
        return rows

    positions = collect("positions", "nextPositionAfter", "positionAfter")
    events = collect("events", "nextEventAfter", "eventAfter")
    layers = [p["layer"] for p in positions]
    assert len({p["placementId"] for p in layers}) == len(layers) == header["positionCount"]
    assert all(p["deliveryId"] == delivery_id for p in layers)
    assert sum(p["quantity"] for p in layers) == header["positionedQuantity"]
    assert sum(p["backedQuantity"] for p in layers) == header["offerBackedQuantity"]
    assert len({(row["positionId"], row["event"]["kind"], row["event"]["id"]) for row in events}) == len(events)
    revisions = [row["event"]["purchaseRevision"] for row in events]
    assert revisions == sorted(revisions, reverse=True)
    assert header["receivedQuantity"] == header["unplacedQuantity"] + header["positionedQuantity"] + header["lostQuantity"]
    assert header["currentOnHandQuantity"] == header["unplacedQuantity"] + header["positionedQuantity"]
    return header, layers, events


def verify_receipt_lots(client, receiving, transfer, loss):
    purchase = receiving[0]
    deliveries = {d["reference"]: d for d in purchase["deliveries"]}
    first = read_lot(client, deliveries["A"]["id"])
    sibling = read_lot(client, deliveries["B"]["id"])
    posted = read_lot(client, loss[3]["receipt"]["deliveryId"])
    head, _, events = first
    assert (head["receivedQuantity"], head["positionedQuantity"], head["itemCostMinor"]) == (8, 8, 800)
    transfers = [r for r in events if r["event"]["id"] in {transfer[4]["entityId"], transfer[6]["entityId"]}]
    assert sorted(r["event"]["quantity"] for r in transfers) == [1, 4]
    assert sum(r["transferBackedQuantity"] for r in transfers) == 3
    assert all(r["event"]["physicalDelta"] == r["event"]["offerBackedDelta"] == 0 for r in transfers)
    assert (sibling[0]["receivedQuantity"], sibling[0]["unplacedQuantity"], sibling[0]["itemCostMinor"]) == (4, 1, None)
    assert {r["placementId"] for r in sibling[1]}.isdisjoint(r["placementId"] for r in first[1])
    head, _, events = posted
    assert (head["receivedQuantity"], head["lostQuantity"], head["positionedQuantity"], head["heldQuantity"],
            head["offerBackedQuantity"], head["allocatedLostItemCostMinor"], head["remainingItemCostMinor"]) == (
                10, 5, 5, 2, 3, 500, 501)
    assert any(r["event"]["postingId"] == loss[3]["postingId"] for r in events)
    return first, sibling, posted
