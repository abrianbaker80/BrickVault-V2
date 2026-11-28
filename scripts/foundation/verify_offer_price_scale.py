"""Qualify the negotiated selling-price scale across the authenticated stock workflows."""

from uuid import uuid4

from verify_receiving import post, purchase_row


def qualify_offer_price_scale(client, catalog):
    def stale(path, method="GET", body=None):
        response = client.call(path, method, body, expected=426, price_scale=False)
        assert response["code"] == "offer_price_client_upgrade_required"

    def checked(row, currency):
        assert row["currency"] == currency
        if currency == "IQD":
            assert row["priceScaleVerified"] is True and row["unitPriceMinor"] == 1234
        else:
            assert "priceScaleVerified" not in row

    def bin(code):
        return post(client, "locations", 0, code=code, name="Fictional pricing shelf")[1]["entityId"]

    source, destination, empty = (bin("SCALE-" + code) for code in ("A", "B", "C"))
    _, purchase = post(client, "receiving/purchases", 0, reference="Scale review", source="Fictional intake",
                       currency="HUF", orderedQuantity=8, pendingChargeMinor=None)
    buy = purchase["entityId"]
    _, delivery = post(client, "receiving/deliveries", 1, purchaseId=buy, reference="Training box",
                       catalogId=catalog, quantity=8, itemCostMinor=800)
    _, placement = post(client, "receiving/putaway", 2, purchaseId=buy, deliveryId=delivery["entityId"],
                        catalogId=catalog, locationId=source, confirmedLocationCode="SCALE-A", quantity=8)
    pos = placement["entityId"]
    base = {"purchaseId": buy, "catalogId": catalog, "unitPriceMinor": 10, "expectedRevision": 3}
    implicit = {**base, "commandId": str(uuid4()), "backing": [{"placementId": pos, "quantity": 1}]}
    offers = "receiving/offers"
    stale(offers, method="POST", body=implicit)
    assert client.call(offers, "POST", implicit, expected=409)["code"] == "offer_price_reentry_required"
    explicit = {**implicit, "currency": "IQD"}
    stale(offers, method="POST", body=explicit)
    assert client.call(offers, "POST", explicit, headers={"X-BrickVault-Price-Scale": "1, 1"}, expected=426)["code"] == "offer_price_client_upgrade_required"
    before = purchase_row(client, buy)
    assert before["revision"] == 3 and before["offers"] == []
    safe = client.call(offers, "POST", {**implicit, "currency": "EUR"}, price_scale=False)
    body, priced = post(client, offers, safe["revision"], purchaseId=buy, catalogId=catalog, currency="IQD",
                        unitPriceMinor=1234, backing=[{"placementId": pos, "quantity": 3}])
    assert client.call(offers, "POST", body, price_scale=False) == priced
    assert client.call(offers, "POST", {**body, "currency": "HUF"}, expected=409, price_scale=False)["code"] == "command_mismatch"
    current = "receiving/current?holds=true&losses=true&quarantine=true&recoveries=true&partOuts=true"
    stale(current)
    row = purchase_row(client, buy)
    assert row["currency"] == "HUF" and row["deliveries"][0]["itemCostMinor"] == 800
    for offer in row["offers"]:
        checked(offer, "IQD" if offer["id"] == priced["entityId"] else "EUR")

    stock = "receiving/bins/" + source
    draft = "inventory/positions/" + pos + "/draft-backing"
    stale(draft)
    checked(client.call(draft + "/" + safe["entityId"] + "/history", price_scale=False)["offer"], "EUR")
    stale(draft + "/" + priced["entityId"] + "/history")
    checked(client.call(draft + "/" + priced["entityId"] + "/history")["offer"], "IQD")
    first = client.call(draft + "?limit=1")
    for path in (draft + "?limit=1", draft + "?limit=1&after=" + first["nextAfter"]):
        page = client.call(path)
        offer, = page["offers"]
        if offer["offerId"] == priced["entityId"]:
            stale(path)
        else:
            checked(client.call(path, price_scale=False)["offers"][0], "EUR")

    where = dict(purchaseId=buy, positionId=pos, locationId=source, confirmedLocationCode="SCALE-A")
    _, withdrawn = post(client, "inventory/draft-backing-withdrawals", priced["revision"], **where,
                        offerId=priced["entityId"], quantity=1, reason="Reduce draft backing")
    receipt = draft + "/receipts?receiptId=" + withdrawn["entityId"]
    stale(receipt)
    checked(client.call(receipt), "IQD")
    _, moved = post(client, "receiving/transfers", withdrawn["revision"], purchaseId=buy, sourcePlacementId=pos,
                    sourceLocationId=source, confirmedSourceLocationCode="SCALE-A", destinationLocationId=destination,
                    confirmedDestinationLocationCode="SCALE-B", quantity=1, reason="Move one backed piece",
                    backing=[{"offerId": priced["entityId"], "quantity": 1}])
    assert not client.call("receiving/bins/" + empty + "/movements", price_scale=False)["movements"]
    stale(stock + "/movements")
    movements = client.call(stock + "/movements")
    checked(movements["movements"][0]["backing"][0], "IQD")
    for offer in movements["positions"][0]["backing"]:
        checked(offer, "IQD" if offer["offerId"] == priced["entityId"] else "EUR")

    _, count = post(client, "receiving/counts", moved["revision"], **where, countedQuantity=1, reason="Witnessed shortage")
    case = count["entityId"]
    position = stock + "/positions/" + pos
    review = position + "/shortages/" + case + "/adjustment-review"
    stale(review)
    for offer in client.call(review)["offers"]:
        checked(offer, "IQD" if offer["id"] == priced["entityId"] else "EUR")
    _, saved = post(client, "receiving/stock-losses", count["revision"], **where, caseId=case,
                    expectedObservationId=case, reason="Release the selected backing for the missing stock",
                    offerReductions=[{"offerId": priced["entityId"], "quantity": 1}])
    loss = position + "/posted-loss?postingId=" + saved["entityId"]
    stale(loss)
    evidence = client.call(loss)
    assert evidence["receipt"]["currency"] == "HUF"
    checked(evidence["reductions"][0], "IQD")
    print("Offer-price scale negotiated across receiving, transfers, withdrawal, adjustment and posted-loss evidence.", flush=True)
