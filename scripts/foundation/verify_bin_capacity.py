"""Exercise operator-authored capacity, netted transformations and durable policy receipts."""
from uuid import uuid4

from verify_part_outs import PART_OUT, exact, output, source_fields
from verify_receiving import post, purchase_row

POLICY = "locations/capacities"
PUTAWAY = "receiving/putaway"


def snapshot(client, location):
    page = client.call(f"locations/{location}/capacity")
    assert page.pop("asOf") and page["scope"] == "location-capacity" and page["complete"]
    assert page["bin"]["location"]["id"] == location
    return page


def policy_receipt(client, location, command, receipt):
    base = f"locations/{location}/capacity/receipts?"
    proof = client.call(base + "commandId=" + command["commandId"])
    assert proof.pop("asOf") and proof["scope"] == "location-capacity-receipt" and proof["complete"]
    row = proof["event"]
    assert proof["locationId"] == location
    assert (row["id"], row["commandId"], row["correlationId"], row["revision"]) == (
        receipt["entityId"], command["commandId"], receipt["correlationId"], receipt["revision"])
    assert (row["pieceLimit"], row["reason"]) == (command["pieceLimit"], command["reason"])
    same = client.call(base + "eventId=" + row["id"])
    assert same.pop("asOf") and same == proof
    return proof


def qualify_capacity(client):
    _, bin_record = post(client, "locations", 0, code="CAPACITY-SHELF", name="Fictional limited shelf")
    location = bin_record["entityId"]
    initial = snapshot(client, location)
    assert initial["bin"]["capacity"] == dict(pieceLimit=None, remainingQuantity=None, overCapacityQuantity=0)
    assert not initial["events"]
    assert "capacity" not in client.call(f"locations/{location}/registry")["bin"]

    changes = []
    def set_limit(limit, reason):
        before = snapshot(client, location)["bin"]
        command, receipt = post(client, POLICY, before["location"]["revision"],
                                locationId=location, pieceLimit=limit, reason=reason)
        proof = policy_receipt(client, location, command, receipt)
        event = proof["event"]
        assert event["previousPieceLimit"] == before["capacity"]["pieceLimit"]
        assert (event["physicalQuantity"], event["positionCount"]) == (before["physicalQuantity"], before["positionCount"])
        changes.append((command, receipt, proof))

    set_limit(4, "Four pieces fit this shelf")
    entries = []
    for kind, name in (("set", "Kit"), ("part", "Axle")):
        _, item = post(client, "catalog", 0, itemType=kind, itemNumber="LIMIT-"+kind, name=name, variant=None,
                       colorId=0, colorState="known", condition="new", completeness="complete",
                       source="Fictional capacity test", license="operator-owned", mapping="confirmed")
        entries.append(item)
    kit, axle = entries
    _, order = post(client, "receiving/purchases", 0, reference="Capacity test", source="Fictional supplier",
                    currency="USD", orderedQuantity=5, pendingChargeMinor=None)
    purchase = order["entityId"]
    _, delivery = post(client, "receiving/deliveries", order["revision"], purchaseId=purchase, quantity=5,
                       reference="Five kits", catalogId=kit["entityId"], itemCostMinor=125)
    fields = dict(purchaseId=purchase, catalogId=kit["entityId"], deliveryId=delivery["entityId"],
                  locationId=location, confirmedLocationCode="CAPACITY-SHELF")
    placement, placed = post(client, PUTAWAY, delivery["revision"], **fields, quantity=3)
    before = snapshot(client, location)
    assert before["bin"]["capacity"]["remainingQuantity"] == 1
    request = dict(commandId=str(uuid4()), expectedRevision=placed["revision"], **fields, quantity=2)
    legacy = client.call(PUTAWAY, "POST", request, expected=409)
    assert legacy["code"] == "quantity_unavailable" and set(legacy) == {"code", "message", "correlationId"}
    denied = client.call(PUTAWAY, "POST", request, headers={"X-BV-Location-Contract": "2"}, expected=409)
    assert denied["code"] == "location_capacity_exceeded"
    assert denied["capacity"] == dict(locationId=location, pieceLimit=4, physicalQuantity=3, remainingQuantity=1, requestedNetQuantity=2)
    assert snapshot(client, location) == before
    assert purchase_row(client, purchase)["revision"] == placed["revision"]

    source = dict(purchaseId=purchase, locationId=location, positionId=placed["entityId"], confirmedLocationCode="CAPACITY-SHELF")
    lines = [output(axle, source, 1, 10, "stock"), output(axle, source, 1, 15, "retained")]
    part, result = post(client, PART_OUT, placed["revision"], **source_fields(source, kit, 1, lines, 0, []))
    transformation = exact(client, part, result)
    assert snapshot(client, location)["bin"]["capacity"] == dict(pieceLimit=4, remainingQuantity=0, overCapacityQuantity=0)
    row = purchase_row(client, purchase)
    assert row["balances"]["knownItemCostMinor"] == 125 and row["balances"]["unavailable"] >= 1

    set_limit(2, "Use a smaller tray after moving these pieces")
    row = snapshot(client, location)["bin"]
    assert row["physicalQuantity"] == 4 and row["capacity"] == dict(pieceLimit=2, remainingQuantity=0, overCapacityQuantity=2)
    assert client.call(PART_OUT, "POST", part, expected=(200, 201)) == result
    assert client.call(PUTAWAY, "POST", placement, expected=(200, 201)) == placed
    set_limit(None, "Remove the tray-specific piece limit")
    state = snapshot(client, location)
    assert state["bin"]["physicalQuantity"] == 4 and state["bin"]["capacity"] == initial["bin"]["capacity"]
    assert [e["id"] for e in state["events"]] == [receipt["entityId"] for _, receipt, _ in reversed(changes)]
    # The receipt includes a current location reference alongside the label recorded on each movement.
    current_revision = state["bin"]["location"]["revision"]
    transformation["sourceLocation"]["revision"] = current_revision
    for line in transformation["outputs"]:
        assert line["location"]["id"] == location
        line["location"]["revision"] = current_revision
    assert exact(client, part, result) == transformation
    return dict(location=location, purchase=purchase_row(client, purchase), state=state,
                changes=changes, stock=(placement, placed), part=(part, result, transformation))


def verify_restored_capacity(client, saved):
    location = saved["location"]
    assert snapshot(client, location) == saved["state"]
    assert purchase_row(client, saved["purchase"]["id"]) == saved["purchase"]
    for body, receipt, proof in saved["changes"]:
        assert client.call(POLICY, "POST", body, expected=(200, 201)) == receipt
        assert policy_receipt(client, location, body, receipt) == proof
    body, receipt = saved["stock"]
    assert client.call(PUTAWAY, "POST", body, expected=(200, 201)) == receipt
    body, receipt, transform = saved["part"]
    assert client.call(PART_OUT, "POST", body, expected=(200, 201)) == receipt
    assert exact(client, body, receipt) == transform
    print("Restored bin policies, over-capacity and cleared-limit receipts, stock and netted part-out costs verified.", flush=True)
