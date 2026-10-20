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

## Next version (v0.4.5) — MUST
- **First-launch onboarding** (owner requirement 2026-10-15, same for Laydown and Eqho). A short, skippable
  welcome on the very first start, plain language a first-time user understands:
  1. What Vecto does: picture in, clean SVG out — best for logos, icons and flat illustrations.
  2. How to start: drop, paste (Ctrl+V) or open a picture.
  3. Style and Detail in one sentence each — "Not sure? Keep Auto and Medium."
  4. Export SVG / Copy SVG.
  5. What doesn't trace well (gradients, small text, small pictures, photos) — the same message as the tips.
  "Don't show again" + reopen any time from Help. Remembered in settings. No pop-up chain: one window, a few steps.
- **One window only (single instance)** (owner 2026-10-16): clicking "Open" in the Microsoft Store (or the Start
  menu) while the app is already running opened a SECOND window. A second launch must hand over to the running
  window (bring it to the front; pass along a picture it was asked to open) and then exit.

## v0.4.6 — the app in many languages (owner 2026-10-16)
- **Interface languages:** move every UI text (window, menus, buttons, tips, onboarding, messages, installer/Store
  strings) into translation files; follow the Windows display language, with a language picker in onboarding and
  settings. Same plan as Eqho and Laydown/Vecto, shared word list where possible.
- Wave 1 languages: **main** — English, German, Spanish, French, Italian, Portuguese, Dutch, Polish, Russian, Ukrainian, Turkish, Japanese, Chinese (Simplified), Korean; **Balkan** — Macedonian, Serbian (Latin only), Croatian, Slovenian, Bulgarian, Albanian, Romanian, Greek (no separate Bosnian: Serbian and Croatian cover it); **Central Europe** — Czech, Slovak. Later waves: as many more as quality allows (owner: "as huge as it can be").
- Translations by AI workers first, native-speaker review welcome; right-to-left languages only after the layout is
  checked for them. Store listings can be localized per language in Partner Center afterwards.

## Milestone v0.4.4 (shipped 2026-10-15)
Vecto stays in beta: every release is 0.4.x.

### M1 — Housekeeping
- [x] **App icon on a solid plate** (owner request 2026-10-13, owner design): the current icon is transparent (pixel "V" +
  thin blue outline strokes) and gets lost on dark taskbars and on blue/accent Start tiles. Put it on a solid rounded
  plate (keep the pixel-V + vector-outline idea), regenerate EVERY size: `assets/vecto.ico` (16-256 px multi-size),
  `assets/msix/*` (StoreLogo, Square44x44 incl. targetsize/unplated variants, Square150x150, Wide if added),
  `assets/Vecto_Logo_1024x1024.png`, site favicon. Check contrast on light, dark and accent backgrounds at 16/24/32 px.
  Done 2026-10-15: navy plate (#0f1f45) with the white pixel-V, chosen by the owner from real-context mockups
  (store-images/icon-options); larger V at 16-32 px; brand logo `Vecto_Logo_1024x1024.png` stays transparent, app
  icon master is `assets/Vecto_Icon_1024x1024.png`.
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
- [x] **Fork tuning values check** (Archers007/vecto, Detail High: fit tolerance 0.09 -> 0.0225, minimum region area
  3 -> 2). Measured 2026-10-15 at Detail High: wrong pixels drop (main clean 0.38% -> 0.20%, realistic 1.41% -> 1.23%)
  but nodes grow +22% to +83% (main jpeg 240 -> 384, realistic 1482 -> 2203, Mona Lisa 84k -> 154k) and
  flag-japan__small gets worse. Not adopted as the default (rule: no more than +15% nodes). Possible later: an optional
  "Detail: Maximum" level using these values, for users who want the closest match and accept bigger files.

### M3 — In-app tips
- [x] Gentle tip when an image has smooth gradients (they trace as bands of colour).
- [x] Tip when an image is small (around 128 px or less): suggest the largest version available.
  (VEC-46: status-bar tip; gradient check fires on the gradient logos and on none of the flat bench images)

## Later 0.4.x
- **CAD / line-art centerline mode** (GitHub issue #1) — **OPEN as a whole goal**: individual stages are shipped
  to `dev` (unreleased), but classification and real drawing geometry quality are not complete. Stages so far,
  each gated (verify PASS + cross-vendor review APPROVE): Stage 1 line-art scan mode (VEC-07c,
  Sauvola front-end, recall/precision 0.97/0.97); Stage 2 centerline strokes (VEC-50, single weighted paths, text/
  hatching stay filled); Stage 3 clean primitives (VEC-52: corner-split TLS lines → one 2-anchor path per straight
  run, Kåsa arcs, junction weld, intersection snap); angle rationalization (VEC-52d: parallel clustering, 15° grid,
  whole degrees); huge-sheet scaling (VEC-53, 14,444×9,632 sheet, 34.5 s measured 2026-10-19); PDF input/export (VEC-47/48b/49) and
  DXF R12 export (VEC-54). See `docs/ai/reviews/2026-10-19-cad-pdf-handoff.md` and CHANGELOG "Unreleased".
  **Remaining — bounded architecture/evaluation reassessment first** (before another broad classification patch):
  original-scan role/geometry references, automatic versus oracle-text-mask prototypes, meaningful coherent
  primitives, and explicit source-ink ownership. The existing two-anchor fitting, angle rationalization and
  exporters remain useful; no architecture winner, automatic text solution or completion estimate is established.
  Required outcomes: hatches/linework as coherent two-anchor strokes, true glyphs filled and legible, no fragment
  explosion, justified circle/arc intent (the 1913 pump sheet: 369 strokes vs 7,079 filled paths, 4.95% by the
  original path denominator — full-sheet majority remains unachieved as a quality-gated deliverable). Also:
  app-side PDF page picker; steam-engine render for the showcase; perf watch beyond 14k-px sheets. Keeping real
  glyphs filled and legible is mandatory; the semantic text/graphics discrimination needed for CAD quality is
  current work. Deskew and OCR/editable-text recognition stay optional/later.
- **App-owned 24-hour Windows trial enforcement** (owner-reported 2026-10-19): the Store-configured one-day trial
  reportedly still allows use after 24 hours (owner observation, not an independently diagnosed platform defect).
  The app must enforce expiry itself — at launch, during a session that crosses the deadline, and on resume — while
  preserving paid entitlements. Scoped design still needed: trial start event, authoritative time source, persisted
  state scope, offline policy, clock rollback/timezone, reinstall/state-reset, practical tamper limits. No hosted
  service, deployment or account collection assumed; nothing implemented or released.
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
- **winget listing** (owner 2026-10-17, like Eqho's `damt.Eqho`): new-package PR to microsoft/winget-pkgs as
  `damt.Vecto` (IDs are case-insensitive, so `winget install damt.vecto` works). Needs a public, winget-installable
  installer: check what `Vecto-<ver>.exe` is (Inno/NSIS installer → silent switches; or ship the ZIP as
  `zip` + `portable`), validate with `winget validate`, test-install, then submit after the next release. Each
  later version = a small update PR (can be automated in CI).
- Demo GIF for README/site
- Code signing (SmartScreen) — costs money, decide separately

## Phase 5 — Cross-platform (Linux + macOS)
- Quick win: publish `vecto` CLI for linux-x64 / osx-x64 / osx-arm64 (Core+Cli are already
  cross-platform; three `dotnet publish -r` commands) and attach to Releases
- Port the app WPF → Avalonia UI (single codebase for Win/Linux/macOS; Core reused 100%,
  ViewModel ~90%, theme translates style-for-style). MAUI ruled out — no Linux support
- GitHub Actions release matrix (build all targets on tag push); macOS needs notarization
  (Apple dev account) to avoid Gatekeeper friction

## Phase 6 — Power features
- Exporters: ~~PDF~~ **done on `dev`** (VEC-49, hand-written zero-dependency writer), ~~DXF~~ **done on `dev`** (VEC-54,
  hand-written ASCII R12 — filled regions/text export as unfilled closed contours); EPS, AI remain (mirror VM's
  formats.dll split as Vecto.Formats)
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

## Later — to iron out (owner 2026-10-17)
- **Guided first-run tour** (owner 2026-10-17, to iron out later): the onboarding opens as a popup first, then
  points at the app's main controls one by one ("this is where…") — same approach in Eqho, Vecto and Laydown.
