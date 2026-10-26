# CAD reference evaluator v2 (developer tool)

A standard-library Python grader for the private CAD reference archive
(original vector -> raster at several DPIs -> Vecto reconstruction). It is a
measuring instrument, not a tracing algorithm, and makes **no general CAD
accuracy claim**: the archive contains only three synthetic source families.

Adapted from the read-only VEC-79 `tools/grade.py` / `pngio.py`; the archive
truth / case / live-geometry schemas are read unchanged.

## Command

```
python3 -B tools/cad_reference/validate.py --archive <vec79-partial archive> --out _bench/cad-evaluator-v2
python3 -B -m unittest discover -s tools/cad_reference/tests -v
```

`validate.py` runs the control suite, verifies the scene manifest and every
case's input/reference hashes, re-grades the existing recovered outputs of all
18 cases (the tracer is never run; truth is only read at grading time) and
writes into `--out` (keep it under the ignored `_bench/`):

| file | content |
|---|---|
| `cases/<case>.json` | per-entity results (lines, circles, arcs, connections, merges, unsupported strokes, image) |
| `summary.json` | all metrics per case |
| `manifest.json` | archive and evaluator file hashes, `tracer_invoked: false` |
| `tolerances.json` | versioned tolerances (`vecto.cad-ref.tolerances/2`) |
| `LIMITATIONS.md` | what is not scored / not claimed |
| `original-grader-counterexamples.json` | each control replayed through the original grader |
| `review.html` + `assets/` | standalone page with the actual input raster and the archive's own rasterizer outputs for B300 clean, C300 clean, A600 clean, plus a table of all 18 cases |

Exit codes: 0 ok, 2 bad arguments/archive, 3 control failure, 4 manifest/hash or
missing-file failure. Expected control result: all tests pass.

## Metrics (separate dimensions, never combined)

Each is reported as `numerator/denominator`:

| metric | numerator | denominator |
|---|---|---|
| `line_geometry_presence` | truth lines whose finite extent is covered (>= 90 %) by recovered **stroke** centrelines | truth lines |
| `line_two_anchor` | truth lines matched one-to-one by a single-segment finite straight stroke | truth lines |
| `line_two_anchor_precision` | matched two-anchor strokes | two-anchor strokes |
| `line_width_correct` | stroke-present truth lines whose covering stroke width is in tolerance | truth lines |
| `circle_complete` | truth circles closed by one stroke with full angular coverage, on-circle support and fit | truth circles |
| `circle_analytic_export` | truth circles exported as DXF CIRCLE or an exact closed bulge polyline | truth circles |
| `arc_complete` | truth arcs covered along their **signed** sweep (drawn side) by one stroke | truth arcs |
| `arc_analytic_export` | truth arcs exported as a matching DXF bulge arc | truth arcs |
| `junction_topology` | declared T-junctions where both objects are recovered, the host is continuous through the junction and the stem reaches it | declared T-junctions |
| `geometry_precision` | recovered strokes >= 90 % supported by truth geometry (lines, circles, arcs) | recovered strokes outside text/solid zones |

Geometry presence is **not** editability: a correct joined L-polyline passes
`line_geometry_presence` and fails `line_two_anchor`, and remains supported in
`geometry_precision`. A filled silhouette is reported as its own form and is not
stroke presence. Exact-format contracts are the DXF entities only; a cubic SVG
path that looks circular is not counted as analytic export. Tangent joins report
`connected_presence` only; tangency itself is not scored.

Corrections relative to the original grader (each has a control):
signed arc sweep / drawn side; junctions on recovered geometry of both objects;
merge flags only for one piece spanning distinct finite boundaries; finite
two-anchor test that rejects collinear overshoot; circle completeness by angular
coverage + closure; unmatched curved/closed strokes and DXF circles counted as
unsupported; DXF parser flushes on every entity transition and at EOF and
accepts exact bulge circles; width as its own dimension; pixel counts are pixels
(not 8x bits); one registration for strokes, regions and widths, taken from the
actual rounded CairoSVG viewport transform, including offsets.

## Limitations

Three synthetic families only (A, B, C); 18 cases. Not scored: drafting-angle
policy, tangency, hatch pitch/angle, text OCR/legibility, solid-fill fidelity
beyond supporting image measures. An unseen source family is the next milestone.

## Registration contract

Archived CairoSVG **2.9.1** cases require the reference SVG: root width/height
in mm matching `page_mm`, viewBox `0 0 page_width page_height`, default
`xMidYMid meet`, no root transform, and recorded output dimensions matching
`pixel_size`. The scale is `min(pixel_width/page_width, pixel_height/page_height)`;
centering offsets are half the remaining viewport space. This is the bounded
renderer path verified by direct `Surface.set_context_size` instrumentation.
At 150/300/600 DPI for the 80x60mm archive, scale/offset are respectively
`5.9 / (0,0)`, `11.8125 / (0,0.125)`, and `23.616666666666667 / (1/3,0)`.

Nominal DPI, the original textual transform and stored `mm_per_px` remain
unchanged provenance in receipts; they do not override this matrix. All stroke,
region, width, exclusion-box and DXF coordinates share this registration.
The review page copies original archive rasters/exports without re-rendering;
any overlay coordinates must use the same `to_px` inverse.
Unknown renderer versions/configurations and ambiguous missing transforms fail
closed. Without renderer metadata, the exact generic declaration
`px = mm * dpi / 25.4` means precisely nominal scale and zero offset; its receipt
explicitly says the renderer is unverified. No arbitrary future renderer claim
is implied. Matching tolerances and geometry algorithms are unchanged.
