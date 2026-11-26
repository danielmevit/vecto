import type { APIRoute } from 'astro';
import { REPO_URL, RELEASES_URL, STORE_URL } from '../lib/site';

// Generated so the Store link comes from the single STORE_URL constant.
const body = `# Vecto

> Open-source bitmap-to-vector (PNG/JPG → SVG) tracing app for Windows, with a cross-platform command-line tool. Its planar-partition engine fits every boundary once and shares it between adjacent shapes, so the output has no hairline gaps and no overlaps — the defect that plagues most image vectorizers.

- Site: https://vecto.damt.xyz/
- Get it: Microsoft Store, ${STORE_URL}
- Price: a one-time purchase after a 15-day free trial. No subscription, no ads.
- Command-line tool for Linux and macOS: ${RELEASES_URL}
- Source code: ${REPO_URL} (AGPL-3.0 from v0.4.4; v0.4.3 and earlier were MIT)
- Support: support@damt.xyz
- Author: Daniel Mevit (https://damt.xyz)

## Tech Stack
* Language: C# / .NET 8
* Engine: Vecto.Core — zero-dependency planar vectorization pipeline (Oklab weighted k-means++ palette inference, crack-following shared boundary graph, corner detection with sub-pixel validation, clamped smoothing, sub-pixel edge refinement from anti-aliasing, simplest-model-first fitting: line → circular arc → least-squares Bezier)
* App: WPF, Windows 10/11 (64-bit); CLI: Windows, Linux, macOS

## Key Features & Differentiators
* **Planar shared-boundary tracing:** every boundary between two shapes is fitted once and used by both sides — gaps and overlaps are structurally impossible, the same property Vector Magic's output has and stacked-layer tracers (potrace, Inkscape) don't.
* **Geometric intelligence:** straight edges become true straight lines; circles, dots and round caps become mathematically exact arcs.
* **Sub-pixel precision:** anti-aliasing is read to place boundaries between pixels instead of snapping to the pixel grid.
* **Live side-by-side compare:** synced zoom/pan, segmentation view, palette swatches, instant re-trace on every setting change.
* **Deterministic and tested:** same input → byte-identical SVG; geometric-invariant test suite; round-trip quality benchmark (\`vecto bench\`).
* **Private:** no account, no upload, runs fully offline.

## Install & Run from Source
\`\`\`bash
git clone https://github.com/danielmevit/vecto.git
cd vecto
dotnet build -c Release
\`\`\`
`;

export const GET: APIRoute = () =>
  new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
