"""Small ASCII DXF entity reader for grading (CIRCLE, LINE, POLYLINE/VERTEX, LWPOLYLINE).

Every group-code-0 record ends the entity being read, and end of input flushes
it too, so a single or final CIRCLE is kept and never swallows the polylines
that follow (both were defects of the original grader's parser).
"""

import math

from .geometry import arc_samples, bulge_arc


def _pairs(text):
    lines = text.splitlines()
    out = []
    i = 0
    while i + 1 < len(lines):
        try:
            code = int(lines[i].strip())
        except ValueError:
            i += 1
            continue
        out.append((code, lines[i + 1].strip()))
        i += 2
    return out


def parse_entities(text):
    """Raw entities in the DXF frame (y-up)."""
    ents = []
    cur = None          # entity record being read
    poly = None         # open POLYLINE awaiting SEQEND
    section = None
    expect_section_name = False

    def flush():
        nonlocal cur
        if cur is None:
            return
        if cur["type"] == "VERTEX":
            if poly is not None and "x" in cur and "y" in cur:
                poly["verts"].append((cur["x"], cur["y"], cur.get("bulge", 0.0)))
                poly.setdefault("span_widths", []).extend(cur.get("span_widths", []))
            elif poly is not None:
                poly["invalid"] = True
        elif cur["type"] == "POLYLINE":
            pass  # header already registered as `poly`
        elif cur["type"] == "LWPOLYLINE":
            if cur.get("_v") is not None:
                cur["verts"].append(tuple(cur["_v"]))
            cur.pop("_v", None)
            ents.append(cur)
        elif cur["type"] in ("CIRCLE", "LINE"):
            ents.append(cur)
        cur = None

    def close_poly():
        nonlocal poly
        if poly is not None:
            ents.append(poly)
        poly = None

    for code, val in _pairs(text):
        if code == 0:
            flush()
            if val == "SECTION":
                expect_section_name = True
                continue
            if val == "ENDSEC":
                close_poly()
                section = None
                continue
            if val == "EOF":
                close_poly()
                break
            if section != "ENTITIES":
                continue
            if val == "POLYLINE":
                close_poly()
                poly = {"type": "POLYLINE", "closed": False, "width": 0.0, "verts": []}
                cur = {"type": "POLYLINE"}
            elif val == "VERTEX":
                cur = {"type": "VERTEX"}
            elif val == "SEQEND":
                close_poly()
            elif val == "LWPOLYLINE":
                close_poly()
                cur = {"type": "LWPOLYLINE", "closed": False, "width": 0.0, "verts": [], "_v": None}
            elif val in ("CIRCLE", "LINE"):
                close_poly()
                cur = {"type": val}
            else:
                close_poly()
            continue
        if expect_section_name and code == 2:
            section = val
            expect_section_name = False
            continue
        if cur is None:
            continue
        t = cur["type"]
        f = None
        try:
            f = float(val)
        except ValueError:
            pass
        if t in ("POLYLINE", "VERTEX", "LWPOLYLINE") and code in (40, 41, 43):
            target = poly if t == "POLYLINE" else cur
            target.setdefault("span_widths", []).append(f)
        if t == "CIRCLE":
            if code == 10: cur["x"] = f
            elif code == 20: cur["y"] = f
            elif code == 40: cur["r"] = f
        elif t == "LINE":
            if code == 10: cur["x1"] = f
            elif code == 20: cur["y1"] = f
            elif code == 11: cur["x2"] = f
            elif code == 21: cur["y2"] = f
        elif t == "POLYLINE" and poly is not None:
            if code == 70: poly["closed"] = bool(int(f) & 1) if f is not None and math.isfinite(f) and f == int(f) else None
            elif code in (40, 43): poly["width"] = f
        elif t == "VERTEX":
            if code == 10: cur["x"] = f
            elif code == 20: cur["y"] = f
            elif code == 42: cur["bulge"] = f
        elif t == "LWPOLYLINE":
            if code == 70: cur["closed"] = bool(int(f) & 1) if f is not None and math.isfinite(f) and f == int(f) else None
            elif code in (43, 40) and cur["_v"] is None: cur["width"] = f
            elif code == 10:
                if cur["_v"] is not None:
                    cur["verts"].append(tuple(cur["_v"]))
                cur["_v"] = [f, None, 0.0]
            elif code == 20 and cur["_v"] is not None: cur["_v"][1] = f
            elif code == 42 and cur["_v"] is not None: cur["_v"][2] = f
    flush()
    close_poly()
    return ents


def to_mm(ents, height_px, reg):
    """Convert raw entities to page millimetres (y-down) through the registration.

    Returns {"circles": [...], "polylines": [...], "lines": [...]}. A polyline
    carries exact sampled points (bulges sampled on their true arcs) and, when
    every segment is a bulge arc of one common circle covering 360 degrees, an
    `exact_circle` (centre, radius): a width-bearing bulged POLYLINE is exact
    circular geometry and need not be a CIRCLE entity.
    """
    def P(x, y):
        return reg.to_mm((x, height_px - y))

    circles, polys, lines = [], [], []
    for e in ents:
        if e["type"] == "CIRCLE" and all(k in e for k in ("x", "y", "r")):
            circles.append({"center_mm": P(e["x"], e["y"]), "r_mm": reg.len_mm(e["r"])})
        elif e["type"] == "LINE" and all(k in e for k in ("x1", "y1", "x2", "y2")):
            lines.append({"a": P(e["x1"], e["y1"]), "b": P(e["x2"], e["y2"])})
        elif e["type"] in ("POLYLINE", "LWPOLYLINE") and e["verts"]:
            v = e["verts"]
            # Do not silently bridge a missing/malformed vertex or sample NaN arcs.
            if (e.get("invalid") or e["closed"] is None
                    or not all(isinstance(x, (int, float)) and math.isfinite(x)
                               for vertex in v for x in vertex)
                    or not isinstance(e.get("width", 0.0), (int, float))
                    or not math.isfinite(e.get("width", 0.0))):
                continue
            n = len(v)
            segs = n if e["closed"] else n - 1
            pts = [P(v[0][0], v[0][1])]
            arcs = []
            straight = 0
            invalid = False
            for i in range(segs):
                p, q = v[i], v[(i + 1) % n]
                arc = bulge_arc((p[0], p[1]), (q[0], q[1]), p[2])
                if arc is None:
                    straight += 1
                    pts.append(P(q[0], q[1]))
                    continue
                cen, r, a0, sw = arc
                if not all(math.isfinite(x) for x in (*cen, r, a0, sw)) or r <= 0:
                    invalid = True
                    break
                k = max(4, int(abs(sw) / 5))
                for s in arc_samples(cen, r, a0, sw, k)[1:]:
                    pts.append(P(s[0], s[1]))
                arcs.append({"center_mm": P(cen[0], cen[1]), "r_mm": reg.len_mm(r), "sweep_deg": sw,
                             # Keep legacy sweep_deg in DXF y-up coordinates. These
                             # additive fields describe the registered page frame.
                             "page_sweep_deg": -sw, "start_mm": P(p[0], p[1]),
                             "end_mm": P(q[0], q[1])})
            if invalid:
                continue
            exact = None
            if arcs and straight == 0 and e["closed"]:
                c0, r0 = arcs[0]["center_mm"], arcs[0]["r_mm"]
                same = all(math.hypot(a["center_mm"][0] - c0[0], a["center_mm"][1] - c0[1]) <= 1e-6 * max(1, r0)
                           and abs(a["r_mm"] - r0) <= 1e-6 * max(1, r0) for a in arcs)
                total = sum(a["sweep_deg"] for a in arcs)
                if same and abs(abs(total) - 360.0) <= 1e-3:
                    exact = {"center_mm": c0, "r_mm": r0}
            polys.append({"closed": e["closed"], "points_mm": pts, "arcs": arcs,
                          "straight_segments": straight, "width_mm": reg.len_mm(e.get("width", 0.0)),
                          "exact_circle": exact, "span_count": segs,
                          "constant_width": all(isinstance(w, (int, float)) and math.isfinite(w)
                                                and abs(w - e.get("width", 0.0)) <= 1e-9
                                                for w in e.get("span_widths", []))})
    return {"circles": circles, "polylines": polys, "lines": lines}
