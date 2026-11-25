"""Qualify post-putaway quarantine through the same API used by Workbench and recovery."""

from uuid import uuid4

from verify_receipt_lots import read_lot
from verify_receiving import post, purchase_row


QUARANTINE = "inventory/quarantines"
RELEASE = "inventory/quarantine-releases"


def check_position(client, purchase, cost, unavailable):
    row = purchase_row(client, purchase)
    balances = row["balances"]
    free = 8 - 2 - unavailable
    assert (balances["physicalOnHand"], balances["unavailable"], balances["reserved"],
            balances["free"], balances["backedQuantity"], balances["withheldQuantity"]) == (
                8, unavailable, 2, free, 3, free - 3)
    assert (balances["knownCostFreeQuantity"], balances["unknownCostFreeQuantity"]) == (
        (0, free) if cost is None else (free, 0))
    assert row["currency"] == "USD" and len(row["deliveries"]) == 1
    assert row["deliveries"][0]["itemCostMinor"] == cost
    assert len(row["offers"]) == 1 and row["offers"][0]["backedQuantity"] == 3
    return row


def qualify_quarantine(client, catalog):
    code = "INSPECT-01"
    _, location = post(client, "locations", 0, code=code, name="Inspection training shelf")
    bin_id = location["entityId"]
    recorded_item = dict(itemType="part", itemNumber="INSPECT-33", variant=None, name="Inspection sample",
                         colorId=0, colorState="known", condition="new", completeness="complete",
                         source="Fictional inspection", license="operator-owned", mapping="confirmed")
    _, editable = post(client, "catalog", 0, **recorded_item)
    results = []
    for name, cost in (("Paid", 839), ("Documented zero", 0), ("Unknown", None)):
        item = editable["entityId"] if cost is None else catalog
        _, order = post(client, "receiving/purchases", 0, reference="Inspection " + name,
                        source="Fictional purchase", currency="USD", orderedQuantity=8, pendingChargeMinor=None)
        purchase = order["entityId"]
        _, delivery = post(client, "receiving/deliveries", order["revision"], purchaseId=purchase,
                           reference=name, quantity=8, catalogId=item, itemCostMinor=cost)
        lot = delivery["entityId"]
        _, placement = post(client, "receiving/putaway", delivery["revision"], purchaseId=purchase,
                            deliveryId=lot, catalogId=item, locationId=bin_id, confirmedLocationCode=code, quantity=8)
        position = placement["entityId"]
        fields = dict(purchaseId=purchase, positionId=position, locationId=bin_id, confirmedLocationCode=code)
        _, hold = post(client, "receiving/holds", placement["revision"], **fields, quantity=2,
                       purpose="Training build", reason="Keep two pieces together")
        _, offer = post(client, "receiving/offers", hold["revision"], purchaseId=purchase, catalogId=item,
                        unitPriceMinor=175, backing=[{"placementId": position, "quantity": 3}])
        layer = client.call("inventory/positions?positionId=" + position)["rows"][0]["layer"]
        identity = dict(catalogId=item, catalogRevision=layer["currentCatalog"]["revision"])
        blocked = dict(commandId=str(uuid4()), expectedRevision=offer["revision"], **fields, **identity,
                       quantity=4, reason="Includes committed stock")
        client.call(QUARANTINE, "POST", blocked, expected=409)
        enter, received = post(client, QUARANTINE, offer["revision"], **fields, **identity,
                               quantity=2, reason="Two pieces require inspection")
        check_position(client, purchase, cost, 2)
        leave, cleared = post(client, RELEASE, received["revision"], **fields, **identity, quantity=1,
                              quarantineId=received["entityId"], inspectionResult="passed",
                              reason="Inspected one piece and confirmed condition")
        after = check_position(client, purchase, cost, 1)
        assert after["revision"] == cleared["revision"]
        results.append((purchase, lot, position, cost, enter, received, leave, cleared))
    post(client, "catalog", editable["revision"], id=editable["entityId"],
         **{**recorded_item, "name": "Revised inspection sample", "condition": "used"})
    verify_recovered_quarantine(client, results)
    return results


def verify_recovered_quarantine(client, results):
    for purchase, lot, position, cost, enter, received, leave, cleared in results:
        assert client.call(QUARANTINE, "POST", enter) == received
        assert client.call(RELEASE, "POST", leave) == cleared
        assert check_position(client, purchase, cost, 1)["revision"] == cleared["revision"]
        path = f"inventory/positions/{position}/quarantine"
        state = client.call(path)
        assert state["complete"] and state["purchaseRevision"] == cleared["revision"]
        assert (state["onHandQuantity"], state["quarantinedQuantity"], state["heldQuantity"],
                state["offerBackedQuantity"], state["sellableQuantity"], state["availableToQuarantineQuantity"]) == (
                    8, 1, 2, 3, 5, 2)
        marked, = state["quarantines"]
        assert (marked["id"], marked["quantity"], marked["releasedQuantity"], marked["remainingQuantity"]) == (
            received["entityId"], 2, 1, 1)
        header, positions, events = read_lot(client, lot, quarantine=True)
        assert (header["currentOnHandQuantity"], header["heldQuantity"], header["offerBackedQuantity"],
                header["remainingItemCostMinor"]) == (8, 2, 3, cost)
        assert len(positions) == 1 and positions[0]["placementId"] == position
        assert positions[0]["quantity"] == 8
        if cost is None:
            assert positions[0]["currentCatalog"]["condition"] == "used"
            assert positions[0]["freeToBackQuantity"] == 0
        else:
            assert positions[0]["freeToBackQuantity"] == 2
        history = client.call(f"inventory/positions/{position}/timeline?quarantine=true")
        for submitted, saved in ((enter, received), (leave, cleared)):
            by_id = client.call(path + "/receipts?receiptId=" + saved["entityId"])
            by_command = client.call(path + "/receipts?commandId=" + saved["commandId"])
            assert by_id.pop("asOf") and by_command.pop("asOf") and by_id == by_command
            assert by_id["complete"] and by_id["scope"] == "quarantine-receipt"
            assert (by_id["id"], by_id["kind"], by_id["revision"], by_id["quarantineId"]) == (
                saved["entityId"], saved["kind"], saved["revision"], received["entityId"])
            for key in ("purchaseId", "positionId", "catalogId", "catalogRevision", "confirmedLocationCode",
                        "commandId", "quantity", "reason"):
                assert by_id[key] == submitted[key]
            assert by_id["inspectionResult"] == submitted.get("inspectionResult")
            observed = by_id["recordedCatalog"]
            assert (observed["id"], observed["revision"]) == (submitted["catalogId"], submitted["catalogRevision"])
            if cost is None:
                assert (observed["itemNumber"], observed["name"], observed["condition"]) == (
                    "INSPECT-33", "Inspection sample", "new")
            change = next(e["event"] for e in events if e["event"]["commandId"] == saved["commandId"])
            assert change["physicalDelta"] == change["reservedDelta"] == change["offerBackedDelta"] == 0
            assert any(e["commandId"] == saved["commandId"] for e in history["events"])
            diag = client.call("diagnostics/" + saved["correlationId"])
            assert diag["kind"] == saved["kind"] and diag["entityId"] == saved["entityId"]
    print("Quarantine and inspected release preserved physical stock, project holds, offer backing and all three cost cases.", flush=True)
