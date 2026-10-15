# Roadmap — Vecto

## Done
- **Phase 0** — bootstrap (solution, playbook docs, CodeGraph, git dev/main)
- **Phase 1–2** — engine (planar boundary graph + Bezier fitting), CLI, 12 tests,
  VM-sample parity (96 nodes vs 96, identical palette)
- **Phase 3** — WPF app MVP (side-by-side compare, segmentation view, live re-trace,
  export/copy SVG)
- **v0.4.1 release + site** — first public GitHub Release (5 binaries); product site
  (`site/`, Astro → GitHub Pages); quick wins 2026-07-19: PNG export, nodes view,
  settings persistence, CLI batch mode

## Next milestone (v0.4.4)
Vecto stays in beta: every release is 0.4.x.

### M1 — Housekeeping
- **App icon on a solid plate** (owner request 2026-10-13, owner design): the current icon is transparent (pixel "V" +
  thin blue outline strokes) and gets lost on dark taskbars and on blue/accent Start tiles. Put it on a solid rounded
  plate (keep the pixel-V + vector-outline idea), regenerate EVERY size: `assets/vecto.ico` (16-256 px multi-size),
  `assets/msix/*` (StoreLogo, Square44x44 incl. targetsize/unplated variants, Square150x150, Wide if added),
  `assets/Vecto_Logo_1024x1024.png`, site favicon. Check contrast on light, dark and accent backgrounds at 16/24/32 px.
- [x] **Relicense to AGPL-3.0-only** (owner decision 2026-10-14, dual licensing; v0.4.3 and earlier stay MIT) — done
  in 0.4.4, see docs/ai/DECISIONS.md and NOTICE.

### M2 — Tracing fixes
- [x] **Near-identical light tints** (cream #fff6e8 and white became one near-white colour on the Store cat, pale blue
  #f1f6ff clouds likewise) — large flat light fills keep their own colour; small specks of a kept tint are absorbed
  (VEC-44 + VEC-45).
- **Exact ellipses** (and better small rounded shapes): circles are fitted exactly, but ellipses fall back to Bezier
  approximations that wobble or flatten at 512 px inputs (cat eyes/cheeks/nose in the Store art); add an ellipse
  primitive like the circle fit, and check small rounded-triangle shapes.
- **Partly hidden circles** (a sun behind waves, 512 px) trace as visible polygons instead of arcs — the circle fit
  must also cover open arcs between junctions.
- **Fork tuning values check** (Archers007/vecto, Detail High: fit tolerance 0.09 -> 0.0225 and minimum region area
  3 -> 2) — VEC-44 started the bench but was cut off by the usage limit; values unchanged until measured.

### M3 — In-app tips
- Gentle tip when an image has smooth gradients (they trace as bands of colour).
- Tip when an image is small (around 128 px or less): suggest the largest version available.

## Later 0.4.x
- **CAD / line-art centerline mode** (GitHub issue #1) — constant-width strokes as paths with a stroke weight; see
  docs/ai/research/2026-10-12-cad-mode-research.md.
- **Small text in JPEGs** (thin letters deform, faint captions vanish) — text-aware tracing.
- **JPEG tile patterns** (pattern-tiles-style JPEGs): noisy smoothing per chain (fewer passes on curved chains) to
  recover the last realistic regression vs 0.4.2.
- **Gradient fills** — detect smooth gradients and emit SVG gradients instead of colour bands.

## Phase 4 — VM-fashion UX
- Wizard flow: fully-automatic vs basic (image type → detail → colors) vs advanced
- Palette editor: merge/pick/lock colors, custom palette (engine: PaletteMode.FixedColors)
- Background removal (flood from borders + color similarity, like VM 1.13)
- ~~Wireframe/nodes view (third right-pane mode)~~ **done 2026-07-19**
- ~~Batch mode in CLI (`vecto trace *.png --out-dir`)~~ **done 2026-07-19**

## Distribution
- Microsoft Store first publish — ~~MSIX packaging~~ **built 2026-07-19**
  (`packaging/windows/build.ps1`: exe/ZIP/sideload-MSIX/Store-MSIX; `docs/store/` has
  the full Partner Center copy). Remaining: register the developer account (~$19,
  individual), reserve "Vecto", paste the 3 Product-identity values → `-Store` build →
  upload (SUBMISSION.md walks every field)
- ~~Portable Windows build~~ **done 2026-07-19** (`portable.txt` beside the exe keeps
  settings there; ZIP ships it)
- winget manifest (`winget install vecto`); demo GIF for README/site
- Code signing (SmartScreen) — costs money, decide separately

## Phase 5 — Cross-platform (Linux + macOS)
- Quick win: publish `vecto` CLI for linux-x64 / osx-x64 / osx-arm64 (Core+Cli are already
  cross-platform; three `dotnet publish -r` commands) and attach to Releases
- Port the app WPF → Avalonia UI (single codebase for Win/Linux/macOS; Core reused 100%,
  ViewModel ~90%, theme translates style-for-style). MAUI ruled out — no Linux support
- GitHub Actions release matrix (build all targets on tag push); macOS needs notarization
  (Apple dev account) to avoid Gatekeeper friction

## Phase 6 — Power features
- Exporters: PDF, EPS, DXF, AI (mirror VM's formats.dll split as Vecto.Formats)
- Segmentation editor: pencil/zap/fill-gaps (VM's edit-result tools)
- ~~PNG/bitmap export of the vector render~~ **done 2026-07-19** (1×; scale picker later)
- Copy vector to clipboard for design apps (SVG clipboard format works for Figma/Inkscape)

## Tuning backlog (quality, engine)
- ~~Sub-pixel edge placement from AA gradients~~ **done 2026-07-08** (`SubpixelRefine`;
  mean ΔE −40% across the ground-truth bench)
- ~~Line/arc primitive recognition~~ **done 2026-07-08** (`TryLine`/`TryArc` in the
  fitter: straight edges → single lines, circles/caps → exact arcs; band test enforces it)
- ~~Sub-pixel **corner** relocation + angle snapping~~ **done 2026-07-19** (flank-line
  intersection with exact 45°-multiple snap; bench meanΔE −1…−7%, >0.1 tail −4…−10%).
  **Junction relocation remains** — chain endpoints still pin to the lattice; needs a
  consistent multi-chain solve (all chains meeting at the junction must agree).
- Chamfered-AA corner detection: a 90° corner whose ~50%-coverage border pixels label
  ambiguously gets chamfered in the raster, and k=3/68° detection misses it → the corner
  rounds. Needs re-detection on refined geometry or multi-scale support (found 2026-07-19).
- Node economy: ~1.4–1.8× Vector Magic's counts at equal detail — smarter tangent
  estimation or two-pass fitting could close the rest
- Centerline/stroke recognition (constant-width shapes → path + stroke weight + round
  caps) — the "Tier 3" mode; big lift, transforms icon output quality
- Gentle-slope staircases: constrained line detection would beat Laplacian+fit on
  near-horizontal edges
- `MinRegionArea` scaling with image resolution; photo-mode color count heuristics
- Parallelize labeling/k-means further if large photos feel slow
