"""Verify physical bin labels, retirement, protected destinations and restored receipts."""

from uuid import uuid4

from verify_part_outs import PART_OUT, output, source_fields
from verify_receiving import post, purchase_row

LABEL = "locations/labels"
ARCHIVE = "locations/archives"
PUTAWAY = "receiving/putaway"
TRANSFER = "receiving/transfers"


def lookup(client, label):
    result = client.call("locations/lookup?label=" + label)
    assert result["complete"] and result["scope"] == "location-label-lookup"
    return result


def detail(client, location):
    page = client.call(f"locations/{location}/registry")
    assert page.pop("asOf") and page["scope"] == "location-registry-detail" and page["complete"]
    assert page["bin"]["location"]["id"] == location
    return page


def event(client, location, command, saved):
    path = f"locations/{location}/registry/receipts?"
    proof = client.call(path + "commandId=" + command["commandId"])
    assert proof.pop("asOf") and proof["scope"] == "location-registry-receipt" and proof["complete"]
    row = proof["event"]
    assert proof["locationId"] == location and row["commandId"] == command["commandId"]
    assert row["id"] == saved["entityId"]
    assert (row["revision"], row["correlationId"]) == (saved["revision"], saved["correlationId"])
    same = client.call(path + "eventId=" + row["id"])
    assert same.pop("asOf") and same == proof
    return proof


def qualify_registry(client, catalog):
    _, first = post(client, "locations", 0, code="REG-STOCK", name="Shelf with stock")
    _, second = post(client, "locations", 0, code="REG-NEXT", name="Transfer destination")
    source = first["entityId"]
    target = second["entityId"]
    label_body, label_receipt = post(client, LABEL, first["revision"], locationId=source, label="BOX-LEGACY")
    alias_proof = event(client, source, label_body, label_receipt)
    assert alias_proof["event"]["kind"] == "label-added" and alias_proof["event"]["label"] == "BOX-LEGACY"
    for key in ("BOX-LEGACY", "box-legacy", "REG-STOCK"):
        resolved = lookup(client, key)
        assert resolved["bin"]["location"]["id"] == source and not resolved["bin"]["archived"]
        assert resolved["matchedLabel"] == key.upper() and resolved["isCanonical"] == (key == "REG-STOCK")
    client.call(LABEL, "POST", dict(commandId=str(uuid4()), locationId=target, expectedRevision=second["revision"],
                                    label="box-legacy"), expected=409)
    _, target_alias = post(client, LABEL, second["revision"], locationId=target, label="BOX-NEW")
    assert lookup(client, "box-new")["bin"]["location"]["revision"] == target_alias["revision"]

    _, order = post(client, "receiving/purchases", 0, reference="Labelled inventory", source="Fictional warehouse",
                    currency="USD", orderedQuantity=4, pendingChargeMinor=None)
    _, received = post(client, "receiving/deliveries", order["revision"], purchaseId=order["entityId"],
                       reference="Original batch", catalogId=catalog, itemCostMinor=100, quantity=4)
    purchase = order["entityId"]
    placement = dict(purchaseId=purchase, deliveryId=received["entityId"], catalogId=catalog, quantity=2,
                     locationId=source, confirmedLocationCode="BOX-LEGACY")
    placed_body, placed = post(client, PUTAWAY, received["revision"], **placement)
    observed = detail(client, source)["bin"]
    assert (observed["physicalQuantity"], observed["positionCount"]) == (2, 1)
    archive = dict(commandId=str(uuid4()), locationId=source, expectedRevision=observed["location"]["revision"],
                   confirmedLabel="BOX-LEGACY", reason="Retire the shelf after moving stock out",
                   expectedPhysicalQuantity=2, expectedPositionCount=1)
    _, extra = post(client, PUTAWAY, placed["revision"], **dict(placement, quantity=1))
    refused = client.call(ARCHIVE, "POST", archive, expected=409)
    assert refused["code"] == "location_archive_stock_changed" and not detail(client, source)["bin"]["archived"]
    observed = detail(client, source)["bin"]
    assert (observed["physicalQuantity"], observed["positionCount"]) == (3, 2)
    archive = dict(archive, commandId=str(uuid4()), expectedPhysicalQuantity=3, expectedPositionCount=2)
    archived = client.call(ARCHIVE, "POST", archive, expected=(200, 201))
    archive_proof = event(client, source, archive, archived)
    history = archive_proof["event"]
    assert (history["kind"], history["reason"], history["physicalQuantity"], history["positionCount"]) == (
        "archived", archive["reason"], 3, 2)
    assert lookup(client, "BOX-LEGACY")["bin"]["archived"]
    assert client.call(PUTAWAY, "POST", placed_body, expected=(200, 201)) == placed
    assert client.call(ARCHIVE, "POST", archive, expected=(200, 201)) == archived
    assert client.call(PUTAWAY, "POST", dict(placed_body, commandId=str(uuid4()), expectedRevision=extra["revision"],
                                              confirmedLocationCode="REG-STOCK", quantity=1), expected=409)["code"] == "location_destination_archived"

    position = placed["entityId"]
    where = dict(purchaseId=purchase, positionId=position, locationId=source, confirmedLocationCode="BOX-LEGACY")
    _, counted = post(client, "receiving/counts", extra["revision"], **where, countedQuantity=2,
                      reason="Count the pieces remaining on the retired shelf")
    move = dict(purchaseId=purchase, sourcePlacementId=position, sourceLocationId=source, confirmedSourceLocationCode="BOX-LEGACY",
                destinationLocationId=target, confirmedDestinationLocationCode="BOX-NEW", quantity=1,
                reason="Move stock off the retired shelf", backing=[])
    move_body, moved = post(client, TRANSFER, counted["revision"], **move)
    back = dict(move_body, commandId=str(uuid4()), expectedRevision=moved["revision"], sourcePlacementId=moved["entityId"],
                sourceLocationId=target, confirmedSourceLocationCode="BOX-NEW", destinationLocationId=source,
                confirmedDestinationLocationCode="BOX-LEGACY")
    assert client.call(TRANSFER, "POST", back, expected=409)["code"] == "location_destination_archived"
    assert (detail(client, source)["bin"]["physicalQuantity"], detail(client, target)["bin"]["physicalQuantity"]) == (2, 1)

    current = next(item for item in client.call("workspace")["catalog"] if item["id"] == catalog)
    item = dict(entityId=catalog, revision=current["revision"])
    selected = dict(purchaseId=purchase, positionId=moved["entityId"], locationId=target, confirmedLocationCode="BOX-NEW")
    line = output(item, where, 1, 25, "retained")
    invalid = dict(source_fields(selected, item, 1, [line], 0, []), commandId=str(uuid4()), expectedRevision=moved["revision"])
    assert client.call(PART_OUT, "POST", invalid, expected=409)["code"] == "location_destination_archived"
    client.call(f"locations/{target}/registry/receipts?commandId=" + archive["commandId"], expected=404)
    saved = {"purchase": purchase_row(client, purchase), "source": source, "target": target,
             "registry": detail(client, source), "other": detail(client, target),
             "events": [(LABEL, label_body, label_receipt, alias_proof), (ARCHIVE, archive, archived, archive_proof)],
             "stock": [(PUTAWAY, placed_body, placed), (TRANSFER, move_body, moved)]}
    assert saved["purchase"]["revision"] == moved["revision"]
    return saved


def verify_restored_registry(client, saved):
    assert purchase_row(client, saved["purchase"]["id"]) == saved["purchase"]
    assert detail(client, saved["source"]) == saved["registry"]
    assert detail(client, saved["target"]) == saved["other"]
    assert lookup(client, "box-legacy")["bin"] == saved["registry"]["bin"]
    for path, command, original, evidence in saved["events"]:
        assert client.call(path, "POST", command, expected=(200, 201)) == original
        assert event(client, saved["source"], command, original) == evidence
    for path, command, receipt in saved["stock"]:
        assert client.call(path, "POST", command, expected=(200, 201)) == receipt
    print("Restored alternate labels, stock in archived bins, outgoing transfer, archive witness and original receipts verified.", flush=True)
