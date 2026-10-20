# STATUS — Vecto (snapshot 2026-10-20)

Point-in-time status for any agent picking up Vecto. Living orientation is in
`START_HERE.md`; this file is "where things stand right now."

## Shipped / live
- **v0.4.4 is the current release** (2026-10-15: navy app icon, in-app tips, light tints
  kept, AGPL-3.0 relicense — see CHANGELOG). v0.4.3 (tracing quality round) was the
  previous release. GitHub Releases carry app + CLI + MSIX + cross-platform CLI binaries.
- **Live on the Microsoft Store**: https://apps.microsoft.com/detail/9PM61P4NC8J2 — **$7.99**
  one-time. Store ID `9PM61P4NC8J2`.
- Also **free and open source (AGPL-3.0-only from v0.4.4; v0.4.3 and earlier MIT)** — the same app is downloadable free from GitHub Releases, and the source builds it. The Store purchase buys convenience: one-click, auto-updating, signed install, and it supports development.
- **Product site is Store-first**, live at https://danielmevit.github.io/vecto/ (deploys from `main` via `.github/workflows/deploy.yml`). The site is the **github.io** URL — `vecto.app` is an unrelated parking page, not this project.
- **Dev-only shipped stages (merged to `dev`, NOT released; accepted `dev` base is 5ebe747,
  DXF R12 and CLI stroke stats merged)**: the whole CAD/PDF/DXF arc —
  Line art scan mode (Sauvola), CAD centerline strokes (Stage 2), clean primitives + angle
  rationalization (Stage 3), huge-sheet scaling, PDF input (CLI + app), PDF export, and DXF
  R12 export. See CHANGELOG "Unreleased" and
  `docs/ai/reviews/2026-10-19-cad-pdf-handoff.md`. No release beyond v0.4.4 exists or is
  claimed.

## Next
- **CAD architecture reassessment — current engine priority (issue #1 stays OPEN as a whole
  goal)**: before another broad classification patch, a bounded architecture/evaluation pass
  is needed — original-scan role/geometry references, automatic versus oracle-text-mask
  prototypes, meaningful coherent primitives, and explicit source-ink ownership. The existing
  two-anchor fitting, angle rationalization and exporters remain useful. No architecture
  winner, automatic text solution or implementation completion estimate is established.
  Required outcomes: hatches/linework as coherent two-anchor strokes, true glyphs filled and
  legible, no fragment explosion, justified circle/arc intent. Full-sheet majority stroke
  recovery remains unachieved as a quality-gated deliverable (the 1913 pump sheet traces 369
  strokes vs 7,079 filled paths, 4.95% by the original 7,448-path denominator). The latest
  classification candidate (f814a98) remains unmerged and failed semantic acceptance despite
  211 tests; accepted dev has 193 tests — candidate counts are not delivered improvement.
- **In-app "rate on the Store" prompt** — link `ms-windows-store://review/?productid=9PM61P4NC8J2`, shown once after a couple of successful traces (never on first launch). Highest-leverage post-launch item; not built yet.
- **App-owned 24-hour trial enforcement** — see the trial section below.
- Engine: sub-pixel junction/corner relaxation (top quality lever — see ROADMAP); Phase 4 UX (palette editor, tracing wizard).

## Owner decisions pending / in flight
- **`TraceDiagnostics.StrokeCount` in CLI `--stats` — shipped to `dev`** (VEC-57, merged in
  558a0a7, in accepted dev 5ebe747): `--stats` now displays `strokes={StrokeCount}` alongside
  chains, regions, nodes and palette size. The original diagnostic property is now displayed;
  no held or prepared state remains. Unreleased (dev-only, like the rest of the CAD arc).
- **Secure TIFF**: scope requested and delivered; the actual hardening (decode bounds,
  first-page limits, preflight, allocator caps) has **not shipped**. No claim of general
  security or no-vulnerabilities is warranted.
- **`Centerline.Enabled`** master switch: test-only escape hatch, unchanged — no removal
  or exposure authorized.
- **TIFF today**: the CLI already decodes TIFF via the pinned ImageSharp 4.1.3
  (`ImageIo.Load` → `Image.Load`); any "TIFF is disabled" claim is stale. The CLI
  (ImageSharp) and the app (WPF WIC codecs) are separate decode paths; proposed CLI
  hardening does not describe the app.

## Known issues
- The **2026-10-07 build break** (five unpatched ImageSharp 3.1.x advisories via a floating version) is **resolved** (2026-10-12, VEC-01): the CLI pins ImageSharp 4.1.3 with the free community key committed at `Vecto.Cli/sixlabors.lic`; the weekly CI audit catches future advisories.
- The **v0.4.1** portable ZIP on GitHub Releases is broken: a case-insensitive filename collision shipped the CLI (`vecto.exe`) in place of the app. Fixed in **v0.4.2** (CLI now lives in `cli\`). The v0.4.1 release asset itself is unchanged.
- Engine limits (ROADMAP): junctions where 3+ regions meet still pin to the pixel lattice; chamfered-AA corners can escape k=3 corner detection.
- **CAD classification gap**: hatch grids and dense scans stay filled; the response is the
  bounded architecture reassessment above (see Next) — the main remaining quality gap of the
  CAD arc.
- **Bench/validation trap**: `SvgReader` skips `fill="none"` stroked paths, so round trips
  through it are unreliable for stroke fidelity — use `Rasterizer.Render` directly or a
  faithful independent SVG renderer (details in ALGORITHM_INDEX.md).

## App-owned 24-hour trial enforcement (backlog need, owner-reported 2026-10-19)
The owner reports that the Store-configured one-day trial still allows use after 24 hours.
This is an owner observation, not an independently diagnosed Store-platform defect. The app
must enforce the intended trial period itself: check expiry at launch, during a session that
crosses the deadline, and on resume from sleep, while preserving legitimate paid/commercial
entitlements. A scoped design is still needed — trial start event, authoritative time
source, persisted state scope, offline policy, clock rollback/timezone changes, reinstall
and state-reset handling, and practical (not unbreakable) tamper limits. No hosted service,
deployment or account collection is assumed; nothing is implemented or released, and no
current Store policy/price/status is asserted beyond the owner report.

## Store packaging (for future updates)
- `packaging/windows/build.ps1 -Store` builds the unsigned Store MSIX (the Store signs it). Identity values live in `docs/store/SUBMISSION.md` (public — they are embedded in every published package).
- Listing copy: `docs/store/LISTING.md`. Config / requirements: `docs/store/SUBMISSION.md`.
- Reserved listing name: **"Vecto - Image to Vector"** ("Vecto - Image to SVG" is also reserved on the same product).
- The site's free-vs-paid presentation is gated by `const storeLive` in `site/src/pages/index.astro` — currently `true` (Store-first). Set `false` to revert to the free-download-first layout.

## Release mechanics (see also GOTCHAS.md + CHANGELOG.md)
- Version lives in `Directory.Build.props` — bump it there; `build.ps1` reads it. Run `build.ps1` from **Windows PowerShell, not WSL**. Cross-platform CLI: `dotnet publish Vecto.Cli -r <rid>` separately.
- A "failed" Pages deploy is often a job **cancelled while queued** for a runner (empty step list) — just re-run: `gh workflow run deploy.yml --repo danielmevit/vecto --ref main`. Verify the live flip by page content, not HTTP status.
