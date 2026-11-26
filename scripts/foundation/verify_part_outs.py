"""Reconcile manual transformations through the authenticated API and an encrypted restore."""

from uuid import uuid4

from verify_receiving import post, purchase_row
from verify_stock_recoveries import receive

PART_OUT = "receiving/part-outs"
HISTORY = "withdrawals=true&quarantine=true&closures=true&recoveries=true"


def output(catalog, where, quantity, cost, disposition):
    return dict(lineId=str(uuid4()), catalogId=catalog["entityId"], catalogRevision=catalog["revision"],
                locationId=where["locationId"], confirmedLocationCode=where["confirmedLocationCode"],
                quantity=quantity, itemCostMinor=cost, disposition=disposition)


def source_fields(where, catalog, quantity, outputs, residual, materials):
    return dict(purchaseId=where["purchaseId"], sourcePositionId=where["positionId"],
                sourceLocationId=where["locationId"], confirmedSourceLocationCode=where["confirmedLocationCode"],
                sourceCatalogId=catalog["entityId"], sourceCatalogRevision=catalog["revision"], quantity=quantity,
                reason="Counted the identified outputs in their confirmed bins", outputs=outputs,
                residualItemCostMinor=residual, physicalResiduals=materials)


def exact(client, body, receipt):
    path = f"inventory/positions/{body['sourcePositionId']}/part-out-receipt?"
    page = client.call(path + "commandId=" + body["commandId"])
    assert page.pop("asOf") and page["complete"] and page["id"] == receipt["entityId"]
    assert (page["commandId"], page["revision"], page["correlationId"]) == (
        body["commandId"], receipt["revision"], receipt["correlationId"])
    assert page["recordedSourceCatalog"]["id"] == body["sourceCatalogId"]
    assert page["purchaseId"] == body["purchaseId"] and page["confirmedSourceLocationCode"] == body["confirmedSourceLocationCode"]
    assert page["quantity"] == body["quantity"] and page["reason"] == body["reason"]
    assert page["nextOutputAfter"] is None and page["nextResidualAfter"] is None
    assert page["residualItemCostMinor"] == body["residualItemCostMinor"]
    assert sorted(page["physicalResiduals"], key=lambda r: r["lineId"]) == sorted(body["physicalResiduals"], key=lambda r: r["lineId"])
    choices = {line["lineId"]: line for line in body["outputs"]}
    assert len(page["outputs"]) == len(choices)
    for line in page["outputs"]:
        choice = choices[line["lineId"]]
        assert line["recordedCatalog"]["id"] == choice["catalogId"] and line["location"]["id"] == choice["locationId"]
        for key in ("quantity", "disposition", "itemCostMinor", "confirmedLocationCode"):
            assert line[key] == choice[key]
        assert line["quarantineId"] == (line["positionId"] if line["disposition"] == "retained" else None)
    by_id = client.call(path + "partOutId=" + receipt["entityId"])
    assert by_id.pop("asOf") and by_id == page
    return page


def check_lot(client, layer, on_hand, cost, consumed, consumed_cost, parent=None):
    lot = client.call(f"inventory/receipt-lots/{layer}?{HISTORY}&partOuts=true")
    assert lot["currentOnHandQuantity"] == on_hand == lot["unplacedQuantity"] + lot["positionedQuantity"]
    assert lot["remainingItemCostMinor"] == cost
    part = lot["partOut"]
    assert (part["consumedQuantity"], part["consumedItemCostMinor"], part["parentTransformationId"]) == (consumed, consumed_cost, parent)
    return lot


def qualify_part_outs(client):
    catalog = []
    for kind, name in (("set", "Source set"), ("part", "Axle"), ("minifigure", "Driver")):
        _, row = post(client, "catalog", 0, itemType=kind, itemNumber="TRANSFORM-" + kind, variant=None,
                      name=name, colorId=0, colorState="known", condition="new", completeness="complete",
                      source="Fictional part-out", license="operator-owned", mapping="confirmed")
        catalog.append(row)
    source, axle, driver = catalog
    _, bin_row = post(client, "locations", 0, code="OUTPUT-TRAY", name="Inspected output tray")
    tray = dict(locationId=bin_row["entityId"], confirmedLocationCode="OUTPUT-TRAY")
    evidence = []
    for label, cost in (("PAID", 100), ("ZERO", 0), ("UNKNOWN", None)):
        where, delivery, rev = receive(client, source["entityId"], "PARTOUT-" + label, 3, cost)
        review = f"inventory/positions/{where['positionId']}/part-out-review?quantity=1"
        summary = client.call(review)
        selected = 33 if cost else cost
        assert summary["selectionAvailable"] and summary["sourceItemCostMinor"] == selected
        axle_cost = 18 if cost else cost
        driver_cost = 11 if cost else cost
        residual = 4 if cost else cost
        lines = [output(axle, tray, 2, axle_cost, "stock"), output(driver, where, 1, driver_cost, "retained")]
        materials = [dict(lineId=str(uuid4()), disposition="discarded", quantity=1, description="Torn instruction sheet")]
        fields = source_fields(where, source, 1, lines, residual, materials)
        invalid = dict(fields, commandId=str(uuid4()), expectedRevision=rev)
        client.call(PART_OUT, "POST", dict(invalid, residualItemCostMinor=1), expected=409)
        client.call(PART_OUT, "POST", dict(invalid, confirmedSourceLocationCode="OTHER"), expected=409)
        client.call(PART_OUT, "POST", dict(invalid, sourceCatalogRevision=source["revision"] + 1), expected=409)
        copies = [output(source, where, 1, axle_cost, "stock"), output(source, tray, 1, driver_cost, "retained")]
        for duplicate in (copies, [*copies, output(axle, tray, 1, None, "stock")]):
            failure = client.call(PART_OUT, "POST", dict(invalid, outputs=duplicate), expected=409)
            assert failure["code"] == "part_out_identity_gain"
        body, receipt = post(client, PART_OUT, rev, **fields)
        page = exact(client, body, receipt)
        assert page["sourceItemCostMinor"] == selected and page["receipt"]["deliveryId"] == delivery
        assert client.call(PART_OUT, "POST", dict(body, outputs=lines[::-1])) == receipt
        client.call(PART_OUT, "POST", dict(body, reason="Changed payload"), expected=409)
        client.call(PART_OUT, "POST", dict(body, commandId=str(uuid4())), expected=409)
        purchase = purchase_row(client, where["purchaseId"])
        assert len(purchase["deliveries"]) == 1 and len(purchase["derivedLayers"]) == 2
        assert purchase["orderedQuantity"] == purchase["deliveries"][0]["quantity"] == 3
        assert purchase["balances"]["knownItemCostMinor"] == cost
        assert purchase["balances"]["physicalOnHand"] == 5 and purchase["balances"]["unavailable"] == 1
        check_lot(client, delivery, 2, None if cost is None else cost - selected, 1, selected)
        for line in page["outputs"]:
            lot = check_lot(client, line["deliveryId"], line["quantity"], line["itemCostMinor"], 0,
                            None if line["itemCostMinor"] is None else 0, receipt["entityId"])
            assert lot["partOut"]["sourceDeliveryId"] == delivery and lot["partOut"]["sourcePositionId"] == where["positionId"]
            events = client.call(f"inventory/positions/{line['positionId']}/timeline?{HISTORY}&partOuts=true")["events"]
            assert len(events) == 1 and events[0]["kind"] == "part-out-output"
            assert events[0]["transformationId"] == receipt["entityId"] and events[0]["physicalDelta"] == line["quantity"]
            assert events[0]["relatedPositionId"] == where["positionId"]
            assert lot["quarantinedQuantity"] == (line["quantity"] if line["disposition"] == "retained" else 0)
        source_events = client.call(f"inventory/positions/{where['positionId']}/timeline?{HISTORY}&partOuts=true")["events"]
        assert [(e["transformationId"], e["physicalDelta"]) for e in source_events if e["kind"] == "part-out"] == [(receipt["entityId"], -1)]
        client.call(f"inventory/receipt-lots/{delivery}?{HISTORY}", expected=426)
        evidence.append((body, receipt, page, purchase))
    client.call("receiving/current?holds=true&losses=true&quarantine=true&recoveries=true", expected=426)
    return evidence


def verify_restored_part_outs(client, evidence):
    for body, receipt, page, purchase in evidence:
        assert client.call(PART_OUT, "POST", body) == receipt
        assert exact(client, body, receipt) == page
        assert purchase_row(client, body["purchaseId"]) == purchase
        for line in page["outputs"]:
            lot = check_lot(client, line["deliveryId"], line["quantity"], line["itemCostMinor"], 0,
                            None if line["itemCostMinor"] is None else 0, receipt["entityId"])
            assert lot["partOut"]["sourceDeliveryId"] == page["receipt"]["deliveryId"]
    print("Manual part-outs, supplier and child layers, retained quarantine and explicit costs survived encrypted restore and replay.", flush=True)
