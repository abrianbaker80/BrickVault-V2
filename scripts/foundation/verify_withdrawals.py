"""Withdraw backing from one local draft offer and recover its original position receipt."""

from uuid import uuid4

from verify_receipt_lots import read_lot
from verify_receiving import post, purchase_row


WITHDRAW = "inventory/draft-backing-withdrawals"


def verify_purchase(client, purchase, cost, expected_backed):
    row = purchase_row(client, purchase)
    balances = row["balances"]
    assert (balances["physicalOnHand"], balances["unavailable"], balances["reserved"], balances["free"],
            balances["backedQuantity"], balances["withheldQuantity"], balances["publishedQuantity"]) == (
                8, 0, 2, 6, expected_backed, 6 - expected_backed, 0)
    assert row["currency"] == "USD" and row["deliveries"][0]["itemCostMinor"] == cost
    return row


def qualify_withdrawals(client, catalog):
    code = "DRAFT-01"
    _, bin_receipt = post(client, "locations", 0, code=code, name="Draft offer training shelf")
    location = bin_receipt["entityId"]
    evidence = []
    for label, cost in (("Paid", 849), ("Zero", 0), ("Unknown", None)):
        _, purchase = post(client, "receiving/purchases", 0, reference="Draft " + label,
                           source="Fictional purchase", currency="USD", orderedQuantity=8, pendingChargeMinor=None)
        buy = purchase["entityId"]
        _, delivery = post(client, "receiving/deliveries", purchase["revision"], purchaseId=buy, reference=label,
                           catalogId=catalog, quantity=8, itemCostMinor=cost)
        _, put = post(client, "receiving/putaway", delivery["revision"], purchaseId=buy, deliveryId=delivery["entityId"],
                      locationId=location, confirmedLocationCode=code, catalogId=catalog, quantity=8)
        pos = put["entityId"]
        where = dict(purchaseId=buy, positionId=pos, locationId=location, confirmedLocationCode=code)
        _, hold = post(client, "receiving/holds", put["revision"], **where, quantity=2,
                       purpose="Training build", reason="Set aside two pieces")
        _, chosen = post(client, "receiving/offers", hold["revision"], purchaseId=buy, catalogId=catalog,
                         unitPriceMinor=175, backing=[{"placementId": pos, "quantity": 2}])
        _, other = post(client, "receiving/offers", chosen["revision"], purchaseId=buy, catalogId=catalog,
                        unitPriceMinor=199, backing=[{"placementId": pos, "quantity": 1}])
        verify_purchase(client, buy, cost, 3)
        target = dict(**where, offerId=chosen["entityId"])
        invalid = dict(**target, commandId=str(uuid4()), expectedRevision=other["revision"],
                       quantity=3, reason="Above this offer's backing")
        client.call(WITHDRAW, "POST", invalid, expected=409)
        first, partial = post(client, WITHDRAW, other["revision"], **target, quantity=1, reason="Reserve shelf capacity")
        verify_purchase(client, buy, cost, 2)
        last, full = post(client, WITHDRAW, partial["revision"], **target, quantity=1, reason="Withdraw remaining allocation")
        evidence.append((buy, pos, delivery["entityId"], cost, chosen, other, first, partial, last, full))
    verify_recovered_withdrawals(client, evidence)
    return evidence


def verify_recovered_withdrawals(client, evidence):
    for purchase, position, delivery, cost, chosen, other, first, partial, last, full in evidence:
        assert client.call(WITHDRAW, "POST", first) == partial
        assert client.call(WITHDRAW, "POST", last) == full
        row = verify_purchase(client, purchase, cost, 1)
        assert row["revision"] == full["revision"]
        offers = {o["id"]: (o["backedQuantity"], o["unitPriceMinor"]) for o in row["offers"]}
        assert offers == {chosen["entityId"]: (0, 175), other["entityId"]: (1, 199)}
        header, positions, events = read_lot(client, delivery, withdrawals=True)
        assert (header["currentOnHandQuantity"], header["heldQuantity"], header["offerBackedQuantity"],
                header["remainingItemCostMinor"]) == (8, 2, 1, cost)
        layer, = positions
        assert (layer["placementId"], layer["quantity"], layer["freeToBackQuantity"]) == (position, 8, 5)
        timeline = client.call(f"inventory/positions/{position}/timeline?withdrawals=true")
        backing = f"inventory/positions/{position}/draft-backing"
        current = client.call(backing)
        assert current["complete"] and current["purchaseRevision"] == full["revision"]
        assert (current["physicalOnHand"], current["heldQuantity"], current["sellableQuantity"],
                current["offerBackedQuantity"], current["withheldQuantity"]) == (8, 2, 6, 1, 5)
        by_offer = {entry["offerId"]: entry["backedQuantity"] for entry in current["offers"]}
        assert by_offer == {chosen["entityId"]: 0, other["entityId"]: 1}
        history = client.call(backing + "/" + chosen["entityId"] + "/history")
        assert history["complete"] and history["offer"]["offerId"] == chosen["entityId"]
        path = backing + "/receipts"
        for before, submitted, saved in ((2, first, partial), (1, last, full)):
            by_id = client.call(path + "?receiptId=" + saved["entityId"])
            by_command = client.call(path + "?commandId=" + saved["commandId"])
            assert by_id.pop("asOf") and by_command.pop("asOf") and by_id == by_command
            assert by_id["complete"] and by_id["quantity"] == 1 and by_id["reason"] == submitted["reason"]
            assert (by_id["offerId"], by_id["positionId"], by_id["purchaseId"], by_id["currency"], by_id["unitPriceMinor"]) == (
                chosen["entityId"], position, purchase, "USD", 175)
            assert (by_id["priorBackedQuantity"], by_id["remainingBackedQuantity"]) == (before, before - 1)
            assert (by_id["priorTotalOfferBackedQuantity"], by_id["remainingTotalOfferBackedQuantity"]) == (before, before - 1)
            by_event = next(r["event"] for r in events if r["event"]["commandId"] == saved["commandId"])
            assert (by_event["kind"], by_event["physicalDelta"], by_event["reservedDelta"], by_event["offerBackedDelta"]) == (
                "offer-withdrawal", 0, 0, -1)
            assert any(e["commandId"] == saved["commandId"] for e in timeline["events"])
            diagnostic = client.call("diagnostics/" + saved["correlationId"])
            assert diagnostic["kind"] == "offer-withdrawal" and diagnostic["entityId"] == saved["entityId"]
    print("Both partial and full withdrawals retained the offer identity, price, stock, costs and original receipts.", flush=True)
