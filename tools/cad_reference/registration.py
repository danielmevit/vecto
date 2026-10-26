"""Pixel <-> millimetre registration for one case.

The recovered geometry (strokes, filled regions, raster images) lives in the
input raster's pixel frame; truth lives in page millimetres (y-down). Every
recovered coordinate and width goes through ONE `Registration`, so strokes,
regions and widths always share units (the original grader fitted filled
regions in pixels against millimetre truth).

Archive cases declare `transform: "px = mm * dpi / 25.4"` (CairoSVG renders the
mm-sized SVG at the float viewport; the integer surface only rounds the canvas
size). That declared transform is used. The alternative a renderer could apply,
preserveAspectRatio="xMidYMid meet" against the ROUNDED pixel surface, is also
computed and its maximum disagreement over the page is recorded; validation
fails if it exceeds `max_alt_disagreement_px`, because then the case metadata
would be too ambiguous to grade at the published tolerances. Cases without a
declared transform fall back to the meet transform and are flagged `inferred`.
"""

MAX_ALT_DISAGREEMENT_PX = 0.5


class RegistrationError(ValueError):
    pass


class Registration:
    def __init__(self, sx, sy, ox=0.0, oy=0.0, method="declared", record=None):
        if sx <= 0 or sy <= 0:
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
    def from_case(cls, case):
        pw, ph = case["page_mm"]
        W, H = case["pixel_size"]
        meet = min(W / pw, H / ph)
        mox, moy = (W - pw * meet) / 2.0, (H - ph * meet) / 2.0
        declared = case.get("transform", "")
        rec = {"page_mm": [pw, ph], "pixel_size": [W, H],
               "declared_transform": declared or None}
        if "dpi" in case and "mm * dpi / 25.4" in declared.replace("  ", " "):
            s = case["dpi"] / 25.4
            reg = cls(s, s, 0.0, 0.0, method="declared_dpi", record=rec)
            worst = 0.0
            for corner in ((0, 0), (pw, 0), (0, ph), (pw, ph), (pw / 2, ph / 2)):
                a = reg.to_px(corner)
                b = (corner[0] * meet + mox, corner[1] * meet + moy)
                worst = max(worst, abs(a[0] - b[0]), abs(a[1] - b[1]))
            rec.update({"px_per_mm": s, "alternative": "preserveAspectRatio xMidYMid meet on rounded surface",
                        "alt_px_per_mm": meet, "alt_offset_px": [mox, moy],
                        "max_alt_disagreement_px": round(worst, 5),
                        "inferred": False})
            if worst > MAX_ALT_DISAGREEMENT_PX:
                raise RegistrationError(f"{case.get('case_id')}: declared and meet transforms disagree by {worst:.3f}px")
            if "mm_per_px" in case:
                # archive cases store mm_per_px = page_width_mm / pixel_width (rounded
                # surface), which is NOT the declared dpi transform; record the gap.
                gap = max(abs(case["mm_per_px"] * s - 1.0) * pw * s, abs(case["mm_per_px"] * s - 1.0) * ph * s)
                rec["archive_mm_per_px"] = case["mm_per_px"]
                rec["archive_mm_per_px_max_disagreement_px"] = round(gap, 5)
                if gap > MAX_ALT_DISAGREEMENT_PX:
                    raise RegistrationError(f"{case.get('case_id')}: archive mm_per_px disagrees by {gap:.3f}px")
            reg.record = rec
            return reg
        rec.update({"px_per_mm": meet, "offset_px": [mox, moy], "inferred": True,
                    "note": "no declared transform: inferred preserveAspectRatio meet"})
        return cls(meet, meet, mox, moy, method="inferred_meet", record=rec)
