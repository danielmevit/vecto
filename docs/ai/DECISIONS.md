# Decisions — Vecto

**Planar shared-boundary architecture (the core decision).**
Vector Magic's own sample SVGs show adjacent regions carrying *literally identical* Bezier
control points on shared boundaries (one side reversed) — the image is a planar partition,
which is why its output has no hairline gaps or overlaps. Vecto reproduces this
structurally: boundaries are traced once as chains between junctions, fitted once, and
reused by both regions. Consequence: any smoothing/fitting must operate per chain, never
per region — per-region processing would break the guarantee.

**Engine written from scratch in C#, not wrapped.**
VTracer (Rust) and potrace (GPL, per-color stacking — not planar) stay external
benchmarks, never dependencies: full control over the planar property, no license
contamination, no second toolchain. The algorithms used are all published (Schneider
fitting, k-means++, crack following, Douglas–Peucker).

**C# / .NET 8 / WPF, engine split from UI.**
Matches Floato's stack and build discipline (0 warnings). Vector Magic itself ships the
same split (`engine_project.dll` + `formats.dll` + Qt exe). WPF renders the preview as
retained-mode geometry — vector-crisp at any zoom, no browser.

**4-connectivity + left-turn pinch rule.**
Components are 4-connected; at checkerboard lattice points the walker prefers the sharpest
left turn, so contours pinch to a point instead of crossing. Both are required for the
boundary graph to stay planar (8-connectivity makes diagonal regions cross).

**Oklab for all color distances.**
Perceptually uniform, cheap (LUT + cbrt), public-domain transform. Display colors are sRGB
means of flat pixels only — AA fringes must not tint fills (white stays #ffffff, matching VM).

**No LAPACK/BLAS.** Vector Magic ships them for its least-squares fitting; Schneider's
formulation reduces to a closed-form 2×2 solve per segment, so plain doubles suffice.

**ImageSharp only in Cli/Tests, WPF codecs in App, Core dependency-free.**
Core takes raw RGBA buffers (`RasterImage`) so the engine stays portable and testable.
ImageSharp is pinned to exactly **4.1.3** (owner decision 2026-10-12, after five unpatched
advisories on the floating 3.1.x line). The free, assembly-scoped community key for the
`vecto` assembly is committed at `Vecto.Cli/sixlabors.lic` — licence mechanics, renewal
and constraints are in GOTCHAS.md.

**Culture-invariant output by construction.** All numeric formatting in the SVG writer and
CLI goes through InvariantCulture; a de-DE test guards it (comma decimals would silently
corrupt SVG path data on European locales).

**Naming: Vecto.** Fits the Floato/Eqho family. No tagline — the name plus a factual
descriptor ("image-to-vector converter") is the brand voice; cute slogans were vetoed.

**UI theme provenance (matters for open-sourcing).** The dark theme's *values* (near-black
surfaces #0A0A0A/#171717, hairline white-alpha borders, #0C8CE9 accent, Inter, 4–8 px radii)
follow the design language of Pixel Point's Toolcraft, which is under a proprietary
"Designer License". **No Toolcraft source code was copied** — Themes/Theme.xaml is original
WPF written from scratch; color/spacing values are unprotectable facts. Do not paste
Toolcraft CSS/JS/components into this repo; that would attach their license.


## Relicense Vecto from MIT to AGPL-3.0-only — done in v0.4.4 (owner decision, 2026-10-14)

From v0.4.4 on Vecto is **AGPL-3.0-only**, for dual licensing: AGPL for everyone, plus a paid
commercial licence for companies that want Vecto in a closed product. Possible because all Vecto
code is the owner's own (copyright Daniel Mitev, public name Daniel Mevit). Releases up to and
including v0.4.3 stay MIT forever.

Done (VEC-44): `LICENSE` = AGPL-3.0 text with a Vecto header; new `NOTICE` (copyright, public
attribution, AGPL summary, warranty, commercial licensing, trademark); SPDX `AGPL-3.0-only` in
`Directory.Build.props`; README badge + licence section + contributor relicensing sentence; site
MIT mentions (the site changelog keeps the historical "MIT" for 0.4.1). The Vecto name and logo
are not licensed with the code: forks must rename. Contributions may be relicensed by the owner
(needed for commercial licences), so outside contributions need that agreement.

ImageSharp (CLI only) stays compatible: its Split License grants Apache-2.0 terms to software
under an open-source licence, and Apache-2.0 code may be combined into an AGPL-3.0 work. A
closed-source commercial build of the CLI would no longer qualify via "open source" — it then
needs the Split License's small-company clause (< 1M USD revenue) or a Six Labors commercial
licence. The app and the engine do not use ImageSharp.


## LineArt scan mode: binarize to pure ink/paper before the pipeline (VEC-07, issue #1)

The `ImageStyle.LineArt` front-end (`ScanPreprocess.ToInkPaper`) normalizes any scan to pure
black ink on pure white paper, so the palette is an exact 2-colour palette by construction,
regardless of the original colours (blueprint blue, pencil grey, aged paper). Sauvola adaptive
thresholding runs on Rec.601 luminance via integral images, with k = 0.42 and window 31 —
measured on the synthetic CAD benchmark (recall/precision 0.97/0.97): lower k dilates strokes
with their blur halos, higher k dissolves faint 1-px strokes. Diagonal-contact bridging
deliberately merges strokes that touch only corner-to-corner — line-art mending, and the ink
is one region colour either way — because 4-connected labelling would otherwise shatter 1-px
diagonal strokes into runs that small-region absorption deletes. Auto never selects LineArt:
a unique-colour census is meaningless on a noisy scan, so it stays a deliberate user choice.
Small-region absorption doubles as the despeckle pass on the binarized raster, so no separate
despeckle stage exists.


## CAD mode: interpret the geometry, don't transcribe the pixels (issue #1, VEC-50/52/52d/53)

A scanned drawing is a photograph of a drawing, but the drawing itself is geometry — straight
lines, circles, arcs, dimensions, text. Users don't want the picture re-described as vector
shapes; they want the drawing's *intent*: one straight line as one selectable stroke with
exactly two anchor points, a circle as one exact circle, text left legible. The acceptance
line for the whole feature family: *"a straight wall in the drawing opens in Inkscape as one
path with exactly two anchors, and a 30-degree line reads 30.00 degrees."*

That framing drove the centerline stage, the clean-primitive and angle stages, and the
huge-sheet spatial indexing (ALGORITHM_INDEX.md rows 5b/5c; findings and measured numbers in
`docs/ai/reviews/2026-10-19-cad-pdf-handoff.md`). Clean weighted two-anchor straight lines,
canonical circles on clean supported rings and filled legible text describe the *intended
output on eligible ink* — they are not a claim of universal recognition. Majority stroke
recovery on dense, hatched or connected scans is still open (next entry).

Current priority under that framing is a bounded architecture/evaluation reassessment —
original-scan role/geometry references, automatic versus oracle-text-mask prototypes,
meaningful coherent primitives, explicit source-ink ownership — before another broad
classification patch. The existing two-anchor fitting, angle rationalization and exporters
remain useful. No architecture winner, automatic text solution or implementation completion
estimate is established; the whole issue #1 goal stays OPEN. Acceptance is semantic, beyond
counts: hatches/linework as coherent two-anchor strokes, true glyphs filled and legible, no
fragment explosion, justified circle/arc intent — full-sheet majority stroke recovery
remains unachieved as a quality-gated deliverable.

## Genuine text stays filled — mandatory protection, not optional text separation

Keeping real glyphs filled and legible is a mandatory acceptance condition of the CAD arc —
it is not deferrable "text separation" work. The centerline classifier strokes a component
only when it is thin AND long AND not a text-row glyph; everything else (text, hatching,
wide ink) keeps the filled region treatment, because centerlining letters would destroy
them. The deliberate cost: hatch grids are dense same-size marks, indistinguishable from
glyph clusters by these tests, so they stay filled too, and dense low-res scans over-trigger
the conservatism — the 1913 pump sheet traces 369 strokes against 7,079 filled paths (4.95%
by the original 7,448-path denominator). The semantic text/graphics discrimination needed
for CAD quality is current work (part of the architecture reassessment above); deskew and
OCR/editable-text *recognition* may remain optional/later — glyph *protection* may not.

## Angle model: drafting intent, not measurement noise (VEC-52d)

Fitted line angles carry drafting intent: near-parallel lines cluster to one angle
(wrap-aware at the 0/180° seam, `AngleClusterTolDeg` 1.0°), cluster angles within 1.5° of
the 15° grid snap exactly, and everything else keeps its measured WHOLE degree (62.7° →
63°). Genuinely off-grid angles are never forced onto the grid; arcs and circles are
untouched. Whole-degree rounding is a deliberate interpretation policy — the model reads
scan noise as intent without inventing geometry, but it does not claim that every measured
17.2° line was certainly intended as 17°.

## Circle/arc intent: exact where supported, honest cubics elsewhere

The Kåsa fitter preserves shared endpoints exactly even when the fitted radii differ, so
noisy rims come out as near-circular generic cubic pieces, and only clean supported rings
become canonical/exact circles. Canonical circular construction on noisy connected real
drawings remains an open LineArt issue — the fitter is shared with the general pipeline and
is deliberately not changed for CAD. Consistent with that, the DXF writer correctly refuses
arbitrary cubic-to-arc conversion, a perspective ellipse is never forced to a circle, and no
claim is made that every real rim must be circular. Dated measured results (e.g. the
VEC-53 bridge sheet) are preserved as history, not as guaranteed current behavior.

## Core stays BCL-only; new third-party code lives only in Cli/App and only permissive

`Vecto.Core` remains dependency-free; every new third-party byte of the CAD/PDF arc lives in
Cli/App only. New dependencies are permissive-only — MIT / Apache-2.0 / BSD (PDFtoImage
(MIT) over PDFium (Apache-2.0/BSD-3, non-V8 builds)) — this is the owner's distribution
policy for the dual-licensed codebase. MuPDF and Ghostscript stay excluded by that owner
policy; no GPL/LGPL/AGPL component may be added. The ImageSharp 4.1.3 exact pin, the committed assembly-scoped
community key and their constraints are unchanged — see the existing entry above and
GOTCHAS.md.

## Hand-written exporters: PDF (VEC-49) and DXF R12 (VEC-54)

**PDF:** `PdfWriter` is hand-written, zero-dependency and byte-deterministic — writing a
PDF is emitting path operators, so the export writer requires no PDF parser dependency
(true vector paths, never a raster inside a PDF). PDF input still uses PDFium through
PDFtoImage; this is a dependency-scope fact, not a broad security claim.

**DXF:** ASCII DXF **R12**, hand-written, because the candidate .NET libraries (netDxf
among them) cannot write R12 — no library was needed or wanted. Conventions: 1 image pixel
per drawing unit, lower-left origin with Y flipped, one LAYER per palette colour (nearest
ACI), stroke weight as polyline global width, arcs as VERTEX bulges, exact zero-width full
circles as CIRCLE entities, and never SPLINE. R12 constraint to state plainly: **filled
regions and text export as unfilled closed contours** — R12 has no fill/hatch entity the
writer targets, so no text-fill is preserved. Weighted rings are bulged polylines with
global width; only exact zero-width full circles become CIRCLE — no universal-CIRCLE or
text-fill claim. General curves stay within `ApproxTolerance` 0.1 px of the traced geometry,
with an explicit failure if the bounded subdivision budget cannot meet the tolerance.

