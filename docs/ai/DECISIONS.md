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
