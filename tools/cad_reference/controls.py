"""Independently constructed control fixtures.

Fixtures are written in millimetres and converted to a 10 px/mm pixel frame
(a "254 dpi" case), so they go through the same registration path as the
archive cases and are judged at realistic (sub-millimetre) tolerances.

Each fixture is built from plain geometry (straight cubics with thirds-control
points, standard 4/3*tan(theta/4) cubic arcs), not from grader output. The
expected outcome of every control is stated by its builder and asserted by the
unit tests; validate.py also replays them through the ORIGINAL grader to record
its counterexample outcomes.
"""

import math

W = 0.5


def line(a, b, width=W):
    a, b = tuple(map(float, a)), tuple(map(float, b))
    p1 = (a[0] + (b[0] - a[0]) / 3, a[1] + (b[1] - a[1]) / 3)
    p2 = (a[0] + 2 * (b[0] - a[0]) / 3, a[1] + 2 * (b[1] - a[1]) / 3)
    return {"width": width, "closed": False, "segment_count": 1,
            "segments": [{"p0": list(a), "p1": list(p1), "p2": list(p2), "p3": list(b)}]}


def polyline(points, closed=False, width=W):
    pts = list(points) + ([points[0]] if closed else [])
    segs = [line(pts[i], pts[i + 1])["segments"][0] for i in range(len(pts) - 1)]
    return {"width": width, "closed": closed, "segment_count": len(segs), "segments": segs}


def arc(center, r, start_deg, sweep_deg, width=W, closed=False):
    n = max(1, int(math.ceil(abs(sweep_deg) / 90.0 - 1e-9)))
    d = math.radians(sweep_deg) / n
    k = 4.0 / 3.0 * math.tan(d / 4.0)
    segs = []
    for i in range(n):
        a0 = math.radians(start_deg) + i * d
        a1 = a0 + d
        p0 = (center[0] + r * math.cos(a0), center[1] + r * math.sin(a0))
        p3 = (center[0] + r * math.cos(a1), center[1] + r * math.sin(a1))
        p1 = (p0[0] - k * r * math.sin(a0), p0[1] + k * r * math.cos(a0))
        p2 = (p3[0] + k * r * math.sin(a1), p3[1] - k * r * math.cos(a1))
        segs.append({"p0": list(p0), "p1": list(p1), "p2": list(p2), "p3": list(p3)})
    return {"width": width, "closed": closed, "segment_count": n, "segments": segs}


PX = 10.0  # px per mm


def _scale(stroke):
    out = dict(stroke)
    out["width"] = stroke["width"] * PX
    out["segments"] = [{k: [v[0] * PX, v[1] * PX] for k, v in sg.items()} for sg in stroke["segments"]]
    return out


def geo(strokes, regions=None, w=100, h=100):
    regs = []
    for r in regions or []:
        regs.append({**r, "loops": [{**lp, "segments": [{k: [v[0] * PX, v[1] * PX] for k, v in sg.items()}
                                                        for sg in lp["segments"]]} for lp in r["loops"]]})
    return {"width": int(w * PX), "height": int(h * PX), "strokes": [_scale(s) for s in strokes], "regions": regs}


def region_rect(x0, y0, x1, y1):
    s = polyline([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], closed=True)
    return {"palette_index": 1, "loops": [{"segment_count": 4, "segments": s["segments"]}]}


def truth(entities, relationships=None):
    return {"scene_id": "CTL", "split": "control", "entities": entities, "relationships": relationships or []}


def t_line(i, a, b, width=W, role="main_structure"):
    return {"id": i, "kind": "line", "role": role, "width_mm": width, "a": list(a), "b": list(b)}


def t_poly(i, points, closed=True, width=W):
    pts = list(points) + ([points[0]] if closed else [])
    return {"id": i, "kind": "polyline", "role": "main_structure", "width_mm": width, "closed": closed,
            "segments": [{"type": "line", "a": list(pts[k]), "b": list(pts[k + 1])} for k in range(len(pts) - 1)]}


def t_circle(i, c, r, width=W):
    return {"id": i, "kind": "circle", "role": "hole", "width_mm": width, "center": list(c), "r_mm": r}


def t_arc(i, c, r, start, sweep, width=W):
    return {"id": i, "kind": "arc", "role": "detail", "width_mm": width, "center": list(c), "r_mm": r,
            "start_deg": start, "end_deg": (start + sweep) % 360.0, "sweep_deg": sweep}


CASE = {"case_id": "control", "dpi": 254.0, "variant": "control", "page_mm": [100.0, 100.0],
        "pixel_size": [1000, 1000], "mm_per_px": 0.1, "transform": "px = mm * dpi / 25.4"}

SQUARE = [(10, 10), (30, 10), (30, 30), (10, 30)]
SLOT_LEFT = t_arc("arc", (50, 50), 10, 270.0, -180.0)  # drawn through the LEFT side (x < 50)


def controls():
    """name -> (truth, geometry, expectation dict)."""
    c = {}
    c["line_identity"] = (truth([t_line("L", (10, 10), (40, 10))]), geo([line((10, 10), (40, 10))]),
                          {"line_two_anchor": 1, "line_geometry_presence": 1})
    c["line_reversed"] = (truth([t_line("L", (10, 10), (40, 10))]), geo([line((40, 10), (10, 10))]),
                          {"line_two_anchor": 1})
    c["square_as_four_lines"] = (truth([t_poly("P", SQUARE)]),
                                 geo([line(SQUARE[i], SQUARE[(i + 1) % 4]) for i in range(4)]),
                                 {"line_two_anchor": 4, "line_geometry_presence": 4})
    rot = SQUARE[2:] + SQUARE[:2]
    c["square_as_joined_cyclic_polyline"] = (truth([t_poly("P", SQUARE)]), geo([polyline(rot, closed=True)]),
                                             {"line_two_anchor": 0, "line_geometry_presence": 4,
                                              "geometry_precision": 1})
    c["l_polyline_vs_two_lines"] = (truth([t_line("A", (10, 10), (40, 10)), t_line("B", (40, 10), (40, 40))]),
                                    geo([polyline([(10, 10), (40, 10), (40, 40)])]),
                                    {"line_two_anchor": 0, "line_geometry_presence": 2, "geometry_precision": 1})
    c["arc_correct_side_reverse_drawn"] = (truth([SLOT_LEFT]), geo([arc((50, 50), 10, 90.0, 180.0)]),
                                           {"arc_complete": 1})
    c["arc_wrong_side"] = (truth([SLOT_LEFT]), geo([arc((50, 50), 10, 270.0, 180.0)]), {"arc_complete": 0})
    q = t_arc("q", (50, 50), 10, 0.0, -90.0)
    c["arc_negative_quarter_correct"] = (truth([q]), geo([arc((50, 50), 10, 0.0, -90.0)]), {"arc_complete": 1})
    c["arc_negative_quarter_opposite"] = (truth([q]), geo([arc((50, 50), 10, 0.0, 90.0)]), {"arc_complete": 0})
    hole = t_circle("H", (50, 50), 8)
    c["circle_complete"] = (truth([hole]), geo([arc((50, 50), 8, 0.0, 360.0, closed=True)]),
                            {"circle_complete": 1, "geometry_precision": 1})
    c["circle_partial_270"] = (truth([hole]), geo([arc((50, 50), 8, 0.0, 270.0)]), {"circle_complete": 0})
    c["circle_missing"] = (truth([hole]), geo([]), {"circle_complete": 0})
    c["circle_extra"] = (truth([hole]), geo([arc((50, 50), 8, 0.0, 360.0, closed=True),
                                             arc((20, 20), 5, 0.0, 360.0, closed=True)]),
                         {"circle_complete": 1, "geometry_precision": 0.5, "unsupported_curved": 1})
    tj = [{"type": "t_junction", "a": "stem", "b": "host", "point_mm": [30, 20]}]
    tt = [t_line("host", (10, 20), (50, 20)), t_line("stem", (30, 20), (30, 40))]
    c["t_junction_true_reversed"] = (truth(tt, tj), geo([line((50, 20), (10, 20)), line((30, 40), (30, 20))]),
                                     {"junction_topology": 1})
    c["t_junction_displaced_stem"] = (truth(tt, tj), geo([line((10, 20), (50, 20)), line((30, 23), (30, 40))]),
                                      {"junction_topology": 0})
    c["t_junction_broken_host"] = (truth(tt, tj), geo([line((10, 20), (28, 20)), line((32, 20), (50, 20)),
                                                       line((30, 20), (30, 40))]), {"junction_topology": 0})
    c["t_junction_missing_crossbar"] = (truth(tt, tj), geo([line((30, 20), (30, 40))]), {"junction_topology": 0})
    tg = [{"type": "tangent_join", "a": "stem", "b": "host", "point_mm": [30, 20]}]
    c["tangent_join_never_claimed"] = (truth(tt, tg), geo([line((10, 20), (50, 20)), line((30, 20), (30, 40))]),
                                       {"tangent_recovered": 0, "tangent_connected_presence": 1})
    par = [t_line("w1", (10, 20), (60, 20)), t_line("w2", (10, 23), (60, 23))]
    c["separate_parallel_walls"] = (truth(par), geo([line((10, 20), (60, 20)), line((10, 23), (60, 23))]),
                                    {"merge_flags": 0, "line_two_anchor": 2})
    perp = [t_line("h", (10, 20), (60, 20)), t_line("v", (60, 20), (60, 50))]
    c["perpendicular_lines"] = (truth(perp), geo([line((10, 20), (60, 20)), line((60, 20), (60, 50))]),
                                {"merge_flags": 0, "line_two_anchor": 2})
    gap = [t_line("g1", (10, 20), (30, 20)), t_line("g2", (36, 20), (60, 20))]
    c["true_overmerge_collinear_gap"] = (truth(gap), geo([line((10, 20), (60, 20))]), {"merge_flags": 1})
    close = [t_line("c1", (10, 20), (60, 20)), t_line("c2", (10, 20.3), (60, 20.3))]
    c["true_overmerge_close_parallel"] = (truth(close), geo([line((10, 20.15), (60, 20.15))]), {"merge_flags": 1})
    c["wrong_width_20x"] = (truth([t_line("L", (10, 10), (40, 10))]), geo([line((10, 10), (40, 10), width=10.0)]),
                            {"line_two_anchor": 1, "line_geometry_presence": 1, "line_width_correct": 0})
    over = line((10, 10), (40, 10))
    over["segments"][0]["p1"] = [1.0, 10.0]
    over["segments"][0]["p2"] = [49.0, 10.0]
    c["collinear_overshoot_cubic"] = (truth([t_line("L", (10, 10), (40, 10))]), geo([over]), {"line_two_anchor": 0})
    c["fill_only_vs_stroke"] = (truth([t_line("L", (10, 10), (40, 10))]),
                                geo([], regions=[region_rect(10, 9.75, 40, 10.25)]),
                                {"line_geometry_presence": 0, "form": "filled_silhouette_only"})
    return c
