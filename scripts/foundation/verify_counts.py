"""Verify witnessed counts and preserve an unresolved shortage through recovery."""

import uuid

from verify_receiving import post, purchase_row


def seed_previous_count(instance, location_id, purchase_id, position_id, revision=5):
    """Stage from a real v5 open case against a position with prior transferred backing."""
    command, observation = (str(uuid.uuid4()) for _ in range(2))
    instance.sql(
        f"""
        BEGIN;
        INSERT INTO brickvault.stock_count_observations(id,command_id,purchase_id,position_id,location_id,
            location_code,expected_purchase_revision,counted_quantity,reason)
            VALUES ('{observation}','{command}','{purchase_id}','{position_id}','{location_id}',
                    'OLD-01',{revision},1,'Witnessed migration fixture');
        INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
            VALUES ('{command}','count',decode(repeat('00',32),'hex'),'{observation}',{revision+1},gen_random_uuid());
        INSERT INTO brickvault.audit(command_id,actor) VALUES ('{command}','operator');
        COMMIT;
        """, role="bv_migrator",
    )


def records(client, location_id):
    snapshot = client.call(f"receiving/bins/{location_id}/counts")
    assert snapshot.pop("asOf") and snapshot["complete"] and snapshot["scope"] == "bin-counts"
    return snapshot


def qualify_counts(client, catalog_id):
    _, location = post(client, "locations", 0, code="CNT-01", name="Count qualification shelf")
    _, purchase = post(client, "receiving/purchases", 0, reference="COUNT-22", source="Training purchase",
                       currency="USD", orderedQuantity=6, pendingChargeMinor=None)
    purchase_id = purchase["entityId"]
    _, delivery = post(client, "receiving/deliveries", purchase["revision"], purchaseId=purchase_id,
                       reference="Counted carton", quantity=6, catalogId=catalog_id, itemCostMinor=840)
    _, position = post(client, "receiving/putaway", delivery["revision"], purchaseId=purchase_id,
                       deliveryId=delivery["entityId"], catalogId=catalog_id, locationId=location["entityId"],
                       confirmedLocationCode="CNT-01", quantity=6)
    choice = [{"placementId": position["entityId"], "quantity": 3}]
    _, offer = post(client, "receiving/offers", position["revision"], purchaseId=purchase_id,
                    catalogId=catalog_id, unitPriceMinor=200, backing=choice)
    path = "receiving/counts"
    fields = dict(purchaseId=purchase_id, positionId=position["entityId"], locationId=location["entityId"],
                  confirmedLocationCode="CNT-01", countedQuantity=2, reason="Witnessed shelf count")
    first, receipt = post(client, path, offer["revision"], **fields)
    assert client.call(path, "POST", first) == receipt
    view = records(client, location["entityId"])
    observation, case, counted = (view[key][0] for key in ("observations", "shortages", "positions"))
    assert (observation["ledgerQuantity"], observation["countedQuantity"], observation["missingQuantity"]) == (6, 2, 4)
    assert observation["offerBackedQuantity"] == 3 and observation["postedAdjustment"] is False
    assert case["id"] == observation["id"] and case["state"] == "open" and counted["shortageOpen"]
    assert client.call("receiving/offers", "POST", dict(commandId=str(uuid.uuid4()), expectedRevision=receipt["revision"],
                       purchaseId=purchase_id, catalogId=catalog_id, unitPriceMinor=200,
                       backing=[{"placementId": position["entityId"], "quantity": 1}]), expected=409)["code"] == "stock_shortage_open"
    second, next_receipt = post(client, path, receipt["revision"],
                                **{**fields, "countedQuantity": 7, "reason": "Independent recount found an extra unit"})
    final = records(client, location["entityId"])
    assert len(final["observations"]) == 2 and len(final["shortages"]) == 1
    assert final["shortages"][0]["id"] == case["id"] and final["shortages"][0]["state"] == "open"
    assert any(o["id"] == next_receipt["entityId"] and o["unidentifiedSurplusQuantity"] == 1
               and o["postedAdjustment"] is False for o in final["observations"])
    row = purchase_row(client, purchase_id)
    assert row["deliveries"][0]["itemCostMinor"] == 840 and row["balances"]["physicalOnHand"] == 6
    assert row["offers"][0]["backing"][0]["quantity"] == 3
    stock = client.call("inventory/positions?positionId=" + position["entityId"])["rows"][0]["layer"]
    assert (stock["quantity"], stock["backedQuantity"], stock["freeToBackQuantity"]) == (6, 3, 0)
    return location["entityId"], final, [(first, receipt), (second, next_receipt)]


def verify_restored_counts(client, evidence):
    location, expected, commands = evidence
    assert records(client, location) == expected
    for command, receipt in commands:
        assert client.call("receiving/counts", "POST", command) == receipt
    assert records(client, location) == expected
    print("Restored witnessed counts, open shortage, existing allocations and both original count receipts.", flush=True)
