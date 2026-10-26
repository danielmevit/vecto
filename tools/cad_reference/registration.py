"""Pixel <-> millimetre registration for one case.

The recovered geometry (strokes, filled regions, raster images) lives in the
input raster's pixel frame; truth lives in page millimetres (y-down). Every
recovered coordinate and width goes through ONE `Registration`, so strokes,
regions and widths always share units (the original grader fitted filled
regions in pixels against millimetre truth).

Archived CairoSVG 2.9.1 cases use the rounded output viewport's default
xMidYMid meet transform, verified against the reference SVG. Nominal DPI and
legacy textual descriptions are provenance, not registration evidence.
Generic nominal transforms are declarations only, never renderer verification.
"""

import math
import re
import xml.etree.ElementTree as ET


class RegistrationError(ValueError):
    pass


class Registration:
    def __init__(self, sx, sy, ox=0.0, oy=0.0, method="declared", record=None):
        if not all(math.isfinite(v) for v in (sx, sy, ox, oy)) or sx <= 0 or sy <= 0:
            raise RegistrationError("non-positive scale")
        self.sx, self.sy, self.ox, self.oy = sx, sy, ox, oy   # px per mm, px offset
        self.method = method
        self.record = record or {}

    @property
    def uniform(self):
        return abs(self.sx - self.sy) <= 1e-9 * max(self.sx, self.sy)

    @property
    def mm_per_px(self):
        # widths are isotropic; only meaningful for a uniform transform
        return 2.0 / (self.sx + self.sy)

    def to_mm(self, p):
        return ((p[0] - self.ox) / self.sx, (p[1] - self.oy) / self.sy)

    def len_mm(self, v_px):
        return v_px * self.mm_per_px

    def to_px(self, p):
        return (p[0] * self.sx + self.ox, p[1] * self.sy + self.oy)

    def seg_to_mm(self, seg):
        return {k: self.to_mm(seg[k]) for k in ("p0", "p1", "p2", "p3")}

    @classmethod
    def unit(cls):
        return cls(1.0, 1.0, method="unit")

    @classmethod
    def from_case(cls, case, reference_svg=None):
        pw, ph = case["page_mm"]
        W, H = case["pixel_size"]
        if not all(math.isfinite(v) and v > 0 for v in (pw, ph, W, H)):
            raise RegistrationError("invalid page or pixel dimensions")
        meet = min(W / pw, H / ph)
        mox, moy = (W - pw * meet) / 2.0, (H - ph * meet) / 2.0
        declared = case.get("transform", "")
        rec = {"page_mm": [pw, ph], "pixel_size": [W, H],
               "declared_transform": declared or None,
               "nominal_dpi": case.get("dpi"),
               "archive_mm_per_px": case.get("mm_per_px")}
        renderer = case.get("renderer")
        if renderer is not None:
            if (renderer.get("tool") != "cairosvg" or renderer.get("version") != "2.9.1"
                    or renderer.get("output_width") != W or renderer.get("output_height") != H):
                raise RegistrationError("unsupported renderer/version or conflicting output dimensions")
            if reference_svg is None:
                raise RegistrationError("CairoSVG registration requires reference SVG viewport evidence")
            root = ET.parse(reference_svg).getroot()
            try:
                vb = [float(v) for v in re.split(r"[ ,]+", root.attrib["viewBox"].strip())]
                width = float(root.attrib["width"].removesuffix("mm"))
                height = float(root.attrib["height"].removesuffix("mm"))
            except (KeyError, ValueError) as exc:
                raise RegistrationError("unsupported SVG viewport") from exc
            if (root.tag != "{http://www.w3.org/2000/svg}svg" or vb != [0, 0, pw, ph]
                    or not root.attrib["width"].endswith("mm")
                    or not root.attrib["height"].endswith("mm")
                    or (width, height) != (pw, ph)
                    or root.get("preserveAspectRatio", "xMidYMid meet").strip() != "xMidYMid meet"
                    or "transform" in root.attrib):
                raise RegistrationError("unsupported SVG viewport, aspect ratio or root transform")
            s, ox, oy, method = meet, mox, moy, "cairosvg_2.9.1_rounded_viewport_meet"
            rec.update({"renderer": dict(renderer), "viewbox": vb,
                        "renderer_verified": True, "inferred": False})
        elif declared == "px = mm * dpi / 25.4":
            s = case["dpi"] / 25.4
            ox, oy, method = 0.0, 0.0, "declared_dpi"
            rec.update({"renderer_verified": False, "inferred": False,
                        "note": "explicit nominal declaration; actual renderer not verified"})
        elif declared:
            raise RegistrationError("unsupported explicit transform without known renderer evidence")
        else:
            raise RegistrationError("ambiguous registration: no explicit transform or known renderer")
        rec.update({"px_per_mm": s, "offset_px": [ox, oy]})
        if "dpi" in case:
            nominal = case["dpi"] / 25.4
            rec["nominal_dpi_max_disagreement_px"] = max(
                abs(x * (s - nominal) + ox) for x in (0, pw))
            rec["nominal_dpi_max_disagreement_px"] = max(
                rec["nominal_dpi_max_disagreement_px"],
                *(abs(y * (s - nominal) + oy) for y in (0, ph)))
        return cls(s, s, ox, oy, method=method, record=rec)
