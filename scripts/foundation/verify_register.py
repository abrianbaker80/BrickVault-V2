"""Verify signed-in stock searches against independently read bin positions."""

import uuid
from urllib.parse import urlencode


def verify_register(client, evidence):
    locations, purchase_id, bins, *_ = evidence
    endpoint = "inventory/positions"
    reference = "PO-208"
    expected = {
        layer["placementId"]: (locations[code], layer)
        for code, record in bins.items()
        for layer in record["layers"]
    }

    def search(**filters):
        return client.call(endpoint + "?" + urlencode(filters))

    client.call(endpoint + "?limit=0", expected=400)
    client.call(endpoint + "?itemType=unsupported", expected=400)
    first = search(q=reference.lower(), limit=2)
    assert first["scope"] == "inventory-positions" and first["complete"]
    assert first["matchingPositions"] == len(expected)
    assert first["matchingUnits"] == sum(layer["quantity"] for _, layer in expected.values())
    assert len(first["rows"]) == 2 and first["nextAfter"]
    second = search(q=reference.lower(), limit=2, after=first["nextAfter"])
    assert second["nextAfter"] is None
    assert second["matchingUnits"] == first["matchingUnits"]
    rows = first["rows"] + second["rows"]
    ids = [row["layer"]["placementId"] for row in rows]
    assert ids == sorted(expected)
    for row in rows:
        layer = row["layer"]
        location_id, original = expected[layer["placementId"]]
        assert row["location"]["id"] == location_id and layer == original
        assert search(positionId=layer["placementId"])["rows"] == [row]
        assert uuid.UUID(row["originPlacementId"])

    assert search(q=purchase_id)["rows"] == rows
    for code, location_id in locations.items():
        filtered = search(q=reference, locationId=location_id)
        assert filtered["matchingUnits"] == sum(r["quantity"] for r in bins[code]["layers"])
        assert all(row["location"]["id"] == location_id for row in filtered["rows"])
    assert search(q=reference, itemType=rows[0]["layer"]["currentCatalog"]["itemType"])["rows"] == rows
    empty = search(q="%_")
    assert empty["complete"] and (empty["matchingUnits"], empty["rows"]) == (0, [])
    # The middle position was created by the first transfer and sent one unit onward.
    _, _, _, _, first_transfer, _, onward = evidence
    middle = first_transfer["entityId"]
    path = endpoint + "/" + middle + "/movements"
    page = client.call(path + "?limit=1")
    assert page["complete"] and page["scope"] == "position-movements" and page["positionId"] == middle
    assert [row["id"] for row in page["rows"]] == [onward["entityId"]]
    assert page["nextAfter"] == onward["entityId"]
    older = client.call(path + "?limit=1&after=" + page["nextAfter"])
    assert [row["id"] for row in older["rows"]] == [middle] and older["nextAfter"] is None
    assert all("backing" not in row for row in page["rows"] + older["rows"])
    origin = older["rows"][0]["sourcePlacementId"]
    origin_path = endpoint + "/" + origin + "/movements"
    assert [row["id"] for row in client.call(origin_path)["rows"]] == [middle]
    client.call(origin_path + "?after=" + onward["entityId"], expected=400)
    client.call(endpoint + "/" + str(uuid.uuid4()) + "/movements", expected=404)
    print("Stock register reconciled keyset pages, filtered totals, stable position identities and receipt costs.", flush=True)
