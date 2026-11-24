"""Qualify stock transfer migration, custody, receipt replay and recovery."""

import uuid


def seed_opening_position(instance, location_id):
    """Create a backed position with valid v3 facts before the stopped-copy upgrade."""
    item, purchase, delivery, placement, offer = [str(uuid.uuid4()) for _ in range(5)]
    facts = [
        (kind, str(uuid.uuid4()), entity, rev)
        for rev, (kind, entity) in enumerate(
            (
                ("purchase", purchase),
                ("delivery", delivery),
                ("putaway", placement),
                ("offer", offer),
            ),
            1,
        )
    ]
    journal = ",".join(
        f"('{cmd}','{kind}',decode(repeat('00',32),'hex'),'{entity}',{rev},gen_random_uuid())"
        for kind, cmd, entity, rev in facts
    )
    instance.sql(
        f"""
        BEGIN;
        INSERT INTO brickvault.catalog(id,revision,item_type,item_number,name,color_state,color_id,condition,
            completeness,source,license,mapping) VALUES ('{item}',1,'part','FIX-17','Training part','known',0,
            'new','complete','Training fixture','operator-owned','confirmed');
        INSERT INTO brickvault.purchases(id,command_id,revision,reference,source,currency,ordered_quantity)
            VALUES ('{purchase}','{facts[0][1]}',4,'MIG-1','Training purchase','USD',4);
        INSERT INTO brickvault.deliveries(id,command_id,purchase_id,reference,quantity,catalog_id,item_cost_minor)
            VALUES ('{delivery}','{facts[1][1]}','{purchase}','Box',4,'{item}',NULL);
        INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,location_id,location_code,quantity)
            VALUES ('{placement}','{facts[2][1]}','{purchase}','{delivery}','{item}','{location_id}','OLD-01',3);
        INSERT INTO brickvault.offers(id,command_id,purchase_id,catalog_id,catalog_revision,unit_price_minor)
            VALUES ('{offer}','{facts[3][1]}','{purchase}','{item}',1,150);
        INSERT INTO brickvault.offer_backing(purchase_id,offer_id,placement_id,catalog_id,catalog_revision,quantity)
            VALUES ('{purchase}','{offer}','{placement}','{item}',1,2);
        INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id) VALUES {journal};
        COMMIT;
    """,
        role="bv_migrator",
    )

    return purchase, placement, offer


def seed_previous_transfer(instance, source_bin, purchase, position, offer):
    """Qualify v4 upgrade with stock and offer backing already moved off their original position."""
    destination, transfer, command = [str(uuid.uuid4()) for _ in range(3)]
    instance.sql(
        f"""
        BEGIN;
        INSERT INTO brickvault.locations(id,revision,code,name) VALUES ('{destination}',1,'NEW-02','Receiving shelf');
        UPDATE brickvault.purchases SET revision=revision+1 WHERE id='{purchase}';
        INSERT INTO brickvault.placement_transfers(id,command_id,purchase_id,source_position_id,source_location_id,
            source_location_code,destination_location_id,destination_location_code,quantity,reason)
            VALUES ('{transfer}','{command}','{purchase}','{position}','{source_bin}','OLD-01',
                    '{destination}','NEW-02',1,'Confirmed fixture count');
        INSERT INTO brickvault.placement_transfer_backing(transfer_id,offer_id,quantity)
            VALUES ('{transfer}','{offer}',1);
        INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
            VALUES ('{command}','transfer',decode(repeat('00',32),'hex'),'{transfer}',5,gen_random_uuid());
        COMMIT;
        """, role="bv_migrator",
    )


def bin_records(client, ids, purchase_id):
    records = {}
    for code, location_id in ids.items():
        path = "receiving/bins/" + location_id
        current = client.call(path)
        history = client.call(path + "/movements")
        assert current["complete"] and history["complete"]
        assert current["location"] == history["location"]
        layers = [r for r in current["layers"] if r["purchaseId"] == purchase_id]
        movements = [m for m in history["movements"] if m["purchaseId"] == purchase_id]
        records[code] = {"layers": layers, "movements": movements}
    return records


def qualify_transfer(client, receiving):
    from verify_receiving import purchase_row

    purchase, offer_request, offer = receiving
    purchase_id = purchase["id"]
    original = next(d for d in purchase["deliveries"] if d["reference"] == "A")[
        "placements"
    ][0]
    ids = {
        r["code"]: r["id"]
        for r in client.call("workspace")["locations"]
        if r["code"] in ("B-12", "C-03")
    }
    before = bin_records(client, ids, purchase_id)
    request = dict(
        commandId=str(uuid.uuid4()),
        expectedRevision=purchase["revision"],
        purchaseId=purchase_id,
        sourcePlacementId=original["id"],
        sourceLocationId=ids["B-12"],
        confirmedSourceLocationCode="B-12",
        destinationLocationId=ids["C-03"],
        confirmedDestinationLocationCode="C-03",
        quantity=4,
        reason="Confirmed bin reorganization",
        backing=[{"offerId": offer["entityId"], "quantity": 2}],
    )
    path = "receiving/transfers"
    for invalid in ({"confirmedDestinationLocationCode": "B-12"}, {"backing": []}):
        client.call(
            path,
            "POST",
            {**request, **invalid, "commandId": str(uuid.uuid4())},
            expected=409,
        )
    assert bin_records(client, ids, purchase_id) == before
    receipt = client.call(path, "POST", request, expected=(200, 201))
    assert client.call(path, "POST", request, expected=(200, 201)) == receipt
    current = bin_records(client, ids, purchase_id)
    first = next(
        m
        for m in current["B-12"]["movements"]
        if m["commandId"] == request["commandId"]
    )
    assert first in current["C-03"]["movements"] and first["quantity"] == 4
    assert (
        first["sourcePlacementId"] == original["id"]
        and first["destinationPlacementId"] == receipt["entityId"]
    )
    assert (
        first["backing"][0]["quantity"] == 2
        and first["backing"][0]["offerId"] == offer["entityId"]
    )
    assert sum(r["quantity"] for r in current["B-12"]["layers"]) == 4
    assert sum(r["backedQuantity"] for r in current["C-03"]["layers"]) == 4
    onward = {
        **request,
        "commandId": str(uuid.uuid4()),
        "expectedRevision": receipt["revision"],
        "sourceLocationId": ids["C-03"],
        "confirmedSourceLocationCode": "C-03",
        "destinationLocationId": ids["B-12"],
        "confirmedDestinationLocationCode": "B-12",
        "sourcePlacementId": receipt["entityId"],
        "quantity": 1,
        "backing": [{"offerId": offer["entityId"], "quantity": 1}],
        "reason": "Confirmed onward transfer",
    }
    second = client.call(path, "POST", onward, expected=(200, 201))
    final = purchase_row(client, purchase_id)
    assert final["balances"] == purchase["balances"]
    assert (
        final["offers"][0]["unitPriceMinor"] == purchase["offers"][0]["unitPriceMinor"]
    )
    records = bin_records(client, ids, purchase_id)
    for code, (quantity, backed) in {"B-12": (5, 4), "C-03": (6, 3)}.items():
        layers = records[code]["layers"]
        assert (
            sum(r["quantity"] for r in layers),
            sum(r["backedQuantity"] for r in layers),
        ) == (quantity, backed)
        moved = next(
            r
            for r in layers
            if r["placementId"] == (second if code == "B-12" else receipt)["entityId"]
        )
        assert moved["receiptItemCostMinor"] == 800 and moved["receiptQuantity"] == 8
    assert (
        next(r for r in records["C-03"]["layers"] if r["deliveryReference"] == "B")[
            "receiptItemCostMinor"
        ]
        is None
    )
    print(
        "Transfer and onward movement reconciled stock, unchanged receipt costs, explicit offer backing and both bin histories.",
        flush=True,
    )
    return (final, offer_request, offer), (
        ids,
        purchase_id,
        records,
        request,
        receipt,
        onward,
        second,
    )


def verify_restored_transfers(client, evidence):
    ids, purchase_id, records, request, receipt, onward, second = evidence
    assert bin_records(client, ids, purchase_id) == records
    for body, expected in ((request, receipt), (onward, second)):
        assert (
            client.call("receiving/transfers", "POST", body, expected=(200, 201))
            == expected
        )
    assert bin_records(client, ids, purchase_id) == records
