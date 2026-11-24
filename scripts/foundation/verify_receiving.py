"""Exercise the M3 receiving case against the authenticated API and recovered database."""

import uuid

from runtime import start, stop


def post(client, path, revision, **fields):
    body = {"commandId": str(uuid.uuid4()), "expectedRevision": revision, **fields}
    receipt = client.call(path, "POST", body, expected=(200, 201))
    assert receipt["commandId"] == body["commandId"]
    return body, receipt


def purchase_row(client, purchase_id):
    view = client.call("receiving/current?holds=true")
    assert view["complete"] and view["scope"] == "receiving"
    return next(row for row in view["purchases"] if row["id"] == purchase_id)


def check_stock(row, on_hand, unavailable, free):
    balances = row["balances"]
    assert (
        balances["physicalOnHand"],
        balances["unavailable"],
        balances["reserved"],
        balances["free"],
    ) == (on_hand, unavailable, 0, free)
    assert on_hand == unavailable + free


def qualify_receiving(instance, client, catalog_id):
    bins = {}
    for code in ("B-12", "C-03"):
        _, receipt = post(
            client, "locations", 0, code=code, name="Receiving bin " + code
        )
        bins[code] = receipt["entityId"]
    _, order = post(
        client,
        "receiving/purchases",
        0,
        reference="PO-208",
        source="Fictional training purchase",
        currency="USD",
        orderedQuantity=12,
        pendingChargeMinor=60,
    )
    purchase_id = order["entityId"]
    a, receipt_a = post(
        client,
        "receiving/deliveries",
        order["revision"],
        purchaseId=purchase_id,
        reference="A",
        quantity=8,
        catalogId=catalog_id,
        itemCostMinor=800,
    )
    original = purchase_row(client, purchase_id)
    check_stock(original, 8, 8, 0)
    stop(instance)
    start(instance)
    assert (
        client.call("receiving/deliveries", "POST", a, expected=(200, 201)) == receipt_a
    )
    assert purchase_row(client, purchase_id) == original
    put_a = dict(
        purchaseId=purchase_id,
        deliveryId=receipt_a["entityId"],
        catalogId=catalog_id,
        locationId=bins["B-12"],
        quantity=8,
    )
    client.call(
        "receiving/putaway",
        "POST",
        dict(
            commandId=str(uuid.uuid4()),
            expectedRevision=receipt_a["revision"],
            confirmedLocationCode="C-03",
            **put_a
        ),
        expected=(400, 409),
    )
    assert purchase_row(client, purchase_id) == original
    _, shelf_a = post(
        client,
        "receiving/putaway",
        receipt_a["revision"],
        confirmedLocationCode="B-12",
        **put_a
    )
    check_stock(purchase_row(client, purchase_id), 8, 0, 8)
    _, receipt_b = post(
        client,
        "receiving/deliveries",
        shelf_a["revision"],
        purchaseId=purchase_id,
        reference="B",
        quantity=4,
        catalogId=None,
        itemCostMinor=None,
    )
    mixed = purchase_row(client, purchase_id)
    check_stock(mixed, 12, 4, 8)
    put_b = dict(
        purchaseId=purchase_id,
        deliveryId=receipt_b["entityId"],
        catalogId=catalog_id,
        locationId=bins["C-03"],
        confirmedLocationCode="C-03",
        quantity=3,
    )
    denied = client.call(
        "receiving/putaway",
        "POST",
        {"commandId": str(uuid.uuid4()), "expectedRevision": receipt_b["revision"], **put_b},
        expected=409,
    )
    assert denied["code"] == "catalog_reconfirmation_required"
    assert purchase_row(client, purchase_id) == mixed
    selected = next(row for row in client.call("workspace")["catalog"] if row["id"] == catalog_id)
    _, shelf_b = post(
        client,
        "receiving/putaway",
        receipt_b["revision"],
        confirmedCatalogRevision=selected["revision"],
        **put_b,
    )
    placed = purchase_row(client, purchase_id)
    check_stock(placed, 12, 1, 11)
    body, offer = post(
        client,
        "receiving/offers",
        shelf_b["revision"],
        purchaseId=purchase_id,
        catalogId=catalog_id,
        unitPriceMinor=150,
        backing=[
            {"placementId": shelf_a["entityId"], "quantity": 5},
            {"placementId": shelf_b["entityId"], "quantity": 2},
        ],
    )
    final = purchase_row(client, purchase_id)
    check_stock(final, 12, 1, 11)
    bal = final["balances"]
    assert (
        bal["backedQuantity"],
        bal["withheldQuantity"],
        bal["publishedQuantity"],
    ) == (7, 4, 0)
    assert (bal["knownCostFreeQuantity"], bal["unknownCostFreeQuantity"]) == (8, 3)
    assert (
        bal["knownItemCostMinor"],
        final["pendingChargeMinor"],
        bal["knownPurchaseComponentsMinor"],
    ) == (800, 60, 860)
    deliveries = {d["reference"]: d for d in final["deliveries"]}
    assert (
        deliveries["B"]["itemCostMinor"] is None
        and deliveries["B"]["intakeQuantity"] == 1
    )
    assert [
        (p["locationCode"], p["quantity"]) for p in deliveries["A"]["placements"]
    ] == [("B-12", 8)]
    assert [
        (p["locationCode"], p["quantity"]) for p in deliveries["B"]["placements"]
    ] == [("C-03", 3)]
    assert len(final["offers"]) == 1 and final["offers"][0]["unitPriceMinor"] == 150
    client.call(
        "receiving/offers",
        "POST",
        {**body, "commandId": str(uuid.uuid4()), "expectedRevision": offer["revision"]},
        expected=409,
    )
    assert purchase_row(client, purchase_id) == final
    assert not any(
        entry["commandId"] == offer["commandId"]
        for entry in client.call("workspace")["audit"]
    )
    entry = next(
        entry
        for entry in client.call("workspace?audit=all")["audit"]
        if entry["commandId"] == offer["commandId"]
    )
    assert entry["kind"] == "offer" and entry["correlationId"] == offer["correlationId"]
    from verify import poll

    poll(
        lambda: client.call("diagnostics/" + offer["correlationId"]),
        lambda d: d["jobState"] == "done",
    )
    print(
        "Receiving quantities, known and unknown costs, bin confirmation, receipt replay and unpublished offer passed.",
        flush=True,
    )
    return final, body, offer


def verify_recovered_receiving(client, evidence):
    purchase, body, offer = evidence
    assert purchase_row(client, purchase["id"]) == purchase
    assert client.call("receiving/offers", "POST", body, expected=(200, 201)) == offer
    assert purchase_row(client, purchase["id"]) == purchase
