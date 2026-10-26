import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from cad_reference import controls as C  # noqa: E402
from cad_reference import dxf, pngio  # noqa: E402
from cad_reference.geometry import bulge_arc, seg_is_finite_line  # noqa: E402
from cad_reference.grade import image_measures, score_case  # noqa: E402
from cad_reference.registration import Registration, RegistrationError  # noqa: E402


def grade(name):
    truth, geo, _exp = C.controls()[name]
    return score_case(truth, geo, C.CASE)


def outcome(res, key):
    m = res["metrics"]
    if key in m:
        num, den = m[key]["numerator"], m[key]["denominator"]
        return m[key]["value"] if key == "geometry_precision" else num
    if key == "merge_flags":
        return res["counts"]["merge_flags"]
    if key == "unsupported_curved":
        return res["counts"]["unsupported_curved_strokes"]
    if key == "form":
        return res["lines"][0]["form"]
    if key == "tangent_recovered":
        return int(res["connections"][0]["recovered"])
    if key == "tangent_connected_presence":
        return int(res["connections"][0]["connected_presence"])
    raise KeyError(key)


class ControlExpectations(unittest.TestCase):
    """Every independently built control produces its stated outcome."""

    def test_all_controls(self):
        for name, (_t, _g, exp) in C.controls().items():
            res = grade(name)
            for key, want in exp.items():
                with self.subTest(control=name, key=key):
                    self.assertEqual(outcome(res, key), want)


class LineContracts(unittest.TestCase):
    def test_reversal_reports_small_endpoint_error(self):
        row = grade("line_reversed")["lines"][0]
        self.assertTrue(row["two_anchor"])
        self.assertLess(row["endpoint_error_mm"], 1e-6)

    def test_joined_polyline_is_present_but_not_two_anchor(self):
        res = grade("square_as_joined_cyclic_polyline")
        self.assertEqual({r["form"] for r in res["lines"]}, {"present_not_two_anchor"})
        self.assertEqual(res["metrics"]["line_geometry_presence"]["value"], 1.0)
        self.assertEqual(res["metrics"]["line_two_anchor"]["value"], 0.0)

    def test_wrong_width_is_its_own_dimension(self):
        res = grade("wrong_width_20x")
        self.assertEqual(res["metrics"]["line_two_anchor"]["value"], 1.0)
        self.assertEqual(res["metrics"]["line_width_correct"]["value"], 0.0)
        self.assertAlmostEqual(res["lines"][0]["recovered_width_mm"], 10.0, places=6)

    def test_overshoot_detected_geometrically(self):
        seg = {"p0": (10, 10), "p1": (1, 10), "p2": (49, 10), "p3": (40, 10)}
        self.assertFalse(seg_is_finite_line(seg, 0.05))
        ok = {"p0": (10, 10), "p1": (20, 10), "p2": (30, 10), "p3": (40, 10)}
        self.assertTrue(seg_is_finite_line(ok, 0.05))

    def test_parallel_walls_keep_distinct_matches(self):
        res = grade("separate_parallel_walls")
        idx = [r["stroke_index"] for r in res["lines"]]
        self.assertEqual(sorted(idx), [0, 1])

    def test_overmerge_names_both_truth_lines(self):
        res = grade("true_overmerge_collinear_gap")
        self.assertEqual(res["merges"][0]["truth_ids"], ["g1", "g2"])


class ArcCircleContracts(unittest.TestCase):
    def test_wrong_side_arc_covers_opposite_side(self):
        row = grade("arc_wrong_side")["arcs"][0]
        self.assertFalse(row["complete"])
        self.assertLess(row["best_stroke_sweep_coverage"], 0.1)
        self.assertGreater(row["best_stroke_opposite_side_coverage"], 0.9)

    def test_partial_circle_reports_coverage_near_three_quarters(self):
        row = grade("circle_partial_270")["circles"][0]
        self.assertFalse(row["complete"])
        self.assertAlmostEqual(row["best_stroke_angular_coverage"], 0.75, delta=0.03)

    def test_extra_circle_is_unsupported_closed_curve(self):
        res = grade("circle_extra")
        kinds = [u["kind"] for u in res["unsupported_strokes"]]
        self.assertEqual(kinds, ["unsupported_closed_curve"])


class JunctionContracts(unittest.TestCase):
    def test_true_t_has_full_local_coverage(self):
        c = grade("t_junction_true_reversed")["connections"][0]
        self.assertTrue(c["recovered"])
        self.assertEqual(c["host_local_coverage"], 1.0)

    def test_broken_host_has_low_local_coverage(self):
        c = grade("t_junction_broken_host")["connections"][0]
        self.assertFalse(c["recovered"])
        self.assertLess(c["host_local_coverage"], 0.9)  # below the presence requirement

    def test_tangent_never_claims_tangency(self):
        c = grade("tangent_join_never_claimed")["connections"][0]
        self.assertEqual(c["tangency"], "not_scored")


class DxfContracts(unittest.TestCase):
    @staticmethod
    def _dxf(entities):
        body = "\n".join(entities)
        return f"0\nSECTION\n2\nENTITIES\n{body}\n0\nENDSEC\n0\nEOF\n"

    def test_circle_then_polyline_then_final_circle(self):
        text = self._dxf([
            "0\nCIRCLE\n8\nL\n10\n50\n20\n50\n40\n8",
            "0\nPOLYLINE\n66\n1\n70\n0\n40\n5\n0\nVERTEX\n10\n0\n20\n0\n0\nVERTEX\n10\n10\n20\n0\n0\nSEQEND",
            "0\nCIRCLE\n10\n20\n20\n20\n40\n5",
        ])
        ents = dxf.parse_entities(text)
        self.assertEqual([e["type"] for e in ents], ["CIRCLE", "POLYLINE", "CIRCLE"])
        self.assertEqual(len(ents[1]["verts"]), 2)
        self.assertEqual(ents[2]["r"], 5.0)

    def test_single_circle_flushed_at_eof_without_endsec(self):
        ents = dxf.parse_entities("0\nSECTION\n2\nENTITIES\n0\nCIRCLE\n10\n1\n20\n2\n40\n3\n")
        self.assertEqual(len(ents), 1)
        self.assertEqual((ents[0]["x"], ents[0]["y"], ents[0]["r"]), (1.0, 2.0, 3.0))

    def test_bulged_closed_polyline_is_exact_circle_in_mm(self):
        # two semicircular bulges around (50, 30) r=8 in a 100x100 px DXF (y-up)
        text = self._dxf(["0\nPOLYLINE\n66\n1\n70\n1\n40\n2\n0\nVERTEX\n10\n42\n20\n30\n42\n1"
                          "\n0\nVERTEX\n10\n58\n20\n30\n42\n1\n0\nSEQEND"])
        reg = Registration(10.0, 10.0)
        out = dxf.to_mm(dxf.parse_entities(text), 100, reg)
        ex = out["polylines"][0]["exact_circle"]
        self.assertIsNotNone(ex)
        self.assertAlmostEqual(ex["center_mm"][0], 5.0, places=9)
        self.assertAlmostEqual(ex["center_mm"][1], 7.0, places=9)   # y flipped: (100-30)/10
        self.assertAlmostEqual(ex["r_mm"], 0.8, places=9)
        self.assertAlmostEqual(out["polylines"][0]["width_mm"], 0.2, places=9)

    def test_bulge_quarter_is_ccw_in_dxf_frame(self):
        cen, r, a0, sw = bulge_arc((1, 0), (0, 1), math.tan(math.radians(90) / 4))
        self.assertAlmostEqual(cen[0], 0.0, places=9)
        self.assertAlmostEqual(cen[1], 0.0, places=9)
        self.assertAlmostEqual(r, 1.0, places=9)
        self.assertAlmostEqual(sw, 90.0, places=9)

    def test_exact_bulge_circle_counts_as_analytic_export(self):
        truth, geo, _ = C.controls()["circle_complete"]
        h = geo["height"]
        # circle centre (50,50) mm r 8 -> px (500,500) r 80; DXF y = h - y
        text = self._dxf([f"0\nPOLYLINE\n66\n1\n70\n1\n40\n5\n0\nVERTEX\n10\n420\n20\n{h - 500}\n42\n1"
                          f"\n0\nVERTEX\n10\n580\n20\n{h - 500}\n42\n1\n0\nSEQEND"])
        res = score_case(truth, geo, C.CASE, dxf_text=text)
        self.assertEqual(res["circles"][0]["export"], "dxf_exact_bulge_circle")
        self.assertEqual(res["metrics"]["circle_analytic_export"]["value"], 1.0)

    def test_unmatched_dxf_circle_is_reported(self):
        truth, geo, _ = C.controls()["circle_complete"]
        text = self._dxf(["0\nCIRCLE\n10\n200\n20\n200\n40\n30"])
        res = score_case(truth, geo, C.CASE, dxf_text=text)
        self.assertEqual(res["counts"]["unmatched_dxf_circles"], 1)
        self.assertEqual(res["circles"][0]["export"], "not_exported_as_circle")


class PixelAndRegistrationContracts(unittest.TestCase):
    def test_ink_counts_are_pixels_not_bits(self):
        w, h = 4, 3
        dark = {(0, 0), (1, 0), (2, 1), (3, 2), (0, 2)}
        rgba = bytearray()
        for y in range(h):
            for x in range(w):
                v = 0 if (x, y) in dark else 255
                rgba += bytes((v, v, v, 255))
        with tempfile.TemporaryDirectory() as d:
            a = os.path.join(d, "a.png")
            pngio.write_png(a, w, h, bytes(rgba))
            m = image_measures(a, a)
        self.assertEqual(m["ink_pixels_input"], 5)
        self.assertEqual(m["pixels_total"], 12)
        self.assertEqual(m["iou"], 1.0)
        self.assertEqual(m["input_ink_lost_share"], 0.0)

    def test_uniform_unit_transform(self):
        reg = Registration.from_case({"case_id": "u", "dpi": 254.0, "page_mm": [10, 5], "pixel_size": [100, 50],
                                      "transform": "px = mm * dpi / 25.4"})
        self.assertTrue(reg.uniform)
        self.assertEqual(reg.to_mm((50, 25)), (5.0, 2.5))
        self.assertAlmostEqual(reg.len_mm(10), 1.0)

    def test_inconsistent_surface_rejected(self):
        with self.assertRaises(RegistrationError):
            Registration.from_case({"case_id": "bad", "dpi": 254.0, "page_mm": [10, 5], "pixel_size": [100, 80],
                                    "transform": "px = mm * dpi / 25.4"})

    def test_inferred_transform_is_flagged(self):
        reg = Registration.from_case({"case_id": "i", "page_mm": [10, 5], "pixel_size": [100, 60]})
        self.assertTrue(reg.record["inferred"])
        self.assertEqual(reg.to_mm((50, 30)), (5.0, 2.5))   # meet: 10 px/mm, 5 px y-offset

    def test_geometry_frame_must_match_case(self):
        truth, geo, _ = C.controls()["line_identity"]
        bad = dict(geo, width=999)
        with self.assertRaises(ValueError):
            score_case(truth, bad, C.CASE)


if __name__ == "__main__":
    unittest.main()
