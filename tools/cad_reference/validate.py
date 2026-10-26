#!/usr/bin/env python3
"""Reproducible CAD-reference evaluation run.

    python3 tools/cad_reference/validate.py --archive <B> --out _bench/cad-evaluator-v2

1. runs the control suite (fails non-zero on any control failure);
2. verifies the archive manifest and source hashes (fails non-zero on mismatch
   or missing files);
3. re-grades the existing recovered outputs of every archive case. The tracer
   is NOT run; truth is read only here, after conversion happened;
4. writes per-case JSON, summary, manifest, tolerances, limitations and a
   standalone review.html with relative copies of actual input/output assets.
"""

import argparse
import hashlib
import html
import importlib.util
import io
import json
import os
import shutil
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from cad_reference import controls as C  # noqa: E402
from cad_reference.grade import NOT_SCORED, TOLERANCES, score_case  # noqa: E402
from cad_reference.registration import Registration  # noqa: E402

REVIEW_CASES = ["B_300dpi_clean", "C_300dpi_clean", "A_600dpi_clean"]
ASSETS = ["input.png", "recovered.png", "recovered-strokes.png", "recovered.svg", "recovered.dxf"]
METRICS = ["line_geometry_presence", "line_two_anchor", "line_two_anchor_precision", "line_width_correct",
           "circle_complete", "circle_analytic_export", "arc_complete", "arc_analytic_export",
           "junction_topology", "geometry_precision"]

LIMITATIONS = """# Limitations of the CAD reference evaluator v2

* Only THREE synthetic source families (A stepped shaft housing, B plate with holes
  and slot, C rounded chamber assembly), 3 DPIs x clean/degraded = 18 cases. No
  general CAD accuracy claim follows from these numbers. An unseen family is the
  next milestone.
* Metrics are separate dimensions with their own denominators; they are never
  combined. Geometry presence is not editability: a joined polyline can be fully
  present and still fail the independent two-anchor requirement.
* Exact-format contracts are only the DXF entities read here (CIRCLE, LINE,
  POLYLINE/VERTEX bulges, LWPOLYLINE). Cubic SVG paths are never treated as proof of
  analytic export.
* Not scored: {not_scored}
* Archived CairoSVG 2.9.1 registration uses the rounded viewport default
  xMidYMid meet matrix, checked against each reference SVG. Nominal DPI,
  textual transform and stored mm_per_px are retained as provenance only.
  Other renderers, versions or SVG viewport configurations are not verified and
  are rejected. Generic explicit nominal transforms are declarations only.
* Text and solid zones are excluded from stroke precision; glyph legibility is
  not judged by this tool.
* Tolerances are the frozen VEC-79 bases; v2 adds explicit coverage fractions; v3 records the existing straightness cap
  and requires 90% coverage for arcs with absolute sweep <=4 degrees.
  They were not tuned on the archive results.
"""


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for b in iter(lambda: fh.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def load(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def dump(path, obj):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(obj, fh, indent=2, ensure_ascii=False, allow_nan=False)
        fh.write("\n")


def run_controls():
    buf = io.StringIO()
    suite = unittest.defaultTestLoader.discover(os.path.join(HERE, "tests"), top_level_dir=os.path.join(HERE, "tests"))
    res = unittest.TextTestRunner(stream=buf, verbosity=1).run(suite)
    return {"tests_run": res.testsRun, "failures": len(res.failures), "errors": len(res.errors),
            "ok": res.wasSuccessful(), "output_tail": buf.getvalue()[-2000:]}


def original_counterexamples(archive):
    """Replay the controls through the ORIGINAL (read-only) archive grader."""
    path = os.path.join(archive, "tools", "grade.py")
    if not os.path.exists(path):
        return {"available": False}
    sys.path.insert(0, os.path.dirname(path))
    try:
        spec = importlib.util.spec_from_file_location("vec79_original_grade", path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
    finally:
        sys.path.pop(0)
    rows = {}
    for name, (truth, geo, exp) in C.controls().items():
        case = {"case_id": name, "dpi": C.CASE["dpi"], "variant": "control", "mm_per_px": 1.0 / C.PX}
        try:
            r = mod.score_case(truth, geo, case)
            orig = {"lines_two_anchor_matched": r["straight"]["matched"],
                    "geometric_presence": r["straight"]["geometric_presence"],
                    "merged_flags": len(r["straight"]["merged_boundary_flags"]),
                    "width_in_tolerance_rate": r["errors"]["width_in_tolerance_rate"],
                    "circles_recovered": sum(1 for c in r["circles"] if c["recovered"]),
                    "arcs_recovered": sum(1 for a in r["arcs"] if a["recovered"]),
                    "connections_recovered": sum(1 for c in r["connections"] if c["recovered"])}
        except Exception as exc:  # reported, never hidden
            orig = {"error": f"{type(exc).__name__}: {exc}"}
        new = score_case(truth, geo, C.CASE)
        rows[name] = {"expected": exp, "original": orig,
                      "v2": {k: new["metrics"][k]["numerator"] for k in METRICS if new["metrics"][k]["denominator"]}
                      | {"merge_flags": new["counts"]["merge_flags"]}}
    return {"available": True, "grader_sha256": sha(path), "controls": rows}


def check_archive(archive):
    errors, files = [], {}
    man_path = os.path.join(archive, "scenes", "manifest.json")
    idx_path = os.path.join(archive, "out", "cases_index.json")
    for p in (man_path, idx_path):
        if not os.path.exists(p):
            errors.append(f"missing {p}")
    if errors:
        return None, None, errors, files
    manifest, index = load(man_path), load(idx_path)
    scenes = {}
    for s in manifest["scenes"]:
        d = os.path.join(archive, s["dir"])
        for fn, key in (("reference.svg", "svg_sha256"), ("truth.json", "truth_sha256")):
            p = os.path.join(d, fn)
            if not os.path.exists(p):
                errors.append(f"missing {p}")
                continue
            h = sha(p)
            files[os.path.relpath(p, archive)] = h
            if h != s[key]:
                errors.append(f"hash mismatch {p}")
        scenes[s["scene_id"]] = s
    for c in index["cases"]:
        cd = os.path.join(archive, "out", "cases", c["case_id"])
        for fn in ("case.json", "input.png", "geometry.json", "recovered.dxf", "recovered.png"):
            p = os.path.join(cd, fn)
            if not os.path.exists(p):
                errors.append(f"missing {p}")
            else:
                files[os.path.relpath(p, archive)] = sha(p)
        cp = os.path.join(cd, "case.json")
        if os.path.exists(cp):
            case = load(cp)
            sc = scenes.get(case["scene_id"])
            if sc is None or case.get("reference_svg_sha256") != sc["svg_sha256"]:
                errors.append(f"{c['case_id']}: reference svg hash differs from scene manifest")
            ip = os.path.join(cd, "input.png")
            if os.path.exists(ip) and case.get("input_png_sha256") != files[os.path.relpath(ip, archive)]:
                errors.append(f"{c['case_id']}: input.png hash mismatch")
    return manifest, index, errors, files


def fmt(m):
    if m["denominator"] == 0:
        return "n/a"
    return f"{m['numerator']}/{m['denominator']}"


def write_html(out, results, controls_info, orig):
    esc = html.escape
    rows = []
    for r in results:
        cells = "".join(f"<td>{esc(fmt(r['metrics'][k]))}</td>" for k in METRICS)
        im = r["image"]
        rows.append(f"<tr><td>{esc(r['case_id'])}</td>{cells}<td>{r['counts']['merge_flags']}</td>"
                    f"<td>{r['counts']['unsupported_strokes']}</td>"
                    f"<td>{im.get('iou', '')}</td><td>{im.get('ink_pixels_input', '')}</td></tr>")
    head = "".join(f"<th>{esc(k)}</th>" for k in METRICS)
    figs = []
    for cid in REVIEW_CASES:
        r = next((x for x in results if x["case_id"] == cid), None)
        if r is None:
            continue
        det = []
        for c in r["circles"]:
            det.append(f"circle {esc(c['id'])}: {esc(c['form'])}, best-stroke angular coverage "
                       f"{c.get('best_stroke_angular_coverage')}, export {esc(c['export'])}")
        for a in r["arcs"]:
            det.append(f"arc {esc(a['id'])} sweep {a['sweep_deg']}: complete={a['complete']}, "
                       f"best-stroke sweep coverage {a['best_stroke_sweep_coverage']}, export {esc(a['export'])}")
        for j in r["connections"]:
            det.append(f"{esc(j['type'])} {esc(j['a'])}/{esc(j['b'])}: recovered={j['recovered']}"
                       + (f", connected_presence={j['connected_presence']}, tangency not scored"
                          if j["type"] == "tangent_join" else ""))
        for m in r["merges"]:
            det.append(f"merge flag: stroke {m['stroke_index']} spans {esc(', '.join(m['truth_ids']))}")
        det.append("line forms: " + esc(json.dumps(r["counts"]["line_forms"])))
        imgs = "".join(f"<figure><img src='assets/{cid}/{a}' alt='{a}'><figcaption>{a} (actual file)</figcaption></figure>"
                       for a in ("input.png", "recovered.png", "recovered-strokes.png")
                       if os.path.exists(os.path.join(out, "assets", cid, a)))
        links = " ".join(f"<a href='assets/{cid}/{a}'>{a}</a>" for a in ("recovered.svg", "recovered.dxf")
                         if os.path.exists(os.path.join(out, "assets", cid, a)))
        figs.append(f"<section><h2>{esc(cid)}</h2><div class='row'>{imgs}</div><p>Exports: {links} · "
                    f"<a href='cases/{cid}.json'>per-entity JSON</a></p><ul>"
                    + "".join(f"<li>{d}</li>" for d in det) + "</ul></section>")
    ctl = []
    for name, row in (orig.get("controls") or {}).items():
        ctl.append(f"<tr><td>{esc(name)}</td><td>{esc(json.dumps(row['expected']))}</td>"
                   f"<td>{esc(json.dumps(row['v2']))}</td><td>{esc(json.dumps(row['original']))}</td></tr>")
    doc = f"""<!doctype html><html><head><meta charset="utf-8"><title>CAD reference evaluator v2 review</title>
<style>body{{font:14px system-ui,sans-serif;margin:20px;max-width:1500px}}table{{border-collapse:collapse;font-size:12px}}
td,th{{border:1px solid #bbb;padding:3px 6px;text-align:left}}.row{{display:flex;gap:8px;flex-wrap:wrap}}
figure{{margin:0}}img{{max-width:460px;border:1px solid #888;background:#fff}}code{{background:#eee}}</style></head><body>
<h1>CAD reference evaluator v2 — private developer review</h1>
<p><b>Scope:</b> 3 synthetic source families, 18 archive cases re-graded from existing outputs (no tracer run).
Each column is a separate dimension shown as numerator/denominator; there is no combined score and no general accuracy claim.
Images are the actual archive input raster and the archive's own rasterizer outputs (no re-rendering).</p>
<p>Controls: {controls_info['tests_run']} tests, ok={controls_info['ok']}.
Tolerances: <a href="tolerances.json">tolerances.json</a> · <a href="LIMITATIONS.md">LIMITATIONS.md</a> · <a href="manifest.json">manifest.json</a></p>
<h2>All 18 cases</h2><table><tr><th>case</th>{head}<th>merge flags</th><th>unsupported strokes</th><th>image IoU</th><th>input ink px</th></tr>
{''.join(rows)}</table>
{''.join(figs)}
<h2>Controls: v2 versus the original VEC-79 grader</h2><table><tr><th>control</th><th>expected</th><th>v2 numerators</th><th>original grader</th></tr>
{''.join(ctl)}</table></body></html>"""
    with open(os.path.join(out, "review.html"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(doc)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--archive", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    archive, out = os.path.abspath(a.archive), os.path.abspath(a.out)
    if not os.path.isdir(archive):
        print(f"error: archive not found: {archive}", file=sys.stderr)
        return 2
    os.makedirs(os.path.join(out, "cases"), exist_ok=True)

    ctl = run_controls()
    print(f"controls: {ctl['tests_run']} run, failures={ctl['failures']} errors={ctl['errors']}")
    if not ctl["ok"]:
        print(ctl["output_tail"], file=sys.stderr)
        dump(os.path.join(out, "controls.json"), ctl)
        return 3

    manifest, index, errors, files = check_archive(archive)
    if errors:
        for e in errors:
            print("error:", e, file=sys.stderr)
        dump(os.path.join(out, "archive-errors.json"), errors)
        return 4
    print(f"archive: {len(files)} files hashed, scene manifest and input hashes verified")

    results = []
    for c in index["cases"]:
        cid = c["case_id"]
        cd = os.path.join(archive, "out", "cases", cid)
        case = load(os.path.join(cd, "case.json"))
        scene = next(s for s in manifest["scenes"] if s["scene_id"] == case["scene_id"])
        truth = load(os.path.join(archive, scene["dir"], "truth.json"))
        geo = load(os.path.join(cd, "geometry.json"))
        with open(os.path.join(cd, "recovered.dxf"), encoding="utf-8", errors="replace") as fh:
            dxf_text = fh.read()
        res = score_case(truth, geo, case, reg=Registration.from_case(case, os.path.join(archive, scene["dir"], "reference.svg")), dxf_text=dxf_text,
                         input_png=os.path.join(cd, "input.png"), recovered_png=os.path.join(cd, "recovered.png"))
        dump(os.path.join(out, "cases", f"{cid}.json"), res)
        results.append(res)
        print(f"{cid:22s} " + " ".join(f"{k}={fmt(res['metrics'][k])}" for k in METRICS))
        if cid in REVIEW_CASES:
            dst = os.path.join(out, "assets", cid)
            os.makedirs(dst, exist_ok=True)
            for fn in ASSETS:
                if os.path.exists(os.path.join(cd, fn)):
                    shutil.copyfile(os.path.join(cd, fn), os.path.join(dst, fn))

    orig = original_counterexamples(archive)
    dump(os.path.join(out, "original-grader-counterexamples.json"), orig)
    dump(os.path.join(out, "controls.json"), ctl)
    dump(os.path.join(out, "tolerances.json"), TOLERANCES)
    summary = {"schema": "vecto.cad-ref.summary/2", "cases": [
        {"case_id": r["case_id"], "metrics": r["metrics"], "counts": r["counts"],
         "image": {k: r["image"].get(k) for k in ("iou", "ink_pixels_input", "ink_pixels_recovered")},
         "registration": r["registration"], "registration_disagreement_px": {k: r["registration"].get(k) for k in
                                          ("nominal_dpi_max_disagreement_px",)}}
        for r in results]}
    dump(os.path.join(out, "summary.json"), summary)
    tool_files = {os.path.relpath(os.path.join(dp, f), HERE): sha(os.path.join(dp, f))
                  for dp, _d, fs in os.walk(HERE) for f in fs if f.endswith(".py")}
    dump(os.path.join(out, "manifest.json"), {
        "schema": "vecto.cad-ref.run-manifest/2", "archive": archive, "archive_files_sha256": files,
        "evaluator_files_sha256": tool_files, "tolerances_version": TOLERANCES["version"],
        "tracer_invoked": False, "truth_used_during_conversion": False, "cases": len(results)})
    with open(os.path.join(out, "LIMITATIONS.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(LIMITATIONS.format(not_scored="; ".join(NOT_SCORED)))
    write_html(out, results, ctl, orig)
    print(f"{len(results)} cases graded -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
