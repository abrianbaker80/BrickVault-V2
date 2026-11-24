"""Qualify a witnessed loss with explicit draft-offer choices and verify its restored receipt."""

from uuid import uuid4

from verify_holds import hold_snapshot
from verify_receiving import post, purchase_row


def seed_prior_build_hold(instance, location, purchase, position):
    """The stopped v6 source already has a real reservation before its witnessed count."""
    hold, command = uuid4(), uuid4()
    instance.sql(
        f"""
        BEGIN;
        INSERT INTO brickvault.stock_build_holds(id,command_id,purchase_id,position_id,location_id,
            location_code,expected_purchase_revision,quantity,purpose,reason)
            VALUES ('{hold}','{command}','{purchase}','{position}','{location}',
                    'OLD-01',5,1,'Training build','Preserve the selected project');
        INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
            VALUES ('{command}','hold',decode(repeat('00',32),'hex'),'{hold}',6,gen_random_uuid());
        INSERT INTO brickvault.audit(command_id,actor) VALUES ('{command}','operator');
        COMMIT;
        """, role="bv_migrator",
    )


def posted_loss(client, path):
    result = client.call(path)
    assert result.pop("asOf") and result["complete"]
    return result


def qualify_loss(client, catalog_id):
    _, bin_receipt = post(client, "locations", 0, code="LOSS-01", name="Short count fixture")
    location = bin_receipt["entityId"]
    _, buy = post(client, "receiving/purchases", 0, reference="LOSS-22", source="Training supplier",
                  currency="USD", orderedQuantity=10, pendingChargeMinor=97)
    purchase = buy["entityId"]
    _, delivery = post(client, "receiving/deliveries", buy["revision"], purchaseId=purchase, reference="Blue plates",
                       quantity=10, catalogId=catalog_id, itemCostMinor=1001)
    put = dict(purchaseId=purchase, deliveryId=delivery["entityId"], catalogId=catalog_id, locationId=location,
               confirmedLocationCode="LOSS-01")
    _, placed = post(client, "receiving/putaway", delivery["revision"], **put, quantity=10)
    position = placed["entityId"]
    ctx = dict(purchaseId=purchase, positionId=position, locationId=location, confirmedLocationCode="LOSS-01")
    backing = [{"placementId": position, "quantity": 3}]
    offer = dict(purchaseId=purchase, catalogId=catalog_id, backing=backing)
    _, a = post(client, "receiving/offers", placed["revision"], **offer, unitPriceMinor=117)
    _, b = post(client, "receiving/offers", a["revision"], **offer, unitPriceMinor=139)
    _, h1 = post(client, "receiving/holds", b["revision"], **ctx, quantity=3, purpose="Bridge", reason="Project reservation")
    _, h2 = post(client, "receiving/holds", h1["revision"], **ctx, quantity=1, purpose="Tower", reason="Project reservation")
    count_body, count = post(client, "receiving/counts", h2["revision"], **ctx, countedQuantity=5, reason="Five blue plates witnessed")
    base = f"receiving/bins/{location}/positions/{position}"
    case = client.call(f"receiving/bins/{location}/counts?observationId={count['entityId']}")["shortages"][0]["id"]
    _, released = post(client, "receiving/holds/release", count["revision"], purchaseId=purchase, holdId=h1["entityId"],
                       quantity=2, reason="Owner released two bridge units", shortageId=case, expectedObservationId=count["entityId"])
    choice = [{"offerId": a["entityId"], "quantity": 1}, {"offerId": b["entityId"], "quantity": 2}]
    intended = dict(**ctx, caseId=case, expectedObservationId=count["entityId"], reason="Confirmed source tray loss")
    review = client.call(f"{base}/shortages/{case}/adjustment-review")
    assert (review["ledgerQuantity"], review["protectedProjectQuantity"], review["minimumOfferBackingReduction"]) == (10, 2, 3)
    revision = released["revision"]
    endpoint = "receiving/stock-losses"
    before = purchase_row(client, purchase)
    rejected = dict(**intended, commandId=str(uuid4()), expectedRevision=revision, offerReductions=choice[:1])
    client.call(endpoint, "POST", rejected, expected=409)
    assert purchase_row(client, purchase) == before
    body, receipt = post(client, endpoint, revision, **intended, offerReductions=choice)
    assert receipt["kind"] == "stock-loss" and receipt["revision"] == revision + 1
    assert client.call(endpoint, "POST", body) == receipt
    client.call(endpoint, "POST", {**body, "commandId": str(uuid4())}, expected=409)
    client.call("receiving/putaway", "POST", {**put, "commandId": str(uuid4()), "expectedRevision": receipt["revision"],
                                             "quantity": 1}, expected=409)
    current = purchase_row(client, purchase)
    bal = current["balances"]
    assert (bal["physicalOnHand"], bal["unavailable"], bal["reserved"], bal["free"], bal["publishedQuantity"]) == (5, 0, 2, 3, 0)
    layer = current["deliveries"][0]
    assert (layer["quantity"], layer["intakeQuantity"], layer["lostQuantity"], layer["itemCostMinor"],
            layer["allocatedLostItemCostMinor"], layer["remainingItemCostMinor"]) == (10, 0, 5, 1001, 500, 501)
    assert current["pendingChargeMinor"] == 97
    assert {o["id"]: (o["backedQuantity"], o["unitPriceMinor"]) for o in current["offers"]} == {
        a["entityId"]: (2, 117), b["entityId"]: (1, 139)}
    projects = hold_snapshot(client, location, position)
    assert {h["id"]: h["remainingQuantity"] for h in projects["holds"]} == {h1["entityId"]: 1, h2["entityId"]: 1}
    assert projects["shortageId"] is None
    detail_path = base + "/posted-loss?commandId=" + body["commandId"]
    detail = posted_loss(client, detail_path)
    assert (detail["caseId"], detail["postingId"], detail["reviewedObservation"]["id"], detail["allocatedItemCostMinor"],
            detail["lostQuantity"], detail["offerBackedQuantityAfter"]) == (case, receipt["entityId"], count["entityId"], 500, 5, 3)
    assert {row["offerId"]: (row["quantity"], row["remainingBackedQuantity"]) for row in detail["reductions"]} == {
        a["entityId"]: (1, 2), b["entityId"]: (2, 1)}
    for selector, identity in (("caseId", case), ("observationId", count["entityId"]), ("postingId", receipt["entityId"])):
        assert posted_loss(client, f"{base}/posted-loss?{selector}={identity}") == detail
    return purchase, current, detail_path, detail, [(endpoint, body, receipt), ("receiving/counts", count_body, count)]


def verify_restored_loss(client, evidence):
    purchase, current, path, detail, commands = evidence
    for endpoint, body, original in commands:
        assert client.call(endpoint, "POST", body) == original
    assert purchase_row(client, purchase) == current and posted_loss(client, path) == detail
    print("Restored the witnessed count, exact posted stock loss, chosen offer reductions and conserved receipt cost.", flush=True)
