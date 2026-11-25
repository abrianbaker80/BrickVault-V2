"""Verify frozen matching sets, original batch receipts, pending tasks and their witnessed counts."""

from urllib.parse import urlencode
from uuid import uuid4

from verify_receiving import post, purchase_row


ROOT = "inventory/recounts"


def read_rows(client, path):
    query = {"limit": 1}
    rows, seen = [], set()
    while True:
        page = client.call(path + "?" + urlencode(query))
        assert page["complete"] and page["asOf"]
        rows.extend(page["rows"])
        cursor = page["nextAfter"]
        if cursor is None:
            assert len(rows) == page["total"]
            return rows
        assert cursor not in seen and len(page["rows"]) == 1
        seen.add(cursor)
        query["after"] = cursor


def qualify_recounts(client, catalog):
    code = "QUEUE-01"
    _, bin_receipt = post(client, "locations", 0, code=code, name="Recount shelf")
    location = bin_receipt["entityId"]
    reference = "QUEUE-QUAL-"

    def position(suffix):
        _, purchase = post(client, "receiving/purchases", 0, reference=reference + suffix,
                           source="Fictional count exercise", currency="USD", orderedQuantity=3, pendingChargeMinor=None)
        buy = purchase["entityId"]
        _, delivery = post(client, "receiving/deliveries", purchase["revision"], purchaseId=buy,
                           reference="Carton", quantity=3, catalogId=catalog, itemCostMinor=None)
        _, put = post(client, "receiving/putaway", delivery["revision"], purchaseId=buy, deliveryId=delivery["entityId"],
                      catalogId=catalog, locationId=location, confirmedLocationCode=code, quantity=3)
        return buy, put["entityId"], put["revision"]

    def witness(row):
        buy, position_id, revision = row
        _, count = post(client, "receiving/counts", revision, purchaseId=buy, positionId=position_id, locationId=location,
                        confirmedLocationCode=code, countedQuantity=3, reason="Verified all three pieces on the shelf")
        return count["entityId"]

    def preview(selected=None):
        pid = str(uuid4())
        body = dict(previewId=pid, scope="selected" if selected else "matching", positionIds=selected or [],
                    q=None if selected else reference, itemType=None, locationId=None)
        result = client.call(ROOT + "/previews", "POST", body, expected=(200, 201))
        assert result["previewId"] == pid and result["complete"]
        return pid

    def confirm(pid):
        command = dict(commandId=str(uuid4()), previewId=pid)
        receipt = client.call(ROOT + "/batches", "POST", command, expected=(200, 201))
        assert (receipt["kind"], receipt["entityId"]) == ("recount", pid)
        return command, receipt

    a, b = position("A"), position("B")
    frozen = preview()
    assert {r["positionId"] for r in read_rows(client, f"{ROOT}/previews/{frozen}")} == {a[1], b[1]}
    witness(a)
    c = position("C")
    command, receipt = confirm(frozen)
    results = read_rows(client, f"{ROOT}/batches/{frozen}")
    assert {r["target"]["positionId"]: r["outcome"] for r in results} == {a[1]: "changed", b[1]: "queued"}
    repeated = preview([b[1]])
    confirm(repeated)
    assert read_rows(client, f"{ROOT}/batches/{repeated}")[0]["outcome"] == "already-queued"
    observed = witness(b)
    pending = preview([c[1]])
    confirm(pending)
    evidence = command, receipt, repeated, pending, a[1], b[1], c[1], observed
    verify_recovered_recounts(client, evidence)
    for buy, _, _ in (a, b, c):
        balances = purchase_row(client, buy)["balances"]
        assert balances["physicalOnHand"] == 3 and balances["reserved"] == balances["backedQuantity"] == 0
    return evidence


def verify_recovered_recounts(client, evidence):
    command, receipt, repeated, pending, a, b, c, observed = evidence
    assert client.call(ROOT + "/batches", "POST", command) == receipt
    diagnostic = client.call("diagnostics/" + receipt["correlationId"])
    assert (diagnostic["kind"], diagnostic["commandId"], diagnostic["entityId"]) == (
        "recount", command["commandId"], receipt["entityId"])
    results = {r["target"]["positionId"]: r for r in read_rows(client, f"{ROOT}/batches/{receipt['entityId']}")}
    assert set(results) == {a, b}
    assert (results[a]["outcome"], results[b]["outcome"], results[b]["observationId"]) == ("changed", "queued", observed)
    assert read_rows(client, f"{ROOT}/batches/{repeated}")[0]["observationId"] == observed
    q = {r["target"]["positionId"]: r["batchId"] for r in read_rows(client, ROOT + "/queue")}
    assert q.get(c) == pending and a not in q and b not in q
