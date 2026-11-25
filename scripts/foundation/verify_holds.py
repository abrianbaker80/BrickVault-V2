"""Qualify an explicit release against a witnessed shortage and restored command receipts."""

import uuid
from urllib.parse import urlencode

from verify_receiving import post, purchase_row


def hold_snapshot(client, bin_id, position_id):
    view = client.call(f"receiving/bins/{bin_id}/positions/{position_id}/holds")
    assert view.pop("asOf") and view["complete"]
    return view


def qualify_holds(client, catalog_id):
    _, location = post(client, "locations", 0, code="HLD-01", name="Build hold fixture")
    bin_id = location["entityId"]
    _, order = post(client, "receiving/purchases", 0, reference="HOLDS-23", source="Training purchase",
                    currency="USD", orderedQuantity=10, pendingChargeMinor=None)
    purchase_id = order["entityId"]
    _, delivery = post(client, "receiving/deliveries", order["revision"], purchaseId=purchase_id,
                       reference="Project carton", quantity=10, catalogId=catalog_id, itemCostMinor=790)
    _, placement = post(client, "receiving/putaway", delivery["revision"], purchaseId=purchase_id,
                        deliveryId=delivery["entityId"], catalogId=catalog_id, locationId=bin_id,
                        confirmedLocationCode="HLD-01", quantity=10)
    position_id = placement["entityId"]
    _, offer = post(client, "receiving/offers", placement["revision"], purchaseId=purchase_id,
                    catalogId=catalog_id, unitPriceMinor=230, backing=[{"placementId": position_id, "quantity": 2}])
    fields = dict(purchaseId=purchase_id, positionId=position_id, locationId=bin_id,
                  confirmedLocationCode="HLD-01")
    path = "receiving/holds"
    first, a = post(client, path, offer["revision"], **fields, quantity=3, purpose="Training bridge", reason="Project allocation")
    second, b = post(client, path, a["revision"], **fields, quantity=4, purpose="Training tower", reason="Project allocation")
    assert client.call(path, "POST", first) == a
    count_request, count = post(client, "receiving/counts", b["revision"], **fields, countedQuantity=5,
                                reason="Physical recount confirmed five pieces")
    count_view = client.call(f"receiving/bins/{bin_id}/counts?observationId={count['entityId']}")
    case = count_view["shortages"][0]
    releases = dict(purchaseId=purchase_id, holdId=b["entityId"], quantity=2, reason="Owner selected tower hold",
                    shortageId=case["id"], expectedObservationId=count["entityId"])
    before = hold_snapshot(client, bin_id, position_id)
    def amounts(view):
        return {row["id"]: row["remainingQuantity"] for row in view["holds"]}
    assert (before["ledgerQuantity"], before["offerBackedQuantity"], before["reservedQuantity"],
            before["availableToHoldQuantity"]) == (10, 2, 7, 0)
    for old_endpoint in ("receiving", "receiving/current"):
        client.call(old_endpoint, expected=426)
    assert amounts(before) == {a["entityId"]: 3, b["entityId"]: 4}
    denied = dict(commandId=str(uuid.uuid4()), expectedRevision=count["revision"],
                  **{**releases, "expectedObservationId": str(uuid.uuid4())})
    client.call(path + "/release", "POST", denied, expected=409)
    chosen, receipt = post(client, path + "/release", count["revision"], **releases)
    assert client.call(path + "/release", "POST", chosen) == receipt
    assert client.call("receiving/counts", "POST", count_request) == count
    client.call(path + "/release", "POST", {**chosen, "commandId": str(uuid.uuid4()), "holdId": a["entityId"]}, expected=409)
    retained = hold_snapshot(client, bin_id, position_id)
    assert (retained["ledgerQuantity"], retained["offerBackedQuantity"], retained["reservedQuantity"],
            retained["availableToHoldQuantity"]) == (10, 2, 5, 0)
    purchase = purchase_row(client, purchase_id)
    balances = purchase["balances"]
    assert (balances["physicalOnHand"], balances["unavailable"], balances["reserved"], balances["free"]) == (10, 0, 5, 5)
    assert balances["knownCostFreeQuantity"] == 5 and balances["unknownCostFreeQuantity"] == 0
    assert purchase["deliveries"][0]["itemCostMinor"] == 790 and purchase["offers"][0]["backedQuantity"] == 2
    assert amounts(retained) == {a["entityId"]: 3, b["entityId"]: 2}
    for term in (a["entityId"], "training BRIDGE"):
        filtered = client.call("inventory/positions?" + urlencode({"q": term, "locationId": bin_id}))
        assert filtered["complete"] and filtered["matchingPositions"] == 1
        assert filtered["rows"][0]["layer"]["placementId"] == position_id
    count_again = client.call(f"receiving/bins/{bin_id}/counts?observationId={count['entityId']}")
    assert count_again["shortages"][0]["id"] == case["id"] and count_again["shortages"][0]["state"] == "open"
    commands = [(path, first, a), (path, second, b), (path + "/release", chosen, receipt)]
    return bin_id, position_id, retained, commands


def verify_restored_holds(client, evidence):
    bin_id, position_id, expected, commands = evidence
    assert hold_snapshot(client, bin_id, position_id) == expected
    for path, body, original_receipt in commands:
        assert client.call(path, "POST", body) == original_receipt
    assert hold_snapshot(client, bin_id, position_id) == expected
    print("Restored both named build holds and the owner's single partial release against the open count case.", flush=True)
