"""Focused regressions for independently reproduced VEC-94 findings."""
import json
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
from cad_reference import controls as C
from cad_reference.grade import TOLERANCES, score_case
from cad_reference.validate import dump


class ReviewFixes(unittest.TestCase):
    def small(self, sweep, strokes):
        return score_case(C.truth([C.t_arc('small', (50, 50), 10, 0, sweep)]), C.geo(strokes), C.CASE)

    def test_empty_small_arcs_fail_with_null_deviation(self):
        for sweep in (2, 4, -2, -4, 90, 180):
            with self.subTest(sweep=sweep):
                res = self.small(sweep, [])
                row = res['arcs'][0]
                self.assertFalse(row['complete'])
                self.assertIsNone(row['max_deviation_from_truth_arc_mm'])
                self.assertNotIn('best_stroke_index', row)
                json.dumps(res, allow_nan=False)

    def test_legitimate_small_arcs_pass(self):
        for sweep in (2, 4, -2, -4):
            res = self.small(sweep, [C.arc((50, 50), 10, 0, sweep)])
            self.assertTrue(res['arcs'][0]['complete'])
            self.assertGreaterEqual(res['arcs'][0]['required_coverage'], .9)
            json.dumps(res, allow_nan=False)

    def test_nearby_unrelated_chord_does_not_get_vacuous_credit(self):
        # Short fragment near the start of a long-radius 2/4 degree arc:
        # some actual coverage is insufficient, even within the search box.
        for sweep in (2, 4):
            truth = C.truth([C.t_arc('small', (0, 0), 100, 0, sweep)])
            res = score_case(truth, C.geo([C.line((100, 0), (100, .1))]), C.CASE)
            self.assertFalse(res['arcs'][0]['complete'])
            self.assertGreater(res['arcs'][0]['best_stroke_sweep_coverage'], 0)
            self.assertLess(res['arcs'][0]['best_stroke_sweep_coverage'], .9)
            json.dumps(res, allow_nan=False)

    def test_fully_missing_arc_with_unrelated_recovery_serializes(self):
        res = self.small(90, [C.line((0, 0), (1, 0))])
        self.assertFalse(res['arcs'][0]['complete'])
        self.assertIsNone(res['arcs'][0]['max_deviation_from_truth_arc_mm'])
        json.dumps(res, allow_nan=False)

    def test_invalid_sweeps_rejected(self):
        for sweep in (0, math.inf, -math.inf, math.nan, 361):
            with self.subTest(sweep=sweep), self.assertRaises(ValueError):
                self.small(sweep, [])

    def test_all_original_controls_strict_json(self):
        self.assertEqual(len(C.controls()), 25)
        for name, (truth, geo, _) in C.controls().items():
            with self.subTest(control=name):
                json.dumps(score_case(truth, geo, C.CASE), allow_nan=False)
        json.dumps(score_case(C.truth([]), C.geo([]), C.CASE), allow_nan=False)

    def test_final_output_guard_rejects_nonfinite(self):
        with tempfile.TemporaryDirectory() as directory:
            for bad in (math.inf, -math.inf, math.nan):
                with self.assertRaises(ValueError):
                    dump(os.path.join(directory, 'bad.json'), {'error': bad})

    def test_existing_straightness_cap_behavior_unchanged(self):
        self.assertEqual(TOLERANCES['two_anchor_straightness_mm'], {'cap': .02, 'endpoint_divisor': 10.0})
        for offset, expected in ((0, 1), (.019, 1), (.021, 0)):
            stroke = C.line((10, 10), (40, 10))
            for key in ('p1', 'p2'):
                stroke['segments'][0][key][1] += offset
            res = score_case(C.truth([C.t_line('line', (10, 10), (40, 10))]), C.geo([stroke]), C.CASE)
            self.assertEqual(res['metrics']['line_two_anchor']['numerator'], expected)
