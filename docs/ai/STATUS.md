# STATUS — Vecto (snapshot 2026-07-26)

Point-in-time status for any agent picking up Vecto. Living orientation is in
`START_HERE.md`; this file is "where things stand right now."

## Shipped / live
- **v0.4.2** is the current release (GitHub Releases: app + CLI + MSIX + cross-platform CLI binaries).
- **v0.4.3** prepared on `dev` 2026-10-13 (tracing quality round, see CHANGELOG); release pending owner go.
- **Live on the Microsoft Store**: https://apps.microsoft.com/detail/9PM61P4NC8J2 — **$7.99** one-time (no subscription, no trial). Store ID `9PM61P4NC8J2`.
- Also **free and open source (AGPL-3.0-only from v0.4.4; v0.4.3 and earlier MIT)** — the same app is downloadable free from GitHub Releases, and the source builds it. The Store purchase buys convenience: one-click, auto-updating, signed install, and it supports development.
- **Product site is Store-first**, live at https://danielmevit.github.io/vecto/ (deploys from `main` via `.github/workflows/deploy.yml`). The site is the **github.io** URL — `vecto.app` is an unrelated parking page, not this project.

## Store packaging (for future updates)
- `packaging/windows/build.ps1 -Store` builds the unsigned Store MSIX (the Store signs it). Identity values live in `docs/store/SUBMISSION.md` (public — they are embedded in every published package).
- Listing copy: `docs/store/LISTING.md`. Config / requirements: `docs/store/SUBMISSION.md`.
- Reserved listing name: **"Vecto - Image to Vector"** ("Vecto - Image to SVG" is also reserved on the same product).
- The site's free-vs-paid presentation is gated by `const storeLive` in `site/src/pages/index.astro` — currently `true` (Store-first). Set `false` to revert to the free-download-first layout.

## Next
- **In-app "rate on the Store" prompt** — link `ms-windows-store://review/?productid=9PM61P4NC8J2`, shown once after a couple of successful traces (never on first launch). Highest-leverage post-launch item; not built yet.
- Engine: sub-pixel junction/corner relaxation (top quality lever — see ROADMAP); Phase 4 UX (palette editor, tracing wizard).

## Known issues
- The **2026-10-07 build break** (five unpatched ImageSharp 3.1.x advisories via a floating version) is **resolved** (2026-10-12, VEC-01): the CLI pins ImageSharp 4.1.3 with the free community key committed at `Vecto.Cli/sixlabors.lic`; the weekly CI audit catches future advisories.
- The **v0.4.1** portable ZIP on GitHub Releases is broken: a case-insensitive filename collision shipped the CLI (`vecto.exe`) in place of the app. Fixed in **v0.4.2** (CLI now lives in `cli\`). The v0.4.1 release asset itself is unchanged.
- Engine limits (ROADMAP): junctions where 3+ regions meet still pin to the pixel lattice; chamfered-AA corners can escape k=3 corner detection.

## Release mechanics (see also GOTCHAS.md + CHANGELOG.md)
- Version lives in `Directory.Build.props` — bump it there; `build.ps1` reads it. Run `build.ps1` from **Windows PowerShell, not WSL**. Cross-platform CLI: `dotnet publish Vecto.Cli -r <rid>` separately.
- A "failed" Pages deploy is often a job **cancelled while queued** for a runner (empty step list) — just re-run: `gh workflow run deploy.yml --repo danielmevit/vecto --ref main`. Verify the live flip by page content, not HTTP status.
