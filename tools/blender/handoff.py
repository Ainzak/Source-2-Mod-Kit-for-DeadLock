"""Geometry-only authoring contracts. No Source 2 decoder or compiled writer."""
import argparse
from fractions import Fraction
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import tempfile


class HandoffError(ValueError):
    pass


def require(ok, code):
    if not ok:
        raise HandoffError(code)


def closed(value, keys):
    require(type(value) is dict and set(value) == set(keys), "CONTRACT_PROPERTIES_INVALID")


def integer(value, maximum=4000000):
    require(type(value) is int and 0 <= value <= maximum, "INTEGER_INVALID")


def sha(value):
    require(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value), "SHA256_INVALID")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def parse(data):
    def pairs(items):
        value = {}
        for key, item in items:
            require(key not in value, "DUPLICATE_JSON_PROPERTY")
            value[key] = item
        return value
    def invalid(_):
        raise HandoffError("NONFINITE_JSON_NUMBER")
    return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid)


def load(path):
    return parse(Path(path).read_bytes())


def publish_bytes(path, data):
    path = Path(path)
    require(not path.exists(), "OUTPUT_ALREADY_EXISTS")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".s2mod-handoff-", dir=path.parent)
    temp = Path(name)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        publish_existing(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def publish_existing(temp, path):
    # Windows rename and POSIX hard-link creation fail if another publisher won the name.
    require(not Path(path).exists(), "OUTPUT_ALREADY_EXISTS")
    if os.name == "nt":
        os.rename(temp, path)
    else:
        os.link(temp, path)


def word(value):
    require(type(value) in (int, float) and math.isfinite(value), "POSITION_INVALID")
    try:
        return struct.pack("<f", value).hex()
    except (OverflowError, struct.error) as error:
        raise HandoffError("POSITION_INVALID") from error


def decode(value):
    require(type(value) is str and re.fullmatch(r"[0-9a-f]{8}", value), "FLOAT32_WORD_INVALID")
    number = struct.unpack("<f", bytes.fromhex(value))[0]
    require(math.isfinite(number), "FLOAT32_NONFINITE")
    return number


def positions(value):
    require(type(value) is list and 0 < len(value) <= 500000, "POSITION_COUNT_INVALID")
    for point in value:
        require(type(point) is list and len(point) == 3, "POSITION_INVALID")
        for item in point:
            decode(item)


def member_id(row):
    return f"lod:{row['lod']}/mesh:{row['mesh']}/buffer:{row['buffer']}"


def bundle_validate(bundle):
    closed(bundle, ["schemaVersion", "kind", "inputHash", "snapshotSha256", "requiredLods",
                    "maximumDisplacement", "omittedAttributes", "members"])
    require(type(bundle["schemaVersion"]) is int and bundle["schemaVersion"] == 1 and
            bundle["kind"] == "geometry_authoring_handoff", "CONTRACT_VERSION_INVALID")
    sha(bundle["inputHash"]); sha(bundle["snapshotSha256"])
    require(bundle["omittedAttributes"] == ["rig", "uv", "weights", "materials", "packedFrames"], "OMITTED_ATTRIBUTES_INVALID")
    limit = bundle["maximumDisplacement"]
    require(type(limit) in (int, float) and math.isfinite(limit) and 0 < limit <= 1000, "DISPLACEMENT_LIMIT_INVALID")
    lods = bundle["requiredLods"]
    require(type(lods) is list and 0 < len(lods) <= 16, "LOD_INVENTORY_INVALID")
    for lod in lods: integer(lod, 255)
    require(lods == sorted(set(lods)), "LOD_INVENTORY_INVALID")
    members = bundle["members"]
    require(type(members) is list and 0 < len(members) <= 256, "MEMBER_COUNT_INVALID")
    seen, selected_lods, total = set(), set(), 0
    for m in members:
        closed(m, ["memberId", "lod", "mesh", "buffer", "sourceDecodedHash", "editable", "positions", "triangles", "protectedIds"])
        for key in ["lod", "mesh", "buffer"]: integer(m[key], 65535)
        require(m["lod"] in lods, "LOD_INVENTORY_INVALID")
        require(m["memberId"] == member_id(m) and m["memberId"] not in seen, "MEMBER_ID_INVALID")
        seen.add(m["memberId"]); sha(m["sourceDecodedHash"])
        require(type(m["editable"]) is bool, "EDITABLE_INVALID")
        if m["editable"]: selected_lods.add(m["lod"])
        positions(m["positions"]); total += len(m["positions"])
        require(total <= 2000000, "BUNDLE_VERTEX_BUDGET_EXCEEDED")
        indices = m["triangles"]
        require(type(indices) is list and 0 < len(indices) <= 6000000 and len(indices) % 3 == 0, "TRIANGLES_INVALID")
        for i in indices: integer(i, len(m["positions"])-1)
        protected = m["protectedIds"]
        require(type(protected) is list, "PROTECTED_IDS_INVALID")
        for i in protected: integer(i, len(m["positions"])-1)
        require(protected == sorted(set(protected)), "PROTECTED_IDS_INVALID")
        if not m["editable"]:
            require(protected == list(range(len(m["positions"]))), "CONTEXT_PROTECTION_INVALID")
    require(selected_lods == set(lods), "LOD_SELECTION_INCOMPLETE")
    return bundle


def trusted_bundle(path, expected_hash):
    sha(expected_hash)
    data = Path(path).read_bytes()
    require(digest(data) == expected_hash, "BUNDLE_HASH_MISMATCH")
    return bundle_validate(parse(data))


def prepare(snapshot_path, policy_path):
    snapshot_bytes = Path(snapshot_path).read_bytes()
    snapshot = parse(snapshot_bytes); policy = load(policy_path)
    closed(policy, ["schemaVersion", "inputHash", "members", "maximumDisplacement"])
    require(type(policy["schemaVersion"]) is int and policy["schemaVersion"] == 1, "CONTRACT_VERSION_INVALID")
    require(policy["inputHash"] == snapshot["inputHash"], "SOURCE_HASH_MISMATCH")
    require(type(policy["members"]) is list and len(policy["members"]) > 0, "SELECTION_EMPTY")
    selection = {}
    for m in policy["members"]:
        closed(m, ["memberId", "protectedIds"])
        require(type(m["memberId"]) is str and m["memberId"] not in selection, "SELECTION_DUPLICATE")
        selection[m["memberId"]] = m["protectedIds"]
    result = {"schemaVersion": 1, "kind": "geometry_authoring_handoff", "inputHash": snapshot["inputHash"],
              "snapshotSha256": digest(snapshot_bytes),
              "requiredLods": sorted(set(r["lod"] for r in snapshot["rows"])),
              "maximumDisplacement": policy["maximumDisplacement"],
              "omittedAttributes": ["rig", "uv", "weights", "materials", "packedFrames"], "members": []}
    for r in snapshot["rows"]:
        mid = member_id(r)
        result["members"].append({"memberId": mid, "lod": r["lod"], "mesh": r["mesh"], "buffer": r["buffer"],
                                  "sourceDecodedHash": r["decodedHash"], "editable": mid in selection,
                                  "positions": [[word(v) for v in p] for p in r["points"]],
                                  "triangles": [i for c in r["calls"] for i in c["triangles"]],
                                  "protectedIds": selection.get(mid, list(range(len(r["points"]))))})
    require(set(selection) <= {m["memberId"] for m in result["members"]}, "SELECTION_MEMBER_MISSING")
    return bundle_validate(result)


def quantized(point):
    # Exact float32 * 2**149 as an integer, including subnormal coordinates.
    result = []
    for value in point:
        bits = struct.unpack("<I", bytes.fromhex(value))[0]
        exponent, mantissa = (bits >> 23) & 255, bits & 0x7fffff
        q = mantissa if exponent == 0 else ((1 << 23) | mantissa) << (exponent-1)
        result.append(-q if bits >> 31 else q)
    return tuple(result)


def cross(a, b, c):
    u = [b[i]-a[i] for i in range(3)]; v = [c[i]-a[i] for i in range(3)]
    return (u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])


def verify(bundle, bundle_hash, delta):
    bundle_validate(bundle); sha(bundle_hash)
    closed(delta, ["schemaVersion", "kind", "operation", "inputHash", "bundleSha256", "blenderVersion", "targetKey", "members"])
    require(type(delta["schemaVersion"]) is int and delta["schemaVersion"] == 1 and delta["kind"] == "geometry_authoring_delta", "CONTRACT_VERSION_INVALID")
    closed(delta["operation"], ["name", "version"])
    require(delta["operation"]["name"] == "author_positions" and type(delta["operation"]["version"]) is int and delta["operation"]["version"] == 1, "AUTHORING_OPERATION_INVALID")
    require(delta["bundleSha256"] == bundle_hash and delta["inputHash"] == bundle["inputHash"], "DELTA_SOURCE_MISMATCH")
    require(type(delta["blenderVersion"]) is str and 0 < len(delta["blenderVersion"]) <= 128, "BLENDER_VERSION_INVALID")
    require(delta["targetKey"] == "S2ModKit_Edit", "TARGET_KEY_INVALID")
    require(type(delta["members"]) is list, "DELTA_MEMBERS_INVALID")
    expected = {m["memberId"]: m for m in bundle["members"] if m["editable"]}
    seen, facts = set(), []
    limit = Fraction.from_float(float(bundle["maximumDisplacement"]))
    bound_num = (limit.numerator**2) << 298
    bound_den = limit.denominator**2
    for returned in delta["members"]:
        closed(returned, ["memberId", "positions"])
        mid = returned["memberId"]
        require(type(mid) is str and mid in expected and mid not in seen, "DELTA_MEMBER_INVALID")
        seen.add(mid); source = expected[mid]; target = returned["positions"]
        positions(target)
        require(len(target) == len(source["positions"]), "VERTEX_COUNT_CHANGED")
        for i in source["protectedIds"]:
            require(target[i] == source["positions"][i], "PROTECTED_POSITION_CHANGED")
        sp = [quantized(p) for p in source["positions"]]; tp = [quantized(p) for p in target]
        coincidence = {}
        for a, b in zip(sp, tp):
            require(a not in coincidence or coincidence[a] == b, "SOURCE_COINCIDENCE_SPLIT")
            coincidence[a] = b
            require(sum((a[i]-b[i])**2 for i in range(3))*bound_den <= bound_num, "DISPLACEMENT_EXCEEDED")
        indices = source["triangles"]
        for offset in range(0, len(indices), 3):
            ids = indices[offset:offset+3]
            a, b = [sp[i] for i in ids], [tp[i] for i in ids]
            sn, tn = cross(*a), cross(*b)
            if sn == (0, 0, 0):
                has_pair = len(set(a)) < 3
                require((has_pair and tn == (0, 0, 0)) or a == b, "SOURCE_COLLAPSE_CHANGED")
            else:
                require(tn != (0, 0, 0) and sum(sn[i]*tn[i] for i in range(3)) > 0, "TRIANGLE_HEMISPHERE_FAILED")
        facts.append({"memberId": mid, "changedRecords": sum(a != b for a,b in zip(source["positions"], target)),
                      "triangles": len(indices)//3, "positionPreflight": "passed"})
    require(seen == set(expected), "LOD_RETURN_INCOMPLETE")
    return {"schemaVersion": 1, "kind": "geometry_authoring_preflight", "inputHash": bundle["inputHash"],
            "bundleSha256": bundle_hash, "deltaSha256": digest(canonical(delta)), "members": facts,
            "compiledWriterQualified": False, "framesQualified": False, "runtimeQualified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_cmd = commands.add_parser("prepare")
    prepare_cmd.add_argument("--snapshot", required=True); prepare_cmd.add_argument("--policy", required=True)
    prepare_cmd.add_argument("--output", required=True)
    verify_cmd = commands.add_parser("verify")
    verify_cmd.add_argument("--bundle", required=True); verify_cmd.add_argument("--bundle-sha256", required=True)
    verify_cmd.add_argument("--delta", required=True); verify_cmd.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            result = prepare(args.snapshot, args.policy)
        else:
            data = Path(args.delta).read_bytes()
            delta = parse(data)
            require(data == canonical(delta), "DELTA_NONCANONICAL")
            result = verify(trusted_bundle(args.bundle, args.bundle_sha256), args.bundle_sha256, delta)
        publish_bytes(args.output, canonical(result))
        print(f"OK: {args.command}; {len(result['members'])} members; SHA-256 {digest(canonical(result))}; geometry-only authoring.")
    except (HandoffError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        parser.exit(1, f"FAILED: {error}\n")


if __name__ == "__main__":
    main()
