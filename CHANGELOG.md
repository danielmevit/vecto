# Changelog — Vecto

## Unreleased

- **CAD documentation and trial roadmap.** Updated the tracing guides and recorded the
  remaining CAD reconstruction work, plus the requirement for an app-owned 24-hour Windows
  trial that preserves paid access.
- **CLI stroke statistics.** `--stats` now reports stroke counts alongside chains,
  regions, nodes and palette size.
- **DXF export for CAD editors.** Save with `-o drawing.dxf` in the CLI or **Export DXF…**
  in the app. ASCII DXF R12 preserves straight centerlines as two-vertex polylines with
  stroke width, circular arcs as bulges, and full zero-width circles as CIRCLE entities.
  Each palette colour gets its own ACI layer. Region outlines and holes, including text,
  export as unfilled closed contours; units are 1 image pixel per drawing unit. General
  curves stay within 0.1 px of the traced geometry, with an explicit failure if the bounded
  subdivision budget cannot meet that tolerance. Vecto.Core adds no dependencies.
- **Angles carry drafting intent.** Fitted line angles now read like a drafting callout:
  lines meant to be parallel come out exactly parallel, angles close to the 15-degree grid
  (0/15/30/45/60/75/90...) snap to it exactly, and every other angle is a whole degree —
  a line that measures 62.7 degrees was always a 63-degree line. Genuinely odd angles are
  left as measured; arcs and circles are untouched.
- **Line art strokes are now clean CAD primitives.** In Line art mode, a straight ink line is
  now ONE path with exactly two anchor points (straight means straight — no pixel zig-zag),
  a circle is one exact arc, and lines that merely touch or cross are welded back into single
  strokes with endpoints snapped to true intersections. On the CAD benchmark, anchors per
  stroke drop from 3.79 to 2.00 and stroke counts halve; every other style is byte-identical.
- **Line art strokes are now real strokes (CAD Stage 2).** In Line art mode, thin ink strokes trace
  as single stroked centerline paths with an accurate stroke weight — no longer dual-boundary filled
  ribbons — while text, hatching and wide shapes keep the filled treatment, so letters stay exactly
  as legible as before. SVG, PDF and rasterized output all render the strokes with round caps and
  joins, and the planar partition check still passes. On the Wright-flyer patent's ultra-thin
  bracing wires and the HAER bridge sheet's 1-pixel dimension lines, every stroke is now one
  editable vector path a CAD user can select, weight and snap — the core ask of issue #1.
- **PDF input in the app.** You can now open or drop a PDF straight into Vecto. The first page is
  rasterized in memory at 600 DPI — the same crisp, no-smoothing treatment the CLI gives scanned
  drawings — and traced exactly like a loaded image, so Line art works out of the box. Multi-page
  PDFs trace page 1 and tell you in the status line how many pages the document has (the CLI can
  pick other pages). PDFs that are corrupted or too large to rasterize safely are refused with a
  clear message, and whatever you had open stays untouched.
- **PDF export.** Traced drawings can now be saved as true vector PDFs — real paths, not a picture
  inside a PDF. Use `vecto trace scan.pdf -o out.pdf` in the CLI (any `-o` path ending in `.pdf`) or
  the new **Export PDF…** button in the app. Pages come out at the traced size (1 px = 1 pt), holes
  and all, and the file is byte-for-byte identical every time you export the same trace. As before,
  the default output is SVG; nothing about SVG export has changed.
- **PDF input.** `vecto trace drawing.pdf` now rasterizes PDF pages in memory and traces them like
  any scan — no export to PNG first. Pages render at 600 DPI by default (`--pdf-dpi`) with image
  smoothing off so line art stays crisp, `--pages 1,3-5` selects pages (one SVG per page for
  multi-page files), and oversized pages are rejected with a clear message instead of exhausting
  memory. The `samples` command now also writes `cad.pdf`, a realistic scanned-drawing PDF fixture.
  PDF rendering uses PDFium via the PDFtoImage library; Vecto.Core remains dependency-free.
- **New: Line art mode for scans and CAD drawings.** Scanned engineering drawings now have their own
  tracing style: pick "Line art (scans, CAD)" in the app or `--style lineart` in the CLI, and Vecto
  first cleans the scan — adaptive thresholding copes with uneven illumination, blur and noise to
  recover crisp pure-black strokes on white — then traces the result as an exact two-colour vector.
  Diagonal strokes that touch only corner-to-corner are mended so thin lines survive as single shapes
  instead of vanishing or doubling, and scan speckles are absorbed automatically. On a synthetic CAD
  benchmark the trace matches the clean original drawing at 0.97 recall / 0.97 precision, and every
  other style produces byte-identical output as before.

## v0.4.4 — 2026-10-15

New app icon, friendlier tips, pastel colours kept, and a new licence: Vecto is AGPL-3.0 from this version
(0.4.3 and earlier stay MIT), with commercial licences available.

- **Licence: AGPL-3.0 from this version.** Vecto stays open source; anyone who ships it, or runs it as a
  service, must share their source code. Companies that want Vecto in a closed product can buy a commercial
  licence instead (dual licensing). Versions up to and including 0.4.3 stay MIT. See NOTICE. The Vecto name
  and logo belong to Daniel Mitev (Daniel Mevit); forks must use a different name and logo.
- **Light pastel colours keep their own colour.** Cream or pale-blue shapes next to white no longer fade
  into one near-white colour, and the tiny stray dots of that colour along outlines are cleaned up. Results
  on our test sets are otherwise unchanged and identical on Windows, Linux and macOS.
- **New app icon.** The white Vecto "V" now sits on a solid navy rounded square, so it no longer disappears on
  dark taskbars and stays clear at small sizes (taskbar, Start menu, desktop, Store and the website tab).
- **Friendly tips for pictures that don't trace well.** A small picture (under 256 px on a side) gets a tip to
  use a bigger version; a picture with smooth colour fades explains that fades come out as bands of colour and
  that flat colours trace best. One line in the status bar, no pop-ups, and the traced result never changes.

## v0.4.3 — 2026-10-13

Tracing quality round. Against 0.4.2 on our test sets (wrong pixels vs the original artwork):
icons/flags/emoji 15% fewer (128-px versions 10% fewer, JPEGs 24% fewer and 39% fewer nodes);
realistic logos/charts/UI/drawings 3-18% fewer depending on size and compression; logos from
real-world samples up to 38% fewer; photos, painted art and old scans unchanged. Large scans trace
faster than in 0.4.2. Results are now identical on Windows, Linux and macOS builds, and the
command-line tool reads JPEGs exactly like the app.

Known limitations: a fine 4-colour tile pattern saved as JPEG traces slightly less accurately than
in 0.4.2 (curved tile edges are smoothed a little too much); tiny round line ends on small icons
can come out flat; very small text in JPEGs stays hard to trace (thin letters can deform and faint
grey captions can disappear — 0.4.2 turned them into coloured fragments instead).

### Build: fixed and future-proofed (VEC-01, VEC-02)
- The build broke on 2026-10-07 when five security advisories hit the image library the `vecto` CLI
  used (SixLabors.ImageSharp 3.1.x, pulled in through a floating version). The CLI now uses
  **ImageSharp 4.1.3**, pinned exactly, with Six Labors' free open-source community licence key
  (`Vecto.Cli/sixlabors.lic`, renew by 2028-01-10). The desktop app never used it. Store packages
  can be built again. `THIRD-PARTY-NOTICES.md` carries ImageSharp's licence for the CLI downloads.
- New CI builds and tests every push and pull request, and once a week, so an advisory that lands
  with no code change turns it red instead of going unnoticed. The SDK is pinned (`global.json`) and
  the test packages are current.

### App: everyday failures explained instead of the crash dialog (VEC-04b)
- Exporting into a read-only or locked location, a busy clipboard or an empty paste now show a short
  message in the status line; a file that cannot be opened shows a warning and leaves the current
  image and result untouched; a damaged settings file can no longer stop the window from opening.

### Engine: straight edges in JPEGs come out straight (VEC-22b)
- JPEG compression shifts an edge slightly differently in every 8x8 block, so straight bars traced
  with a gentle wave. On images detected as JPEG, each straight side between two corners is now
  fitted to one line through all its points: 3% fewer wrong pixels on the JPEG test set and
  visibly straight bars (e.g. the South Korea flag). Curved edges are left as they were.

### Engine: translucent overlaps keep their shape at small sizes (VEC-21b)
- Areas whose colour is a real blend of their neighbours (overlapping translucent shapes) are no
  longer cut apart by the soft-edge clean-up at small sizes.

### Faster start (VEC-24)
- Hot loops are compiled fully optimised on first use and the command-line tool skips .NET's
  profiling JIT tier: about 12% less total engine time on the test sets, identical results.

### CLI: JPEGs decoded exactly like the app (VEC-23)
- The command-line tool now decodes JPEGs the same way as the Windows app (libjpeg's integer IDCT
  and smooth chroma upsampling, our own code, no new dependency), so a JPEG traces identically in
  both: 26 of 26 test JPEGs match. Unusual JPEGs (CMYK, 12-bit, arithmetic coding) still use the
  previous decoder.

### Engine: small dark text in JPEGs stays dark (VEC-21, part 1)
- The soft-edge clean-up could merge so many fringe pixels into small dark text that its colour
  was averaged towards grey (a chart title in a JPEG). Such colours now keep their fringes.

### Engine: faster (VEC-20b)
- The new stages are tuned: large scans now trace faster than in 0.4.2 (a 2,000-px patent drawing
  0.70 s -> 0.42 s; 0.4.2 took 0.55 s), with byte-for-byte identical results.

### Engine: JPEG detection no longer fooled by gradients and small text (VEC-19c)
- The JPEG clean-up now also requires JPEG's 8x8 block pattern, so blurred shadows, gradients,
  downscaled text and UI screenshots keep their detail. On 114 test images: 0 false detections
  (was 13), all 42 JPEGs still detected.

### Engine: photos and textured images keep their detail (VEC-17)
- The soft-edge and JPEG clean-ups added in this version were also firing on photos, painted or
  textured artwork and old scans, where thin in-between colour bands are real shading. Vecto now
  measures how textured an image is first and leaves such images on the previous path: a photo
  test (Mona Lisa) is back to 0.4.2 quality instead of 20% worse, while logos and line-art scans
  keep their gains.

### Engine: small rounded tips stay rounded (VEC-16)
- The new corner stage could turn a narrow rounded tip (a rocket's stripe at 128 px) into a sharp
  point. Sharp-versus-rounded is now checked against the anti-aliased pixels at the tip: rounded
  tips stay round, genuinely sharp tips (wedges, star points) stay sharp.

### Engine: thin bars keep square ends, corners decided by shape (VEC-12)
- Corners used to be found by measuring the turn over a fixed 3-pixel window. At the end of a thin
  bar the two corners are only ~3 pixels apart, so the window saw one big turn and the end came out
  rounded — small rectangles became ovals, flag bars got teardrop ends. Each outline is now first
  reduced to its simplest polygon (after the published Potrace method, Selinger 2003; our own
  implementation), and every candidate corner is checked with its own two edges and against the
  image, so both corners of a bar end survive and circles stay smooth. Quality test set: small
  (128 px) images 4% fewer wrong pixels, clean 8%, JPEG 9%; the South Korea flag at 128 px 29%.

### Engine: soft-edge decision made where anti-aliasing blends (VEC-13)
- The soft-edge fix below decided "dark or light side" at the midpoint of a perceptual colour
  space; anti-aliasing blends in plain sRGB, so partly-dark edge pixels went to the light side and
  thin dark lines lost a little ink. The decision is now made in sRGB.

### Engine: JPEG specks cleaned up (VEC-14)
- On images detected as JPEG-like, specks up to 14 px left by compression ringing are merged into
  their surroundings: 10% fewer nodes on JPEGs, slightly more accurate, clean images unchanged.

### Engine: soft edges no longer become slivers of a third colour (VEC-10b)
- A pixel on the soft edge between two colours has an in-between colour, and it was often given to
  a third palette colour that happened to be closest — so edges traced as thin slivers: a grey strip
  along a dark camera body, pink smudges around a lion's muzzle, grey bumps along black icon
  strokes. Such pixels are now recognised as a blend of their two neighbouring colours and go to
  the nearer one; a genuine thin line in an unrelated colour is kept. Quality test set: clean
  images 5.5% fewer wrong pixels and 7% fewer nodes, small (128 px) images 6% / 6%; the worst small
  case (camera emoji) 31% fewer wrong pixels.

### Engine: JPEG inputs detected and smoothed harder (VEC-11b)
- Vecto now measures how noisy the inside of the traced shapes is (JPEG ringing and blocking show
  up there; clean images are perfectly flat). Noisy inputs get stronger edge smoothing, clean
  ones take exactly the old path. All 36 clean and small test images stay clean, all 18 JPEGs are
  detected. JPEG test set: 12% fewer nodes with both changes (27% since 0.4.2), slightly more
  accurate. Edges of heavily compressed JPEGs still wave a little — a straight-edge fitting stage
  is the planned fix.
- `vecto trace --stats` shows the measurement (`noise=… noisy=…`).

### Engine: cleaner JPEG traces — duplicate colours merged (VEC-08b)
- JPEG noise could leave two palette entries that end up almost the same colour (#020202 and
  #040404). Each flat shape then traced as a patchwork of near-identical pieces: the Korea flag
  JPEG came out with 1,566 nodes. After the palette is finalised, colours closer than a small
  Oklab distance are now merged and the image is relabelled. Korea flag 1,566 → 578 nodes,
  scissors icon 432 → 208, camera emoji 298 → 236; across the 18-image JPEG test set nodes drop
  17% on average and colour accuracy improves slightly. Clean and small inputs are unchanged;
  "exact palette" mode is untouched. One regression test pins it.

### Engine: no more holes eaten into the edge of opaque images (VEC-03)
- Small-region cleanup used to treat the area outside the image as transparency. On fully opaque
  images (scans, JPEGs, photos) that deleted specks along the border and cut notches into the
  edge — a 1-px speck in a corner left a transparent bite, and noisy photos lost up to ~5% of
  their border pixels. Out-of-image neighbours are now ignored, so a speck merges into the colour
  next to it. `vecto trace --check` now measures coverage against the input's opaque pixels, so
  it can no longer pass when pixels were silently deleted. Two regression tests pin it.

## v0.4.2 — 2026-07-19

### Engine: sub-pixel corner relocation + angle snapping
- Validated corners no longer stay pinned to the pixel lattice: each moves to the
  intersection of least-squares lines fitted to its refined flanks (`RelocateCorners`).
  Straight sides fit one shared line from both ends, flank directions within 1° of a
  45° multiple snap exactly, and the AA-contaminated shoulder vertices are projected
  onto the flank lines — so axis-aligned and diagonal sides come out *mathematically*
  straight at the sub-pixel position the anti-aliasing encodes. Junctions stay pinned
  (multi-chain solve still on the roadmap).
- New test: an AA box with sub-pixel borders must trace to exactly 4 lines, exactly
  horizontal/vertical, at the true edge positions. Bench (4 ground-truth files):
  meanΔE −1…−7%, big-error tail (>0.1) −4…−10%, nodes flat to −4%.
- Found + documented in ROADMAP: chamfered-AA corners (ambiguous ~50% border pixels)
  escape k=3 corner detection entirely — a separate detection-side lever.

### Windows packaging: MSIX + portable (packaging/windows/build.ps1)
- One script builds every Windows artifact, version read from Directory.Build.props:
  compressed single-file exe, portable ZIP (app + `vecto` CLI + `portable.txt`),
  self-signed sideload MSIX with its public `.cer`, and — with `-Store` — the
  unsigned Partner Center upload stamped with the `VECTO_STORE_*` identity values.
- `AppxManifest.xml`: `runFullTrust` as the only capability, Open-with file
  association for png/jpg/jpeg/bmp/gif, Store tiles generated from the master logo
  (`scripts/make-msix-assets.ps1`). `certs/` is gitignored (the PFX is a secret).
- **Portable mode**: a `portable.txt` beside `Vecto.exe` moves `settings.json` next
  to the exe; the portable ZIP ships the marker plus a README explaining it.
- `docs/store/SUBMISSION.md` + `LISTING.md`: field-by-field Partner Center copy
  (properties, requirements with measured numbers, runFullTrust justification,
  listing text and keywords). The site gains `/privacy/` — certification requires
  a policy URL for full-trust apps.

### Quick wins (app + CLI)
- **App: PNG export** — Export PNG… renders the traced vector through the Core
  Rasterizer (1×, supersampled) and saves via the WPF PNG encoder.
- **App: Nodes view** — third right-pane mode drawing every path outline plus node
  markers, sized in screen pixels (rebuilt on zoom, clipped to canvas); markers skip
  above 20k nodes to stay responsive.
- **App: settings persistence** — last-used palette/style/detail and window size/state
  restored from `%APPDATA%\Vecto\settings.json`; corrupt/missing files fall back to
  defaults silently.
- **CLI: batch tracing** — `vecto trace` takes multiple inputs and self-expanded
  wildcards (`vecto trace *.png --out-dir out`); `-o`/`--seg-png` stay single-input;
  `--check` failures accumulate to exit 2. `--version` now reports the real assembly
  version (was hardcoded 0.1.0).
- Docs: START_HERE status/priority de-drifted (still said v0.1.0); ROADMAP gains a
  Distribution section (Store/winget/signing).

### Product website (site/)
- Astro landing page + changelog at https://danielmevit.github.io/vecto/ — mirrors the
  Eqho/Laydown site playbook (data-driven sections, version read from
  Directory.Build.props at build time, robots/sitemap/llms.txt, SoftwareApplication
  JSON-LD) in Vecto's own design language (near-black, hairline borders, #0C8CE9).
- Download buttons resolve the latest GitHub Release assets via JS, with the releases
  page as no-JS fallback. Deploys via GitHub Actions (withastro/action) on push to main.

## v0.4.1 — 2026-07-08

### Dark UI theme (Toolcraft-inspired, original XAML)
- New Themes/Theme.xaml: near-black surfaces, hairline low-alpha borders, #0C8CE9 accent,
  Inter typography, custom templates for Button/ComboBox/CheckBox/Slider/ScrollBar/ToolTip,
  thin pill scrollbars, dark title bar via DWM. Layout unchanged (compact).
- Provenance: design-token values follow Pixel Point's Toolcraft (proprietary license);
  zero Toolcraft code copied — see DECISIONS.md. Repo stays clean for open-sourcing.

## v0.4.0 — 2026-07-08

### Perfect circles + straight-run extraction
- **Corner validation**: detected corners are re-measured on the refined sub-pixel
  geometry (where staircase quantization spikes vanish); false corners are demoted and
  the chain reprocessed. Small circles no longer get lattice-pinned dents — rings/dots
  now reach the whole-circle arc fit and come out as exact circles.
- **Arc-before-cubic** on spans ≥16 points; **greedy straight-run extraction** peels
  truly straight prefixes/suffixes off mixed ranges ([line][cap][line] with no corner);
  **collinear line merge** heals sides split by chain seams or max-error splits.
- Tests 14 green (new: rounded-box sides must be exactly 4 single lines; circle radial
  bound tightened to 0.8 px, segment cap 8).
- Resolution finding (bench, 1× vs 2× raster of the same ground truth): mean ΔE and
  wrong-pixel rates halve at 2× — input resolution is the cheapest quality lever.
- Photo sanity check: 4K Windows Bloom wallpaper → 24 colors, 2 251 regions, 54 594
  nodes in 4.5 s end-to-end.

## v0.3.0 — 2026-07-08

### Geometric primitives in the fitter (icons stop wobbling)
- Simplest-model-first fitting per range: straight-chord test (`TryLine`) before the
  cubic, least-squares circle arc (`TryArc`, Kåsa fit) before recursive splitting.
  Straight edges now emit single `L` segments and circles/round caps emit exact arc
  geometry (≤90° cubic pieces, endpoints kept exact — planarity preserved). A closed
  no-corner ring lands in the arc path automatically, so dots/heads become true circles.
- New test: a 30° band across the canvas must produce exactly 2 straight lines for its
  long edges (≤8 segments total); circle radial-error bound tightened 1.6 → 1.2 px.
- Bench: node counts −11…−18% (gt-crisp 508→454, gt-blend 705→579, transparency
  339→303) at flat ΔE; VM logo 118→116 nodes. Known trade documented in ROADMAP:
  chord endpoints pinned to the lattice can tilt accepted lines ~0.5 px — sub-pixel
  junction/corner relocation is the next quality lever.

## v0.2.0 — 2026-07-08

### Ground-truth evaluation harness + sub-pixel quality pass
- **Rasterizer** (Core): renders any VectorDocument to pixels — nonzero scanline fill,
  painter's order, supersampled. **SvgReader** (Cli): parses M/L/H/V/C/Z path SVGs back
  into documents. New CLI commands: `vecto render` (SVG→PNG) and `vecto bench` — the
  round trip *original vector → raster → trace → re-render → per-pixel Oklab ΔE diff*
  with heatmap output; VM's own sample SVGs serve as local ground truth.
- **Sub-pixel edge refinement**: boundary points slide along their normals to the 50%
  coverage crossing of the two region colors (bilinear-sampled; alpha against transparent
  sides). Recovers the edge position anti-aliasing encodes instead of snapping to the
  pixel lattice. Mean ΔE on the 4-logo ground-truth bench: −40…−46% per file.
- Corner-flank smoothing damping (thin tips no longer fatten into lobes) + tighter fit
  tolerances + post-refinement jitter pass (error held, ~20% fewer nodes than without).
- **App fix**: original pane now displays at true pixel size — PNGs with non-96-DPI
  metadata (like VM's samples) made the left pane shrink, breaking the side-by-side scale.

## v0.1.0 — 2026-07-08

### Phase 0 — Bootstrap
- .NET 8 solution: Vecto.Core (engine, dependency-free), Vecto.Cli (`vecto`),
  Vecto.App (WPF, assembly `Vecto`), Vecto.Tests (xUnit).
- Solution-wide props: nullable, implicit usings, TreatWarningsAsErrors, version 0.1.0.
- Git: `main` = releases, `dev` = working branch. `.codegraph/`, `_bench/`, bin/obj ignored.

### Phase 1–2 — Engine + CLI
- Full pipeline (see docs/ai/ALGORITHM_INDEX.md): Oklab weighted k-means++ palette with
  agglomerative auto-merge and edge-excluded sampling; exact palettes for crisp art;
  nearest-color labeling with alpha threshold; small-region absorption; **planar boundary
  graph** — crack-following chains shared between adjacent regions (structurally no
  gaps/overlaps, same property as Vector Magic's output); corner detection with non-max
  suppression; corner-pinned clamped Laplacian smoothing; Schneider least-squares Bezier
  fitting with C1 wrap-around seams; VM-style SVG writer (region paths + holes, 2-decimal
  invariant coords, line collapsing).
- CLI: `trace` (colors/detail/style/polygons/seed/seg-png/stats), `samples` (procedural
  crisp/blended/transparent test images), `--check` planarity self-verification.
- 12 tests: exact planar partition, corner preservation, circle node count + radial error,
  transparency, speckle absorption, exact crisp palettes, curved planarity ≤0.5%,
  determinism, de-DE culture guard.
- Palette display colors average flat pixels only (AA fringes can't tint fills);
  weighted k-means over unique-color histogram (palette stage 534→39 ms on the VM sample).

### Phase 3 — WPF app MVP
- Side-by-side original/vector with synced zoom+pan (Ctrl+wheel, Fit/1:1), right pane
  switchable to segmentation view, live re-trace on setting change (250 ms debounce,
  cancellable), palette swatches with hex tooltips, stage-timing stats.
- Open/drag-drop/paste input; SVG export and copy-to-clipboard; `--smoke` headless self-test;
  crash log to %TEMP%.
- Corner threshold 60°→68° (false corners at staircase slope transitions); benchmark on
  Vector Magic's "Logo With Blending Small": identical palette (#1404ab/#ffffff),
  8 paths vs 8, **96 nodes vs VM's 96**, planarity deviation 0.00%.
