# Algorithm index — Vecto.Core pipeline

Orchestrator: `Tracer.Trace` (Vecto.Core/Tracer.cs). Stages run in order; every stage is
deterministic for a given input + options + seed. All tuning constants are resolved in one
place: `Tracer.Resolve` → `EffectiveParams` (dumped by `vecto trace --stats`).

| # | Stage | Algorithm | Key params | Anchor (file · symbol) |
|---|-------|-----------|------------|------------------------|
| 1 | Style resolve | unique-color census → Crisp/Blended heuristic | ≤64 unique → Crisp | Tracer.cs · `Tracer.Resolve` |
| 2 | Prefilter (Photo only) | 3×3 per-channel median | — | Preprocess.cs · `Preprocess.Median3x3` |
| 2b | Scan binarize (LineArt only) | luminance → Sauvola via integral images → ink/paper raster → 8→4 diagonal bridging (LineArt style only; Auto never selects it) | window 31, k 0.42, R 128; threshold floor 31 after alpha-to-paper normalization guarantees opaque luminance ≤30 is ink; no guarantee for noisy black >30 or arbitrary gray fills; very dark backgrounds may change | ScanPreprocess.cs · `ScanPreprocess.ToInkPaper` / `InkMask` / `BridgeDiagonalContacts` |
| 3 | Palette | exact unique colors, or weighted k-means++ in Oklab over a color histogram; Auto mode merges clusters agglomeratively | `KMeansK` (≤64), `MergeThreshold` 0.055 (0.03 photo), edge-excluded sampling (Δchannel > 24) | PaletteBuilder.cs · `PaletteBuilder.Build` |
| 4 | Labeling | nearest palette color in Oklab; alpha < 128 → transparent (-1) | `Segmenter.OpaqueThreshold` | Segmentation.cs · `Segmenter.Label` |
| 5 | Absorption | regions < MinRegionArea relabeled to longest-border neighbor (iterated ≤10 passes); eats AA fringes and dust | `MinRegionArea` per Detail/Style (2–24 px) | Segmentation.cs · `Segmenter.AbsorbSmallRegions` |
| 5b | Centerline extraction (LineArt only) | ink-mask 4-connected components → exact squared EDT → medial ridge → skeleton cleanup → junction-split chains (ridge bridge reconnects staircase breaks) → spur pruning → per-chain width (2·DT−1, even-width twin correction); whole components require thin AND long AND not a text-row glyph; before either admission, repeated similar-size row components are protected using the existing global glyph scale plus a compact (aspect ≤ 4) component-ridge-median glyph scale, so hairlines cannot make larger glyph rows bypass protection; new local candidates additionally use component-centre row directions at arbitrary angles (four similar-size peers within 4 × component maximum dimension, transverse error ≤ 0.25 × smaller component maximum dimension); own ridge-median/EDT-maximum scale with intrinsic principal-axis compactness ≤ 4 excludes diagonal hatches, and a 64-neighbour hypothesis cap conservatively preserves fill in crowded contexts. Uniform thin circular annuli (box aspect ≤ 1.1, radial thickness ≤ 0.25 × mean radius) retain CAD admission; round O/C-like letters remain ambiguous. The annulus exception also bounds the added component-scale horizontal guard above the original global glyph scale, preserving CAD circle rows; the original global glyph rule remains intact. VEC107/109: new local admission of any compact component (intrinsic extent ≤ 20 × (own median ridge width + parity slack)) requires positive primitive evidence — currently only an isolated full rectangular frame (own-frame band coverage ≥ 95 % ink, every side ≥ 90 % inked) around exactly one counter with no similar-scale compact neighbour within 4 × its extent; all other compact marks (open E/F/H/L, numerals, B/8, rectangle rows) stay filled; thin annuli keep their own path. The neighbour scan runs only for that rare rectangle case and exits on the first hit. Mixed-width components beyond the global size gate may emit long locally thin runs with near-uniform 10th–90th percentile widths (0.6–1.4 × run median). Nonuniform rejected runs get one partition by width bands around their median, median-filtered with radius ceil(global median width), min 2 px; each part must independently pass the original support/length/uniformity checks. Source-supported complete circular cavities in explicitly non-text local-eligible mixed components emit one measured, closed four-quarter cubic stroke; original-ink coverage rejects gaps/occlusion, and only fragments whose entire painted band is contained by the accepted ring are removed. Whole-component rings retain their existing path. DXF recovers analytic bulges and measured width; SVG retains the cubic approximation. Local source ink is released only within the final emitted round stroke band (width/2 + 0.5 px), after fitting, welding, angle and junction movement; solid/uncovered ink stays filled | `ThinFactor` 2.0, `ThinSlack` 0.75, `LongFactor` 8.0, `TextMaxFactor` 20.0, `TextPeerMin` 4, `BridgeDist` 4.5 px, `BridgeMaxAngleDeg` 45°, `LocalUniformLow/High` 0.6/1.4 | Centerline.cs + CenterlineLocal.cs · `Centerline.Extract` / `EdtSq` / `Ridge` / `Cleanup` / `BuildStroke` |
| 5c | CAD regularization (LineArt only) | corner-split runs: total-least-squares straight-line hypothesis at a width-derived tolerance (45°-multiple snap) → else the fitter's arc/Bezier path (Kåsa arcs; clean supported rings can come out canonical/exact); junction weld merges collinear pieces a crossing split; endpoints snap to true line-line intersections; mixed-component exact fitted straight segments at least 8 × their width become individual editable paths before angle rationalization, using nearest ridge samples along each primitive interior (exclude 2 × chain width at either end; search radius ceil(line tolerance + 1 px), require ≥ 8 × chain width samples and the same 0.6–1.4 uniformity check) for its own weight (closed rings/curved portions stay coherent; straight remnants shorter than 2 × width retain fill instead of becoming cap/spur objects); angle rationalization: wrap-aware near-parallel clustering → 15°-grid snap → whole-degree rest | `FitWidthFactor` 0.5 (clamp 0.25–1.5 px), `CadAngleSnapDeg` 1.5°, `JunctionMergeGap` 4.5 px, `AngleClusterTolDeg` 1.0°, `AngleGridStepDeg` 15°, `AngleGridSnapDeg` 1.5° | Centerline.cs · `FitCadPrimitives` / `StraightRunLine` / `MergeCollinearAcrossJunctions` / `SnapEndsToIntersections` / `RationalizeLineAngles` |
| 6 | Components | 4-connected flood fill (4-conn is required for a planar crack graph) | — | Segmentation.cs · `Segmenter.ConnectedComponents` |
| 7 | Boundary graph | crack-following on the pixel-corner lattice; maximal chains between junctions traced once, shared by both regions; left-turn preference pinches checkerboard points instead of crossing | `TurnOrder` | BoundaryGraph.cs · `BoundaryTracer.Run` / `.Walk` / `.Assemble` |
| 8 | Corners | turn angle over k-vertex support on the raw staircase, greedy non-max suppression; then **validation**: re-measured on refined sub-pixel geometry (≥55° survives), demotions reprocess the chain — kills quantization-spike corners on small circles | k=3, threshold 68° (must exceed 63.4° — see comment at `Tracer.Resolve`) | ChainGeometry.cs · `ChainGeometry.DetectCorners` / `TurnAngleAt`; Tracer.cs · chain loop |
| 9 | Smoothing | clamped Laplacian (Jacobi), corners + junctions pinned to lattice, λ damped within 2 vertices of pins (protects thin-tip flanks) | 8 iters, λ 0.55, clamp 0.6 px | ChainGeometry.cs · `ChainGeometry.SmoothInPlace` |
| 9b | Sub-pixel refine | slide each point along its normal to the 50% coverage crossing of the two region colors (bilinear-sampled from the source) — recovers the edge position that anti-aliasing encodes; alpha channel used against transparent sides | `SubpixelMaxShift` 0.75 px (0.35 crisp) | ChainGeometry.cs · `ChainGeometry.SubpixelRefine` |
| 9c | Jitter pass | 3 more clamped-Laplacian iterations relative to the *refined* positions — per-point estimates jitter; this trades <0.25 px for ~20% fewer nodes | λ 0.5, clamp 0.25 px | Tracer.cs · call site in `Tracer.Trace` |
| 9d | Corner relocation | each validated corner moves from its lattice pin to the intersection of least-squares flank lines fitted to the refined geometry (full run when straight ≤0.4 px — both corners of a shared side then use the *identical* line; else the 4 nearest); flank directions within 1° of a 45° multiple snap exactly, so axis/diagonal sides come out mathematically straight; the AA-contaminated shoulder vertices are projected onto the flank lines. Junctions stay pinned (multi-chain solve — ROADMAP) | `CornerRelocateMaxShift` 1.0 px, `AngleSnapDeg` 1.0° | ChainGeometry.cs · `ChainGeometry.RelocateCorners` |
| 10 | Curve fitting | simplest-model-first per range: straight chord (`TryLine`) → circle arc on long spans (`TryArc`, Kåsa fit, ≤90° pieces that preserve the shared endpoints exactly — clean supported rings come out canonical circles, while noisy rims whose endpoint radii differ stay near-circular generic cubic pieces) → greedy straight-run prefix/suffix extraction (`LongestLinePrefix/Suffix`, 0.35× tol, min 16 pts) → Schneider least-squares cubic (Graphics Gems 1990) → arc fallback → recursive split; finally collinear line merge (`MergeCollinearLines`) heals seam/split-divided sides | `FitToleranceSq` 0.09/0.25/1.0 px² by Detail | BezierFitter.cs · `BezierFitter.FitCubic` / `FitChain` |
| 10b | Polygon mode | Douglas–Peucker per chain (debug/tests; ε=0 → exact lattice) | `PolygonEpsilon` | ChainGeometry.cs · `ChainGeometry.SimplifyDp` |
| 11 | Assembly | region loops concatenate shared chain curves (reversed for the right-hand region — identical geometry both sides) | — | Tracer.cs · `Tracer.BuildLoop` |
| 12 | Emit (SVG) | VM-style SVG: one path per region, holes as subpaths, plus one `fill="none"` stroked path per centerline stroke (round caps/joins, stroke-width = measured weight); 2-decimal invariant coords, near-straight cubics → `L` | `CubicBezier.IsLine` tol 0.02 | SvgWriter.cs · `SvgWriter.Write` / `AppendStroke` |
| 12b | Emit (PDF) | hand-written, zero-dependency: true vector path operators (not a raster inside a PDF), holes as subpaths, stroked centerlines with round caps/joins, 1 px = 1 pt, byte-deterministic output | — | PdfWriter.cs · `PdfWriter.Write` / `AppendStroke` |
| 12c | Emit (DXF, ASCII R12) | hand-written R12 (`$ACADVER` AC1009): contours as POLYLINE/VERTEX with bulges, stroke weight as polyline global width, exact zero-width full circles as CIRCLE entities, one LAYER per palette colour (nearest ACI), 1 image pixel per drawing unit, lower-left origin (Y flipped); never SPLINE; filled regions and text become unfilled closed contours | `ApproxTolerance` 0.1 px (bounded subdivision, explicit failure if unmeetable) | DxfWriter.cs · `DxfWriter.Write` / `WriteContour` / `TryConstruction` |

Huge-sheet scaling (VEC-53): the 5c weld and snap passes find candidates through spatial
hashes — an endpoint grid with `JunctionMergeGap` cells for the weld, an 8-px
segment-crossing grid for the snap — instead of O(n²) scans. On the 14,444×9,632 bridge
sheet this went from >20 min to 34.5 s with equivalent output (VEC-53 measurement, dated
2026-10-19 — a historical figure, not a guaranteed current runtime).

Remaining CAD quality gap (explicit, not a checklist item): the 5b classifier is
deliberately conservative — hatch grids are dense same-size marks, indistinguishable from
glyph clusters by the thin/long/text-row tests, so they stay filled. On the 1913 pump sheet
only 369 of 7,448 paths (4.95% by the original path denominator) became strokes; 7,079
stayed filled. The clean two-anchor machinery works on eligible ink (62–77% of strokes on
clean scans are pure two-anchor lines); majority stroke recovery on dense, hatched or
connected linework is the open work, and the current priority is a bounded
architecture/evaluation reassessment (original-scan role/geometry references, automatic
versus oracle-text-mask prototypes, meaningful coherent primitives, explicit source-ink
ownership) before another broad classification patch — no architecture winner or completion
estimate is established.

Circle/arc intent on real drawings (recorded; no code change here): the Kåsa fitter
preserves shared endpoints exactly even at differing radii, so noisy rims come out as
near-circular generic cubic pieces, and only clean supported rings become canonical/exact
circles. Canonical circular construction on noisy connected real drawings remains an open
LineArt issue. The DXF writer correctly refuses arbitrary cubic-to-arc conversion; a
perspective ellipse is not forced to a circle, and no real rim is claimed to be circular
without support.

## Color space
sRGB → Oklab (Ottosson), LUT-accelerated: OklabColor.cs · `Oklab.FromRgb`. Distances in
Oklab; display colors stay sRGB — palette entries average only flat (non-edge) pixels so AA
fringes can't tint fills (Tracer.cs · `Tracer.CompactPalette`).

## Evaluation harness (ground-truth round trip)
`Rasterizer` (Core) renders any `VectorDocument` to pixels (nonzero scanline, supersampled);
`SvgReader` (Cli) parses M/L/H/V/C/Z path SVGs back into documents. `vecto bench <svg...>
[--scale s]` runs original vector → raster → trace → re-render → per-pixel Oklab ΔE vs the
raster, writing `.in.png` / `.out.svg` / `.out.png` / `.diff.png` (heatmap) next to each
input. This is the tuning loop: change a constant, re-bench VM's sample SVGs, compare
meanΔE / >JND% / node counts against ground truth.

Scope limit of that harness (recorded; no code fix here): `SvgReader` parses only flat
filled `path` elements and skips `fill="none"` — stroked centerline paths are dropped. A
stored showcase PNG, a CLI re-render, or a `vecto bench` round trip that goes through
`SvgReader` is therefore unreliable for stroke fidelity. Validating strokes requires
`Rasterizer.Render` directly on the traced document (as
`CadScanBenchmark_RecallAndPrecisionAtLeast95` does, holding recall/precision ≥ 0.95) or a
faithful independent SVG renderer.

## Invariants the tests enforce (Vecto.Tests/TracerTests.cs)
- Planar partition: Σ region areas == opaque pixel count (exact in polygon mode,
  ≤0.5% in curve mode; boundary shifts cancel between neighbors).
- Corners of axis-aligned rectangles stay exact lattice points in both modes.
- A circle r=90 fits in ≤16 cubics with ≤1.6 px radial error.
- Same input + seed → byte-identical SVG; output is culture-invariant (de-DE guard).
- An anti-aliased axis-aligned box with sub-pixel borders traces to exactly 4 lines that
  are *exactly* horizontal/vertical (1e-6) at the encoded sub-pixel positions (±0.2 px).

VEC114 local junction finishing (`CenterlineLocalJunctions.cs`): after legacy final
snapping, before local ink release, spatially indexed same-component painted
line/circle bands may shorten finite local line endpoints to their centerline.
Original component cross-sections and the complete removed centerline interval
must support the join; parallel, foreign, unsupported and crossing ends stay.
Accepted circle proposals retain their measured geometry and source identity.

Local analytic arc endpoints use source tangent-side cross-sections beyond the join,
with observation reach `max(6 × width, 3 × sqrt(radius × width))`. At least 12 of
17 sections must fit a straight centreline (0.75 px residual); isolated remote
branch sections may be omitted, but the attachment must contain continuous source
ink. Tangent estimates require a coherent central population (interquartile spread
≤2 px along the circle). Only a verified tangent permits a width-long endpoint
section contaminated by the attachment; interior gaps retain the strict section
check. Output angles use DetMath. Final curved ownership releases only the painted
half-width, retaining boundary source ink as fill instead of using the straight
line's extra 0.5 px release margin. Anchors: `RecoverLocalArcs`, `ReleaseLocalInk`.

VEC122 local curve ownership retains the half-pixel raster release margin in
curve interiors and closed loops. Beyond an open curved endpoint's tangent
plane, only the painted half-width round cap releases source ink. Tangent
recovery targets must advance from the source-supported trimmed seed angle;
a backward remote foot cannot retract that endpoint. Crossing-contaminated
sections may retain source fill rather than claim a complete arc.
