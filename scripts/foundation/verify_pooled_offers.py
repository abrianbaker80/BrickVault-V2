"""Check the buyer-visible set configuration through signed-in receiving and encrypted recovery."""

from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
import json
from threading import Barrier
from uuid import uuid4

from recovery import activate, backup, restore
from runtime import start, stop

CAPABILITIES = {"X-BrickVault-Set-Contents": "1", "X-BrickVault-Pooled-Offers": "1"}
CURRENT = "receiving/current?holds=true&losses=true&quarantine=true&recoveries=true&partOuts=true&pooledOffers=true"
FULL_SET = {"includesBox": True, "includesInstructions": True}
LOOSE_SET = {"includesBox": False, "includesInstructions": False}


def settle(client, command, expected):
    contents = command.get("setContents")
    sources = [[source["purchaseId"], source["expectedRevision"],
                [[row["placementId"], row["quantity"]] for row in source["backing"]]]
               for source in [command, *command["additionalSources"]]]
    fields = ["brickvault.pooled-offer.v1", command["commandId"], command["catalogId"],
              command["expectedCatalogRevision"], command["currency"], command["unitPriceMinor"],
              [contents["includesBox"], contents["includesInstructions"]] if contents else None, sources]
    digest = sha256(json.dumps(fields, separators=(",", ":")).encode()).hexdigest()
    return client.call(f"receiving/pooled-offers/{command['commandId']}/confirmation", "POST",
                       headers={**CAPABILITIES, "X-BrickVault-Command-Digest": digest}, expected=expected)


def check_closed(client, command):
    assert settle(client, command, 204) is None
    late = client.call("receiving/pooled-offers", "POST", command, headers=CAPABILITIES, expected=409)
    assert late["code"] == "pooled_offer_unapplied"
    changed = {**command, "unitPriceMinor": command["unitPriceMinor"] + 1}
    assert settle(client, changed, 409)["code"] == "command_mismatch"


def record_examples(client, observer):
    def post(path, revision, **fields):
        body = {"commandId": str(uuid4()), "expectedRevision": revision, **fields}
        reply = client.call(path, "POST", body, headers=CAPABILITIES, expected=(200, 201))
        assert reply["commandId"] == body["commandId"]
        return reply["entityId"]

    catalog = post("catalog", 0, itemType="set", itemNumber="LAB-TRAIN", variant=None, name="Fictional complete used train",
                   colorState="not-applicable", colorId=None, condition="used", completeness="complete",
                   source="Offer recovery qualification", license="operator-owned", mapping="confirmed")

    def purchase(label, currency, amount, contents):
        location = post("locations", 0, code=label, name="Train shelf " + label)
        order = post("receiving/purchases", 0, reference="TRAIN-" + label, source="Fictional purchase",
                     currency=currency, orderedQuantity=2, pendingChargeMinor=None)
        receipt = post("receiving/deliveries", 1, purchaseId=order, reference="Carton " + label,
                       quantity=2, catalogId=catalog, itemCostMinor=amount, setContents=contents)
        position = post("receiving/putaway", 2, purchaseId=order, deliveryId=receipt, catalogId=catalog,
                        locationId=location, confirmedLocationCode=label, quantity=2, setContents=contents)
        return order, position

    first, first_position = purchase("TRAIN-A", "EUR", 8500, FULL_SET)
    second, second_position = purchase("TRAIN-B", "USD", None, FULL_SET)
    loose, loose_position = purchase("TRAIN-C", "EUR", 0, LOOSE_SET)
    command = {"commandId": str(uuid4()), "purchaseId": first, "expectedRevision": 3, "catalogId": catalog,
               "expectedCatalogRevision": 1, "currency": "USD", "unitPriceMinor": 4900, "setContents": FULL_SET,
               "backing": [{"placementId": first_position, "quantity": 1}],
               "additionalSources": [{"purchaseId": second, "expectedRevision": 3,
                                      "backing": [{"placementId": second_position, "quantity": 2}]}]}
    incompatible = {**command, "commandId": str(uuid4()), "additionalSources": [
        {"purchaseId": loose, "expectedRevision": 3, "backing": [{"placementId": loose_position, "quantity": 1}]}
    ]}
    rejection = client.call("receiving/pooled-offers", "POST", incompatible, headers=CAPABILITIES, expected=409)
    assert rejection["code"] == "set_contents_mismatch"
    # Exercise two sessions contending for one command ID before either has a receipt.
    barrier = Barrier(2, timeout=15)

    def race_confirmation():
        barrier.wait()
        return settle(observer, command, (200, 204))

    with ThreadPoolExecutor(max_workers=1) as worker:
        proof = worker.submit(race_confirmation)
        barrier.wait()
        result = client.call("receiving/pooled-offers", "POST", command, headers=CAPABILITIES, expected=(200, 409))
        confirmed = proof.result()
    if confirmed is None:
        assert result["code"] == "pooled_offer_unapplied"
        command = {**command, "commandId": str(uuid4())}
        receipt = client.call("receiving/pooled-offers", "POST", command, headers=CAPABILITIES)
    else:
        assert result == confirmed and result["kind"] == "offer"
        receipt = result
    assert settle(client, command, 200) == receipt
    loose_offer = post("receiving/offers", 3, purchaseId=loose, catalogId=catalog, currency="USD", unitPriceMinor=3100,
                       setContents=LOOSE_SET, backing=[{"placementId": loose_position, "quantity": 1}])
    return command, receipt, loose, loose_offer


def checked_purchases(client, examples):
    command, receipt, loose, loose_offer = examples
    page = client.call(CURRENT, headers=CAPABILITIES)
    assert page["complete"]
    indexed = {p["id"]: p for p in page["purchases"]}
    first = indexed[command["purchaseId"]]
    second = indexed[command["additionalSources"][0]["purchaseId"]]
    for purchase, quantity, currency, cost in ((first, 1, "EUR", 8500), (second, 2, "USD", None)):
        offer, = purchase["offers"]
        assert offer["id"] == receipt["entityId"] and offer["setContents"] == FULL_SET
        assert offer["backedQuantity"] == quantity and offer["pool"]["totalBackedQuantity"] == 3
        assert offer["currency"] == "USD" and offer["unitPriceMinor"] == 4900 and offer["publishedQuantity"] == 0
        assert purchase["currency"] == currency and purchase["deliveries"][0]["itemCostMinor"] == cost
        assert purchase["balances"]["physicalOnHand"] == 2 and purchase["balances"]["backedQuantity"] == quantity
    other = indexed[loose]
    separate, = other["offers"]
    assert separate["id"] == loose_offer and separate["id"] != receipt["entityId"]
    assert separate["setContents"] == LOOSE_SET and separate["publishedQuantity"] == 0
    assert other["deliveries"][0]["itemCostMinor"] == 0
    return first, second, other


def qualify_set_workspace(lab, port, init, browser, run_tests, poll):
    source = init(lab / "set-stock", lab / "set-keys", port)
    archive = lab / "set-keys" / "set-contents.age"
    try:
        start(source)
        run_tests(source, "set-stock")
        client = browser(source)
        password = (source.path / "owner-login.txt").read_text().splitlines()[1]
        client.login(password)
        observer = browser(source)
        observer.login(password)
        examples = record_examples(client, observer)
        before = checked_purchases(client, examples)
        closed = {**examples[0], "commandId": str(uuid4())}
        check_closed(client, closed)
        assert checked_purchases(client, examples) == before
        poll(lambda: client.call("diagnostics"), lambda status: status["pendingJobs"] == 0)
        proof = backup(source, archive)
        assert proof["facts"]["set_position_inspections"]["rows"] >= 4
        assert proof["facts"]["position_set_contents"]["rows"] >= 4
    finally:
        stop(source)

    rescue = init(lab / "restored-set-stock", None, port, recipient=source.config["recipient"])
    target = "bv_m2_restored_set_stock"
    rescue.pg_start()
    try:
        restored = restore(rescue, archive, lab / "set-keys" / (source.config["id"] + ".agekey"), target)
        assert restored["facts"] == proof["facts"]
    finally:
        rescue.pg_stop()
    activate(rescue, target)
    try:
        start(rescue)
        restored_client = browser(rescue)
        restored_client.login((rescue.path / "owner-login.txt").read_text().splitlines()[1])
        assert restored_client.lineage == client.lineage
        assert checked_purchases(restored_client, examples) == before
        command, receipt, _, _ = examples
        assert restored_client.call("receiving/pooled-offers", "POST", command, headers=CAPABILITIES) == receipt
        assert settle(restored_client, command, 200) == receipt
        check_closed(restored_client, closed)
        assert checked_purchases(restored_client, examples) == before
    finally:
        stop(rescue)
    print("Restored signed-in purchase layers, set contents, distinct draft offers and exact pooled receipt: passed.", flush=True)
