"""Reconcile a paged position timeline with its count, posting, holds and current stock."""

from urllib.parse import urlencode


def verify_timeline(client, loss):
    purchase, current, _, posting, _ = loss
    position = posting["positionId"]
    path = f"inventory/positions/{position}/timeline"
    events = []
    seen = set()
    query = {"limit": 3}
    while True:
        page = client.call(path + "?" + urlencode(query))
        assert page["complete"] and page["asOf"] and page["scope"] == "position-timeline"
        assert page["hasUndatedEvents"] is False
        assert (page["positionId"], page["purchaseId"], page["deliveryId"]) == (
            position, purchase, posting["receipt"]["deliveryId"]
        )
        assert page["location"]["id"] == posting["location"]["id"]
        assert len(page["events"]) <= 3
        events.extend(page["events"])
        cursor = page["nextAfter"]
        if cursor is None:
            break
        assert cursor not in seen and len(page["events"]) == 3
        seen.add(cursor)
        query["after"] = cursor

    assert len(events) == 8 and len({e["commandId"] for e in events}) == 8
    assert [e["kind"] for e in reversed(events)] == [
        "putaway", "offer-backing", "offer-backing", "hold", "hold", "count", "hold-release", "stock-loss"
    ]
    revisions = [event["purchaseRevision"] for event in events]
    assert revisions == sorted(set(revisions), reverse=True)
    balance = current["balances"]
    assert sum(e["physicalDelta"] for e in events) == balance["physicalOnHand"] == 5
    assert sum(e["reservedDelta"] for e in events) == balance["reserved"] == 2
    assert sum(e["offerBackedDelta"] for e in events) == balance["backedQuantity"] == 3
    count = next(e for e in events if e["kind"] == "count")
    assert (count["observationId"], count["quantity"], count["ledgerQuantityAtWitness"], count["physicalDelta"]) == (
        posting["reviewedObservation"]["id"], 5, 10, 0
    )
    assert (events[0]["postingId"], events[0]["commandId"], events[0]["offerBackedDelta"]) == (
        posting["postingId"], posting["commandId"], -3
    )
    return events
