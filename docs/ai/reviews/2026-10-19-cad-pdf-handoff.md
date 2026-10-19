# CAD / PDF build-out — findings, state and handoff (2026-10-19)

Session wrap-up document for the issue #1 feature arc and the PDF round trip. Lives here beside
the 2026-10-12 review and research docs. Session-operational detail (Unio routes, adapter state,
run ledger) is in the project folder: `coord/reports/HANDOFF-2026-10-19.md` — this file is the
product view.

## The main goal (and the idea that carried it)

GitHub issue #1 (2026-10-09): an engineer tracing scanned mechanical drawings got "crooked lines
and distorted text" and asked for a CAD-optimized mode. The real requirement underneath: a
scanned drawing is a photograph of a drawing, but the drawing itself is **geometry** — straight
lines, circles, arcs, dimensions, text. Users don't want the picture re-described as vector
shapes; they want the *drawing's intent*: one straight line as one selectable stroke with two
anchor points, a circle as one exact circle, text left legible.

That framing — **interpret the geometry, don't transcribe the pixels** — drove every design
decision in the arc below. The acceptance line for the whole feature family was set at the end:
*"a straight wall in the drawing opens in Inkscape as one path with exactly two anchors, and a
30-degree line reads 30.00 degrees."*

## What shipped (all merged to dev, each gated: verify PASS + cross-vendor review APPROVE)

1. **Line art mode, Stage 1** (`ScanPreprocess.cs`, VEC-07c): Sauvola binarization via integral
   images (window 31, k 0.42, R 128 — measured), 8→4 diagonal bridging so 1-px diagonal strokes
   survive 4-connected labelling. Auto never selects it. Benchmark recall/precision 0.97/0.97.
2. **PDF input** (`Vecto.Cli/PdfInput.cs`, VEC-47): scanned-image PDFs rasterize at 600 DPI
   (no image smoothing — the edges Sauvola needs), `--pdf-dpi`, `--pages`, 16k-px cap.
   Dependency: PDFtoImage (MIT) over PDFium (Apache-2.0/BSD-3, non-V8 builds) — permissive-only
   stack so AGPL and closed commercial builds both stay clean; MuPDF/Ghostscript were rejected
   because Artifex's commercial terms would poison Vecto's dual-licensing.
3. **PDF export** (`Vecto.Core/PdfWriter.cs`, VEC-49): hand-written, zero-dependency, true
   vector paths (not an image in a PDF), byte-deterministic, roundtrip-proven through PDFium.
   Rationale: writing PDFs is emitting path operators — no parser attack surface, no dependency.
4. **PDF import in the app** (`Vecto.App/PdfImport.cs`, VEC-48b): open/drag-drop, first page
   600 DPI off the UI thread; display-only BitmapSource conversion.
5. **CAD Stage 2: centerline extraction** (`Vecto.Core/Centerline.cs`, VEC-50): Euclidean
   distance transform → medial ridges → junction-split chains → spur pruning → per-chain width;
   thin-vs-wide classification keeps text/hatching/wide ink as filled regions (centerlining
   letters would destroy them). Strokes render in SvgWriter, PdfWriter and Rasterizer.
6. **CAD Stage 3: clean primitives** (VEC-52): corner-split TLS line fits at a width-derived
   tolerance → one 2-anchor path per straight run; Kåsa arc hypothesis → exact arcs; junction
   weld (facing endpoints ≤4.5 px, direction matched) merges pieces split by crossings;
   endpoints snap to true line-line intersections.
7. **Angle rationalization** (VEC-52d, owner's model — prototyped in
   `coord/artifacts/prototype-angles/angle_proto.py`): fitted angles carry drafting intent —
   near-parallel lines cluster to one angle (wrap-aware at the 0/180° seam), angles within 1.5°
   of the 15° grid snap exactly, everything else rounds to a whole degree (62.7° → 63°).
   Genuinely off-grid angles keep their measured whole degree; nothing is forced onto the grid.
8. **Huge-sheet scaling** (VEC-53): the Stage-3 passes had an O(n²) weld scan — the
   14,458×9,632 bridge sheet went from 51 s (pre-Stage-3) to >20 min, fixed with spatial-hash
   weld, snap grid and angle buckets: **34.5 s**, output equivalent, 155/155 tests.

## Key findings (measured, reproducible)

- **The headline metric**: before Stage 3, real scans produced **0 clean two-anchor lines out
  of ~9,200 strokes** (average 4 anchors on clean 400-DPI scans, 28 on old patent scans).
  After: 62–77% of strokes are pure two-anchor lines depending on the drawing, average 2.2–2.5
  anchors across the whole showcase set. The "straight means straight" requirement is met.
- **Fidelity held throughout**: retrace meanΔE ≈ 0.0006–0.0008 on the patent sheets (0.1% of
  pixels above the perceptual threshold); planarity deviation 0.00% on every showcase trace;
  Auto and all pre-existing styles byte-identical at every merge.
- **Text protection works and must not regress**: Gemini's visual QA of real scans (Tesla,
  Wright, HAER, pump) judged text "exceptionally legible and undistorted" through the whole
  pipeline. The deliberate cost: text, hatching and wide ink remain filled regions.
- **The remaining quality gap — classification** (owner-confirmed at wrap-up): hatch grids are
  kept filled because the text-protection heuristic can't distinguish them from glyph clusters
  (both are dense same-size marks). Hatch = long, parallel, evenly spaced strokes → should be
  centerlined; glyphs = small closed loops → stay filled. Dense low-res scans over-trigger the
  conservatism: the 1913 wood-screw pump traces 95% filled (369 strokes / 7,448 paths).
- **Cross-vendor review earns its keep**: it caught a critical pre-merge bug in the app's
  PDF display path (only blue+red channels populated → fully transparent render + corrupted
  trace input) that the implementing worker's dimension-only tests missed.
- **Licensing finding (durable)**: all new dependencies are permissive (Apache/BSD/MIT);
  the engine (`Vecto.Core`) stayed BCL-only — every new third-party byte lives in Cli/App only.
- **Angle model finding**: whole-degree rounding + 15° grid + parallel clustering, with the
  0/180° seam handled, reads scan noise as intent without inventing geometry (17.2° stays 17).

## The showcase (proof, open in Inkscape)

`coord/artifacts/cad-real/showcase/` (project folder, not the repo — asset files): six real
drawings traced with the final engine, renders matching, README with the numbers table. The
measuring script used for the 2-anchor metric lives in this session's history — the numbers in
the README are the source of truth.

## Open work, in recommended order

1. **DXF export (Stage 4)** — designed, no code. Perplexity proposal
   (recorded in `coord/proposals/perplexity/perplexity-fbf17ca092dc214a824f609e/`): ASCII DXF
   R12, POLYLINE/VERTEX + bulges, CIRCLE, one LAYER per palette colour (ACI), centerline width
   as polyline global width, hand-written ~300-line writer in Core. netDxf cannot write R12 —
   no library needed or wanted. Y-flip + lower-left origin, `$INSUNITS`, never SPLINE.
2. **Classification refinement** — hatch grids → strokes; loosen the line-like test on dense
   scans. This is the main remaining gap between "the lines that survived are perfect" and
   "the whole drawing reads as drafted lines."
3. **docs/ai refresh** — ALGORITHM_INDEX rows for the new stages (2b binarize, centerline,
   regularization, angles); DECISIONS entries for the interpretation-over-transcription
   philosophy, the angle model, the permissive-only dependency rule; STATUS/START_HERE.
4. **Owner decisions pending**: TIFF decoder (re-enable vs document PNG/JPG-only — the
   restriction was the VEC-01 security fix); expose `TraceDiagnostics.StrokeCount` in
   `--stats`; the `Centerline.Enabled` master switch is test-only (expose or remove).
5. Smaller: app-side PDF page picker; steam-engine render missing from the showcase; perf
   watch beyond 14k-px sheets.

## How to continue (five-minute recipe)

Repo: `dev` @ `091bbcc`, clean, pushed, 155/155 tests. Build with `dotnet.exe` from the repo
root (0 warnings required). Tasks go through the Unio loop in the project folder — the
operating notes that bit this session (bare scope lines, token budgets, salvage pattern) are
in `coord/reports/HANDOFF-2026-10-19.md` §5–6; the quick-continue recipe is its §9.
