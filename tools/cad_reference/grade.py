"""CAD reference grader v2 (standard library only).

Grades ONE already-recovered document against its truth. It never runs the
tracer and never feeds truth into conversion: inputs are files written before
grading. All geometry is compared in page millimetres (y-down) after a single
registration (see registration.py).

Separate dimensions, each with its own documented denominator (never merged
into one percentage):

  line_geometry_presence   truth lines whose finite extent is covered by
                           recovered STROKE centrelines        / truth lines
  line_two_anchor          truth lines matched one-to-one by a single-segment
                           finite straight stroke              / truth lines
  line_two_anchor_precision  matched two-anchor strokes        / two-anchor strokes
  line_width_correct       stroke-present truth lines whose covering stroke
                           width is within tolerance           / truth lines
  circle_complete          truth circles closed by ONE recovered stroke with
                           full angular coverage and fit       / truth circles
  circle_analytic_export   truth circles exported as a DXF CIRCLE or exact
                           closed bulge circle                 / truth circles
  arc_complete             truth arcs covered along their SIGNED sweep by one
                           stroke                              / truth arcs
  arc_analytic_export      truth arcs exported as a matching DXF bulge arc
                                                               / truth arcs
  junction_topology        declared T-junctions where both incident objects
                           are recovered and meet              / declared T-junctions
  geometry_precision       recovered strokes supported by truth geometry
                           / recovered strokes outside text/solid zones
"""

import math
import os

from . import dxf as dxfmod
from . import pngio
from .geometry import (ang_diff, angle_deg, arc_samples, chords_of, circle_fit, dist,
                       line_samples, point_chords_distance, point_seg_distance,
                       sample_segments, seg_is_finite_line)
from .registration import Registration

TOLERANCES_VERSION = "vecto.cad-ref.tolerances/3"
# Numeric bases remain frozen; v3 records the existing straightness cap and
# requires meaningful coverage for sweeps within the angular allowance.
TOLERANCES = {
    "version": TOLERANCES_VERSION,
    "endpoint_mm": {"base": 0.45, "per_mm_per_px": 1.0},
    "angle_deg": 2.0,
    "width_mm": {"base": 0.20, "per_mm_per_px": 0.3},
    "length_mm": {"base": 0.60, "per_mm_per_px": 1.0},
    "circle_center_mm": {"base": 0.45, "per_mm_per_px": 1.0},
    "circle_radius_mm": {"base": 0.45, "per_mm_per_px": 1.0},
    "junction_mm": {"base": 0.60, "per_mm_per_px": 1.0},
    "arc_cover_deg": 4.0,
    "small_arc_coverage": 0.9,
    "two_anchor_straightness_mm": {"cap": 0.02, "endpoint_divisor": 10.0},
    "circle_cover_deg": 4.0,
    "presence_coverage": 0.9,
    "partial_coverage": 0.5,
    "stroke_support": 0.9,
    "junction_window_factor": 3.0,
}

NOT_SCORED = [
    "tangency (C1 continuity) itself: tangent joins report only that both objects are present and meet",
    "drafting-angle interpretation policy (15-degree grid +-1.5 / whole degrees) is not graded here",
    "arc length/curvature error beyond the point-wise arc distance",
    "hatch pitch / hatch angle fidelity",
    "text OCR / transcription and glyph legibility (strokes inside text boxes are excluded, not scored)",
    "solid-mark fill fidelity beyond the supporting image measures",
    "3D / solid model semantics",
]


def tol(spec, mm_per_px):
    return spec["base"] + spec["per_mm_per_px"] * mm_per_px


def tolerances_for(mm_per_px):
    T = TOLERANCES
    return {
        "endpoint": tol(T["endpoint_mm"], mm_per_px),
        "angle": T["angle_deg"],
        "width": tol(T["width_mm"], mm_per_px),
        "length": tol(T["length_mm"], mm_per_px),
        "circle_center": tol(T["circle_center_mm"], mm_per_px),
        "circle_radius": tol(T["circle_radius_mm"], mm_per_px),
        "junction": tol(T["junction_mm"], mm_per_px),
    }


# ----------------------------------------------------------------------------- truth


def truth_items(truth):
    lines, circles, arcs, zones = [], [], [], []
    for e in truth["entities"]:
        role = e.get("role", "")
        k = e["kind"]
        if k == "line":
            lines.append({"id": e["id"], "source": e["id"], "role": role, "width_mm": e["width_mm"],
                          "a": tuple(e["a"]), "b": tuple(e["b"])})
        elif k == "polyline":
            for i, s in enumerate(e["segments"]):
                sid = f"{e['id']}#{i}"
                if s["type"] == "line":
                    lines.append({"id": sid, "source": e["id"], "role": role, "width_mm": e["width_mm"],
                                  "a": tuple(s["a"]), "b": tuple(s["b"])})
                elif s["type"] == "arc":
                    arcs.append(_arc(sid, e["id"], role, e["width_mm"], s["center"], s["r"], s))
        elif k == "circle":
            circles.append({"id": e["id"], "role": role, "width_mm": e["width_mm"],
                            "center": tuple(e["center"]), "r_mm": e["r_mm"]})
        elif k == "arc":
            arcs.append(_arc(e["id"], e["id"], role, e["width_mm"], e["center"], e["r_mm"], e))
        elif k == "solid" and "rect_mm" in e:
            x, y, w, h = e["rect_mm"]
            zones.append({"id": e["id"], "kind": "solid", "box_mm": (x, y, x + w, y + h)})
    return lines, circles, arcs, zones


def _arc(aid, source, role, width, center, r, s):
    if "sweep_deg" in s:
        sweep, inferred = float(s["sweep_deg"]), False
    else:
        sweep, inferred = (s["end_deg"] - s["start_deg"]) % 360.0 or 360.0, True
    if not math.isfinite(sweep) or not 0 < abs(sweep) <= 360:
        raise ValueError(f"arc {aid}: sweep must be finite, nonzero and at most 360 degrees")
    return {"id": aid, "source": source, "role": role, "width_mm": width, "center": tuple(center),
            "r_mm": r, "start_deg": float(s["start_deg"]), "sweep_deg": sweep, "sweep_inferred": inferred}


# ----------------------------------------------------------------------------- helpers


def _bbox(points, pad=0.0):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return (min(xs) - pad, min(ys) - pad, max(xs) + pad, max(ys) + pad)


def _overlap(b1, b2):
    return not (b1[2] < b2[0] or b2[2] < b1[0] or b1[3] < b2[1] or b2[3] < b1[1])


def coverage(samples, chord_sets, t, bbox=None):
    """Fraction of `samples` within t of the union of chord sets [(bbox, chords)]."""
    if not samples:
        return 0.0
    sets = [cs for cs in chord_sets if bbox is None or _overlap(cs[0], bbox)]
    if not sets:
        return 0.0
    hit = 0
    for p in samples:
        for bb, ch in sets:
            if bb[0] - t <= p[0] <= bb[2] + t and bb[1] - t <= p[1] <= bb[3] + t:
                if point_chords_distance(p, ch) <= t:
                    hit += 1
                    break
    return hit / len(samples)


def bipartite(cands):
    adj = {}
    for t, c, _ in sorted(cands, key=lambda x: x[2]):
        adj.setdefault(t, []).append(c)
    owner = {}

    def go(t, seen):
        for c in adj.get(t, []):
            if c in seen:
                continue
            seen.add(c)
            if c not in owner or go(owner[c], seen):
                owner[c] = t
                return True
        return False

    for t in sorted(adj):
        go(t, set())
    return {t: c for c, t in owner.items()}


def _ratio(n, d):
    return {"numerator": n, "denominator": d, "value": (round(n / d, 4) if d else None)}


# ----------------------------------------------------------------------------- scoring


def build_strokes(geo, reg, n=16):
    strokes = []
    for i, s in enumerate(geo.get("strokes", [])):
        segs = [reg.seg_to_mm(seg) for seg in s["segments"]]
        if not segs:
            continue
        pts = sample_segments(segs, n)
        strokes.append({"index": i, "segments": segs, "points": pts, "chords": chords_of(pts),
                        "bbox": _bbox(pts), "width_mm": reg.len_mm(s.get("width", 0.0)),
                        "closed": bool(s.get("closed")), "seg_count": len(segs)})
    loops = []
    for r in geo.get("regions", []):
        for lp in r.get("loops", []):
            if not lp.get("segments"):
                continue
            pts = sample_segments([reg.seg_to_mm(sg) for sg in lp["segments"]], 8)
            loops.append({"chords": chords_of(pts), "bbox": _bbox(pts)})
    return strokes, loops


def score_case(truth, geo, case, reg=None, dxf_text=None, input_png=None, recovered_png=None):
    reg = reg or Registration.from_case(case)
    if "pixel_size" in case and [geo.get("width"), geo.get("height")] != list(case["pixel_size"]):
        raise ValueError(f"geometry frame {geo.get('width')}x{geo.get('height')} != case pixel_size {case['pixel_size']}")
    T = tolerances_for(reg.mm_per_px)
    t_lines, t_circles, t_arcs, zones = truth_items(truth)
    for chk in case.get("text_render_check", []):
        x0, y0, x1, y1 = chk["box_px"]
        a, b = reg.to_mm((x0, y0)), reg.to_mm((x1, y1))
        zones.append({"id": chk["id"], "kind": "text", "box_mm": (a[0], a[1], b[0], b[1])})
    strokes, loops = build_strokes(geo, reg)
    sets = [(s["bbox"], s["chords"]) for s in strokes]
    fill_sets = [(lp["bbox"], lp["chords"]) for lp in loops]
    ep = T["endpoint"]
    PC, PP = TOLERANCES["presence_coverage"], TOLERANCES["partial_coverage"]

    # ---- straight lines ----------------------------------------------------
    two = []  # single-segment finite straight strokes
    for s in strokes:
        if s["seg_count"] == 1 and seg_is_finite_line(s["segments"][0], min(TOLERANCES["two_anchor_straightness_mm"]["cap"],
                                                                       ep / TOLERANCES["two_anchor_straightness_mm"]["endpoint_divisor"])):
            two.append(s)
    cands = []
    for ti, tl in enumerate(t_lines):
        for ci, s in enumerate(two):
            c = _line_cost(tl, s, T)
            if c is not None:
                cands.append((ti, ci, c))
    match = bipartite(cands)
    used = set(match.values())

    rows = []
    for ti, tl in enumerate(t_lines):
        smp = line_samples(tl["a"], tl["b"])
        bb = _bbox(smp, ep)
        per = [(coverage(smp, [(s["bbox"], s["chords"])], ep, bb), s) for s in strokes if _overlap(s["bbox"], bb)]
        best_cov, best = max(per, key=lambda x: x[0], default=(0.0, None))
        union = coverage(smp, sets, ep, bb)
        row = {"id": tl["id"], "role": tl["role"], "width_mm": tl["width_mm"],
               "a": list(tl["a"]), "b": list(tl["b"]), "stroke_coverage": round(union, 4),
               "best_single_stroke_coverage": round(best_cov, 4)}
        if ti in match:
            s = two[match[ti]]
            seg = s["segments"][0]
            p, q = seg["p0"], seg["p3"]
            e1, e2 = _end_errs(tl["a"], tl["b"], p, q)
            row.update({"form": "two_anchor", "two_anchor": True, "geometry_present": True,
                        "stroke_index": s["index"], "endpoint_error_mm": round(max(e1, e2), 4),
                        "angle_error_deg": round(ang_diff(angle_deg(tl["a"], tl["b"]), angle_deg(p, q)), 4),
                        "length_error_mm": round(abs(dist(p, q) - dist(tl["a"], tl["b"])), 4)})
            wstroke = s
        else:
            row["two_anchor"] = False
            wstroke = best
            if union >= PC:
                row.update({"form": "present_not_two_anchor", "geometry_present": True,
                            "covering_stroke_segments": best["seg_count"] if best else None})
            else:
                row["geometry_present"] = False
                fc = coverage(smp, fill_sets, ep, bb)
                row["fill_coverage"] = round(fc, 4)
                if fc >= PC:
                    row["form"] = "filled_silhouette_only"
                elif union >= PP:
                    row["form"] = "partial"
                else:
                    row["form"] = "missing"
                wstroke = None
        if wstroke is not None:
            w = wstroke["width_mm"]
            row["recovered_width_mm"] = round(w, 4)
            row["width_error_mm"] = round(abs(w - tl["width_mm"]), 4)
            row["width_correct"] = abs(w - tl["width_mm"]) <= T["width"]
        else:
            row["width_correct"] = False
        rows.append(row)

    dup = sum(max(0, len({c for t, c, _ in cands if t == ti}) - 1) for ti in range(len(t_lines)))

    # ---- merges: one straight recovered piece spanning distinct truth boundaries
    merges = []
    for s in strokes:
        for k, seg in enumerate(s["segments"]):
            if not seg_is_finite_line(seg, ep / 4):
                continue
            smp = line_samples(seg["p0"], seg["p3"], 40)
            hosts = []
            for tl in t_lines:
                on = [p for p in smp if point_seg_distance(p, tl["a"], tl["b"]) <= ep]
                if len(on) >= 2:
                    span = max(dist(on[0], x) for x in on)
                    if span >= 2 * ep:
                        hosts.append(tl)
            srcs = {h["id"] for h in hosts}
            if len(srcs) >= 2 and not _all_adjacent(hosts, ep):
                merges.append({"stroke_index": s["index"], "segment": k, "truth_ids": sorted(srcs),
                               "note": "one straight recovered piece runs along two distinct finite truth boundaries"})

    # ---- circles ----------------------------------------------------------
    dx = None
    if dxf_text:
        dx = dxfmod.to_mm(dxfmod.parse_entities(dxf_text), geo["height"], reg)
    cc = TOLERANCES["circle_cover_deg"]
    circle_rows, used_c = [], set()
    for tc in t_circles:
        smp = arc_samples(tc["center"], tc["r_mm"], 0.0, 360.0, 72)[:-1]
        bb = _bbox(smp, ep)
        best = None
        for s in strokes:
            if s["index"] in used_c or not _overlap(s["bbox"], bb):
                continue
            cov = coverage(smp, [(s["bbox"], s["chords"])], T["circle_radius"], bb)
            on = sum(1 for p in s["points"] if abs(dist(p, tc["center"]) - tc["r_mm"]) <= T["circle_radius"]) / len(s["points"])
            fit = circle_fit(s["points"])
            closed = s["closed"] or dist(s["points"][0], s["points"][-1]) <= ep
            ok = (cov >= 1 - cc / 360.0 and on >= TOLERANCES["stroke_support"] and closed and fit is not None
                  and dist(fit[:2], tc["center"]) <= T["circle_center"] and abs(fit[2] - tc["r_mm"]) <= T["circle_radius"])
            key = (ok, cov, on)
            if best is None or key > best[0]:
                best = (key, s, cov, on, fit, closed)
        union = coverage(smp, sets, T["circle_radius"], bb)
        row = {"id": tc["id"], "role": tc["role"], "center": list(tc["center"]), "r_mm": tc["r_mm"],
               "union_angular_coverage": round(union, 4), "complete": False}
        if best is not None:
            (ok, cov, on), s, cov, on, fit, closed = best
            row.update({"best_stroke_index": s["index"], "best_stroke_angular_coverage": round(cov, 4),
                        "best_stroke_on_circle_share": round(on, 4), "best_stroke_closed": closed})
            if fit:
                row.update({"center_error_mm": round(dist(fit[:2], tc["center"]), 4),
                            "radius_error_mm": round(abs(fit[2] - tc["r_mm"]), 4)})
            if ok:
                used_c.add(s["index"])
                row["complete"] = True
                row["width_error_mm"] = round(abs(s["width_mm"] - tc["width_mm"]), 4)
                row["width_correct"] = abs(s["width_mm"] - tc["width_mm"]) <= T["width"]
        if not row["complete"]:
            fc = coverage(smp, fill_sets, T["circle_radius"], bb)
            row["fill_coverage"] = round(fc, 4)
            row["form"] = ("filled_silhouette_only" if fc >= PC and union < PC else
                           "partial" if union >= PP else "missing")
        else:
            row["form"] = "complete_stroke"
        row["export"] = _circle_export(tc, dx, T)
        circle_rows.append(row)
    extra_dxf_circles = []
    if dx:
        for c in dx["circles"] + [p["exact_circle"] for p in dx["polylines"] if p["exact_circle"]]:
            if not any(dist(c["center_mm"], tc["center"]) <= T["circle_center"]
                       and abs(c["r_mm"] - tc["r_mm"]) <= T["circle_radius"] for tc in t_circles):
                extra_dxf_circles.append({"center_mm": [round(v, 3) for v in c["center_mm"]], "r_mm": round(c["r_mm"], 3)})

    # ---- arcs (signed sweep, drawn side) -----------------------------------
    ac = TOLERANCES["arc_cover_deg"]
    arc_rows = []
    for ta in t_arcs:
        span = abs(ta["sweep_deg"])
        smp = arc_samples(ta["center"], ta["r_mm"], ta["start_deg"], ta["sweep_deg"], 72)
        opp = arc_samples(ta["center"], ta["r_mm"], ta["start_deg"] + ta["sweep_deg"],
                          (360.0 - span) * (1 if ta["sweep_deg"] > 0 else -1), 72)[1:-1]
        bb = _bbox(smp, ep)
        best = (0.0, None)
        for s in strokes:
            if not _overlap(s["bbox"], bb):
                continue
            cov = coverage(smp, [(s["bbox"], s["chords"])], T["circle_radius"], bb)
            if cov > best[0]:
                best = (cov, s)
        need = TOLERANCES["small_arc_coverage"] if span <= ac else 1 - ac / span
        union = coverage(smp, sets, T["circle_radius"], bb)
        nearby_chords = [c for st in strokes if _overlap(st["bbox"], bb) for c in st["chords"]]
        worst = max(point_chords_distance(p, nearby_chords) for p in smp) if nearby_chords else None
        row = {"id": ta["id"], "role": ta["role"], "center": list(ta["center"]), "r_mm": ta["r_mm"],
               "start_deg": ta["start_deg"], "sweep_deg": ta["sweep_deg"], "sweep_inferred": ta["sweep_inferred"],
               "best_stroke_sweep_coverage": round(best[0], 4), "union_sweep_coverage": round(union, 4),
               "required_coverage": round(need, 4), "complete": best[1] is not None and best[0] >= need,
               "max_deviation_from_truth_arc_mm": round(worst, 4) if worst is not None else None}
        if best[1] is not None:
            row["best_stroke_index"] = best[1]["index"]
            row["best_stroke_opposite_side_coverage"] = round(
                coverage(opp, [(best[1]["bbox"], best[1]["chords"])], T["circle_radius"]), 4)
        row["export"] = _arc_export(ta, dx, T)
        arc_rows.append(row)

    # ---- junctions ----------------------------------------------------------
    by_source = {}
    for tl in t_lines:
        by_source.setdefault(tl["source"], []).append(tl)
    arcs_by_source = {}
    for ta in t_arcs:
        arcs_by_source.setdefault(ta["source"], []).append(ta)
    conn = []
    for rel in truth.get("relationships", []):
        if rel.get("type") not in ("t_junction", "tangent_join") or not rel.get("point_mm"):
            continue
        conn.append(_junction(rel, by_source, arcs_by_source, sets, T))

    # ---- precision over recovered strokes -----------------------------------
    truth_line_sets = [(tl["a"], tl["b"]) for tl in t_lines]
    truth_arc_chords = [chords_of(arc_samples(ta["center"], ta["r_mm"], ta["start_deg"], ta["sweep_deg"], 72))
                        for ta in t_arcs]
    supported, excluded, unsupported = 0, 0, []
    for s in strokes:
        if _in_zones(s["points"], zones, ep) >= 0.5:
            excluded += 1
            continue
        on = 0
        for p in s["points"]:
            if (any(point_seg_distance(p, a, b) <= ep for a, b in truth_line_sets)
                    or any(abs(dist(p, tc["center"]) - tc["r_mm"]) <= T["circle_radius"] for tc in t_circles)
                    or any(point_chords_distance(p, ch) <= T["circle_radius"] for ch in truth_arc_chords)):
                on += 1
        share = on / len(s["points"])
        if share >= TOLERANCES["stroke_support"]:
            supported += 1
            continue
        fit = circle_fit(s["points"])
        curved = (s["seg_count"] > 0 and fit is not None and fit[3] <= T["circle_radius"]
                  and fit[2] < 1e4 and not (s["seg_count"] == 1 and seg_is_finite_line(s["segments"][0], ep / 4)))
        kind = ("unsupported_closed_curve" if curved and (s["closed"] or dist(s["points"][0], s["points"][-1]) <= ep)
                else "unsupported_curve" if curved
                else "unsupported_straight" if s["seg_count"] == 1 and seg_is_finite_line(s["segments"][0], ep / 4)
                else "unsupported_other")
        unsupported.append({"stroke_index": s["index"], "kind": kind, "support_share": round(share, 4),
                            "segments": s["seg_count"], "bbox_mm": [round(v, 3) for v in s["bbox"]]})
    graded = len(strokes) - excluded

    image = {"measured": False}
    if input_png and recovered_png and os.path.exists(input_png) and os.path.exists(recovered_png):
        image = image_measures(input_png, recovered_png)

    nL, nC, nA = len(t_lines), len(t_circles), len(t_arcs)
    tj = [c for c in conn if c["type"] == "t_junction"]
    forms = {}
    for r in rows:
        forms[r["form"]] = forms.get(r["form"], 0) + 1
    metrics = {
        "line_geometry_presence": _ratio(sum(r["geometry_present"] for r in rows), nL),
        "line_two_anchor": _ratio(len(match), nL),
        "line_two_anchor_precision": _ratio(len(match), len(two)),
        "line_width_correct": _ratio(sum(1 for r in rows if r["geometry_present"] and r["width_correct"]), nL),
        "circle_complete": _ratio(sum(r["complete"] for r in circle_rows), nC),
        "circle_analytic_export": _ratio(sum(r["export"] in ("dxf_circle", "dxf_exact_bulge_circle")
                                             for r in circle_rows), nC),
        "arc_complete": _ratio(sum(r["complete"] for r in arc_rows), nA),
        "arc_analytic_export": _ratio(sum(r["export"] == "dxf_bulge_arc" for r in arc_rows), nA),
        "junction_topology": _ratio(sum(c["recovered"] for c in tj), len(tj)),
        "geometry_precision": _ratio(supported, graded),
    }
    return {
        "schema": "vecto.cad-ref.grade/2",
        "case_id": case.get("case_id"), "scene_id": truth.get("scene_id"), "split": truth.get("split"),
        "dpi": case.get("dpi"), "variant": case.get("variant"),
        "registration": {"method": reg.method, "mm_per_px": reg.mm_per_px, **reg.record},
        "tolerances_version": TOLERANCES_VERSION,
        "tolerances_mm": {k: round(v, 5) for k, v in T.items()},
        "metrics": metrics,
        "counts": {"truth_lines": nL, "truth_circles": nC, "truth_arcs": nA,
                   "recovered_strokes": len(strokes), "two_anchor_strokes": len(two),
                   "strokes_excluded_text_or_solid": excluded, "filled_loops": len(loops),
                   "line_forms": forms, "duplicate_line_candidates": dup,
                   "merge_flags": len(merges), "unsupported_strokes": len(unsupported),
                   "unsupported_curved_strokes": sum(1 for u in unsupported if "curve" in u["kind"]),
                   "unmatched_dxf_circles": len(extra_dxf_circles)},
        "lines": rows, "circles": circle_rows, "arcs": arc_rows, "connections": conn,
        "merges": merges, "unsupported_strokes": unsupported, "unmatched_dxf_circles": extra_dxf_circles,
        "image": image, "not_scored": NOT_SCORED,
    }


def _end_errs(a, b, p, q):
    if dist(a, p) + dist(b, q) <= dist(a, q) + dist(b, p):
        return dist(a, p), dist(b, q)
    return dist(a, q), dist(b, p)


def _line_cost(tl, s, T):
    seg = s["segments"][0]
    p, q = seg["p0"], seg["p3"]
    e1, e2 = _end_errs(tl["a"], tl["b"], p, q)
    if max(e1, e2) > T["endpoint"]:
        return None
    # the DRAWN curve (incl. any control-point overshoot) must stay on the finite truth segment
    if any(point_seg_distance(x, tl["a"], tl["b"]) > T["endpoint"] for x in s["points"]):
        return None
    if ang_diff(angle_deg(tl["a"], tl["b"]), angle_deg(p, q)) > T["angle"]:
        return None
    if abs(dist(p, q) - dist(tl["a"], tl["b"])) > T["length"]:
        return None
    return e1 + e2


def _all_adjacent(hosts, ep):
    """Collinear consecutive edges sharing a vertex are one boundary, not a merge."""
    for i in range(len(hosts)):
        for j in range(i + 1, len(hosts)):
            h, g = hosts[i], hosts[j]
            share = min(dist(x, y) for x in (h["a"], h["b"]) for y in (g["a"], g["b"])) <= 1e-6
            colin = ang_diff(angle_deg(h["a"], h["b"]), angle_deg(g["a"], g["b"])) <= 0.5
            if not (share and colin):
                return False
    return True


def _circle_export(tc, dx, T):
    if dx is None:
        return "no_dxf"
    def m(c):
        return dist(c["center_mm"], tc["center"]) <= T["circle_center"] and abs(c["r_mm"] - tc["r_mm"]) <= T["circle_radius"]
    if any(m(c) for c in dx["circles"]):
        return "dxf_circle"
    if any(p["exact_circle"] and m(p["exact_circle"]) for p in dx["polylines"]):
        return "dxf_exact_bulge_circle"
    for p in dx["polylines"]:
        if p["closed"] and len(p["points_mm"]) >= 6:
            f = circle_fit(p["points_mm"])
            if f and f[3] <= T["circle_radius"] and dist(f[:2], tc["center"]) <= T["circle_center"] \
                    and abs(f[2] - tc["r_mm"]) <= T["circle_radius"]:
                return "dxf_polygon_approximation"
    return "not_exported_as_circle"


def _arc_export(ta, dx, T):
    if dx is None:
        return "no_dxf"
    # Match one entire finite open analytic object, never a union of objects or
    # one conveniently matching segment of a larger polyline.
    expected = arc_samples(ta["center"], ta["r_mm"], ta["start_deg"], ta["sweep_deg"], 1)
    for p in dx["polylines"]:
        try:
            arcs = p["arcs"]
            if (p["closed"] is not False or not arcs or p["straight_segments"] != 0
                    or p["span_count"] != len(arcs) or not p["constant_width"]):
                continue
            values = [p["width_mm"]]
            for a in arcs:
                values.extend((*a["center_mm"], a["r_mm"], a["page_sweep_deg"],
                               *a["start_mm"], *a["end_mm"]))
            if not all(math.isfinite(v) for v in values):
                continue
            if p["width_mm"] <= 0 or abs(p["width_mm"] - ta["width_mm"]) > T["width"]:
                continue
            c0, r0 = arcs[0]["center_mm"], arcs[0]["r_mm"]
            # Co-circularity is a representation invariant, not a raster fit.
            eps = 1e-6 * max(1.0, r0)
            if any(a["r_mm"] <= 0 or dist(a["center_mm"], c0) > eps
                   or abs(a["r_mm"] - r0) > eps
                   or not 0 < abs(a["page_sweep_deg"]) < 360
                   or (a["page_sweep_deg"] > 0) != (ta["sweep_deg"] > 0)
                   for a in arcs):
                continue
            if any(dist(a["end_mm"], b["start_mm"]) > eps for a, b in zip(arcs, arcs[1:])):
                continue
            sweep = sum(a["page_sweep_deg"] for a in arcs)
            if (not math.isfinite(sweep) or abs(sweep) >= 360 - 1e-7
                    or abs(sweep - ta["sweep_deg"]) > TOLERANCES["arc_cover_deg"]
                    or abs(sweep) < TOLERANCES["small_arc_coverage"] * abs(ta["sweep_deg"])):
                continue
            if (dist(c0, ta["center"]) <= T["circle_center"]
                    and abs(r0 - ta["r_mm"]) <= T["circle_radius"]
                    and dist(arcs[0]["start_mm"], expected[0]) <= T["endpoint"]
                    and dist(arcs[-1]["end_mm"], expected[-1]) <= T["endpoint"]):
                return "dxf_bulge_arc"
        except (KeyError, TypeError, ValueError, IndexError, OverflowError):
            # Missing additive fields in old JSON are not analytic evidence.
            continue
    return "not_exported_as_arc"


def _in_zones(points, zones, pad):
    if not zones:
        return 0.0
    n = 0
    for p in points:
        for z in zones:
            x0, y0, x1, y1 = z["box_mm"]
            if min(x0, x1) - pad <= p[0] <= max(x0, x1) + pad and min(y0, y1) - pad <= p[1] <= max(y0, y1) + pad:
                n += 1
                break
    return n / len(points)


def _nearest_piece(source, pt, by_source, arcs_by_source):
    best = None
    for tl in by_source.get(source, []):
        d = point_seg_distance(pt, tl["a"], tl["b"])
        if best is None or d < best[0]:
            best = (d, "line", tl)
    for ta in arcs_by_source.get(source, []):
        d = point_chords_distance(pt, chords_of(arc_samples(ta["center"], ta["r_mm"], ta["start_deg"], ta["sweep_deg"], 72)))
        if best is None or d < best[0]:
            best = (d, "arc", ta)
    return best


def _piece_samples(kind, piece, step=0.05):
    if kind == "line":
        n = max(2, int(dist(piece["a"], piece["b"]) / step))
        return line_samples(piece["a"], piece["b"], n)
    n = max(8, int(abs(math.radians(piece["sweep_deg"])) * piece["r_mm"] / step))
    return arc_samples(piece["center"], piece["r_mm"], piece["start_deg"], piece["sweep_deg"], n)


def _junction(rel, by_source, arcs_by_source, sets, T):
    pt = tuple(rel["point_mm"])
    t, w = T["endpoint"], TOLERANCES["junction_window_factor"] * T["junction"]
    out = {"type": rel["type"], "a": rel["a"], "b": rel["b"], "point_mm": list(pt), "recovered": False}
    pa = _nearest_piece(rel["a"], pt, by_source, arcs_by_source)
    pb = _nearest_piece(rel["b"], pt, by_source, arcs_by_source)
    if pa is None or pb is None:
        out["note"] = "declared object not present in truth as line/arc geometry"
        return out
    sa, sb = _piece_samples(pa[1], pa[2]), _piece_samples(pb[1], pb[2])
    pres_a = coverage(sa[::max(1, len(sa) // 40)], sets, t)
    pres_b = coverage(sb[::max(1, len(sb) // 40)], sets, t)
    host_win = [p for p in sb if dist(p, pt) <= w]
    host_local = coverage(host_win, sets, t)
    # stem window: near the junction, but off the host (so host ink cannot stand in for it)
    hb = chords_of(sb)
    stem_win = [p for p in sa if dist(p, pt) <= w and point_chords_distance(p, hb) > t]
    stem_local = coverage(stem_win, sets, t) if stem_win else None
    out.update({"object_a_presence": round(pres_a, 4), "object_b_presence": round(pres_b, 4),
                "host_local_coverage": round(host_local, 4),
                "stem_local_coverage": None if stem_local is None else round(stem_local, 4),
                "window_mm": round(w, 4)})
    PC = TOLERANCES["presence_coverage"]
    both = pres_a >= PC and pres_b >= PC
    meet = host_local >= PC and (stem_local is None or stem_local >= PC)
    if rel["type"] == "t_junction":
        out["recovered"] = both and meet
    else:
        out["recovered"] = False
        out["connected_presence"] = both and meet
        out["tangency"] = "not_scored"
    return out


# ----------------------------------------------------------------------------- image


def image_measures(input_png, recovered_png):
    """Supporting pixel agreement. read_ink_plane yields one BYTE (0xFF/0x00) per
    pixel, so bit popcounts are divided by 8 to give pixel counts (the original
    grader reported 8x the true pixel counts; its ratios were unaffected)."""
    w1, h1, r1, _ = pngio.read_ink_plane(input_png)
    w2, h2, r2, _ = pngio.read_ink_plane(recovered_png)
    if (w1, h1) != (w2, h2):
        return {"measured": False, "error": f"size mismatch {w1}x{h1} vs {w2}x{h2}"}
    i1, i2 = int.from_bytes(r1, "big"), int.from_bytes(r2, "big")

    def px(v):
        return bin(v).count("1") // 8

    n_in, n_out = px(i1), px(i2)
    inter, union = px(i1 & i2), px(i1 | i2)
    return {"measured": True, "pixels_total": w1 * h1, "ink_pixels_input": n_in, "ink_pixels_recovered": n_out,
            "iou": round(inter / union, 4) if union else None,
            "input_ink_lost_share": round((n_in - inter) / max(1, n_in), 4),
            "newly_painted_share": round((n_out - inter) / max(1, n_out), 4),
            "note": "supporting evidence only; does not replace geometry metrics"}
