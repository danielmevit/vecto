"""Analytic export requires a whole finite DXF object, independently of strokes."""
import copy
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
from cad_reference import dxf
from cad_reference.grade import _arc_export, tolerances_for
from cad_reference.registration import Registration


def truth(start=0, sweep=180, radius=10):
    return dict(center=(30, 30), r_mm=radius, start_deg=start,
                sweep_deg=sweep, width_mm=0.7)


def polyline(start=0, sweep=180, pieces=2, radius=10, center=(30, 30),
             width=0.7, closed=False, kind='POLYLINE'):
    # Write page y-down angles into DXF y-up coordinates, including bulge sign.
    vertices = []
    for i in range(pieces + 1):
        angle = math.radians(start + sweep * i / pieces)
        vertices.append((center[0] + radius * math.cos(angle),
                         100 - center[1] - radius * math.sin(angle),
                         -math.tan(math.radians(sweep / pieces) / 4) if i < pieces else 0))
    header = f'0\n{kind}\n70\n{int(closed)}\n{43 if kind == "LWPOLYLINE" else 40}\n{width}\n'
    return header + ''.join(('0\nVERTEX\n' if kind == 'POLYLINE' else '') +
                            f'10\n{x}\n20\n{y}\n42\n{b}\n' for x, y, b in vertices) + (
                                '0\nSEQEND\n' if kind == 'POLYLINE' else '')


def parse(*polys):
    text = '0\nSECTION\n2\nENTITIES\n' + ''.join(polys) + '0\nENDSEC\n0\nEOF\n'
    return dxf.to_mm(dxf.parse_entities(text), 100, Registration.unit())


class ArcExportContracts(unittest.TestCase):
    def exported(self, t=None, *polys):
        return _arc_export(t or truth(), parse(*(polys or (polyline(),))), tolerances_for(0.1))

    def test_two_piece_semicircle_and_one_piece_quarter(self):
        for kind in ('POLYLINE', 'LWPOLYLINE'):
            for start, sweep, pieces in ((0, 180, 2), (30, 90, 1), (90, -180, 2),
                                         (-90, 180, 3), (5, 270, 3)):
                with self.subTest(kind=kind, start=start, sweep=sweep):
                    self.assertEqual(self.exported(truth(start, sweep), polyline(start, sweep, pieces, kind=kind)),
                                     'dxf_bulge_arc')

    def test_forward_and_reverse_traversals_have_same_export(self):
        for kind in ('POLYLINE', 'LWPOLYLINE'):
            for start, sweep, pieces in ((30, 90, 1), (0, 180, 2),
                                         (90, -180, 3), (5, 270, 3)):
                for reverse in (False, True):
                    with self.subTest(kind=kind, sweep=sweep, reverse=reverse):
                        rendered = polyline(start + sweep if reverse else start,
                                            -sweep if reverse else sweep, pieces, kind=kind)
                        self.assertEqual(self.exported(truth(start, sweep), rendered), 'dxf_bulge_arc')

    def test_endpoint_swap_requires_sweep_reversal(self):
        # The same unordered endpoints on the opposite half-circle must fail.
        for start, sweep in ((0, -180), (180, 180)):
            self.assertEqual(self.exported(None, polyline(start, sweep)), 'not_exported_as_arc')
        bad = parse(polyline())
        arcs = bad['polylines'][0]['arcs']
        arcs[0]['start_mm'], arcs[-1]['end_mm'] = arcs[-1]['end_mm'], arcs[0]['start_mm']
        self.assertEqual(_arc_export(truth(), bad, tolerances_for(0.1)), 'not_exported_as_arc')

    def test_near_loop_and_close_wrong_geometry_rejected(self):
        for rendered in (polyline(sweep=359.99, pieces=4),
                         polyline(center=(30.56, 30)), polyline(radius=10.56)):
            self.assertEqual(self.exported(None, rendered), 'not_exported_as_arc')
        # Even when endpoint tolerances encompass a tiny circle, its opposite
        # midpoint lies on the wrong side and cannot be accepted.
        self.assertEqual(self.exported(truth(radius=0.1), polyline(sweep=-180, radius=0.1)),
                         'not_exported_as_arc')

    def test_registered_endpoints_and_legacy_sweep_are_additive(self):
        out = parse(polyline())['polylines'][0]
        self.assertEqual(out['span_count'], 2)
        a, b = out['arcs']
        self.assertAlmostEqual(a['sweep_deg'], -90)
        self.assertAlmostEqual(a['page_sweep_deg'], 90)
        self.assertEqual(a['end_mm'], b['start_mm'])
        self.assertAlmostEqual(a['start_mm'][0], 40)
        self.assertAlmostEqual(b['end_mm'][0], 20)

    def test_wrong_geometry(self):
        cases = {
            'half_sweep': polyline(sweep=90, pieces=1),
            'long_sweep': polyline(sweep=270, pieces=3),
            'opposite_direction': polyline(sweep=-180),
            'wrong_placement': polyline(start=90),
            'nearby': polyline(center=(32, 30)),
            'wrong_radius': polyline(radius=12),
            'wrong_width': polyline(width=2),
            'missing_width': polyline(width=0),
            'closed_arc': polyline(closed=True),
            'open_full_circle': polyline(sweep=360, pieces=4),
            'open_six_piece_circle': polyline(sweep=360, pieces=6),
            'multiple_turns': polyline(sweep=720, pieces=8),
            'closed_full_circle': polyline(sweep=360, pieces=4, closed=True),
        }
        for name, text in cases.items():
            with self.subTest(name=name):
                self.assertEqual(self.exported(None, text), 'not_exported_as_arc')

    def test_wrong_circle_in_actual_dxf_is_rejected(self):
        text = polyline()
        bulge = str(-math.tan(math.radians(90) / 4))
        # The first span is correct; the other has the same shared endpoint but
        # a different analytic circle. A matching segment must not rescue it.
        pos = text.rfind('42\n' + bulge)
        text = text[:pos] + text[pos:].replace(bulge, '-0.5', 1)
        self.assertEqual(self.exported(None, text), 'not_exported_as_arc')

    def test_separate_objects_never_joined(self):
        self.assertEqual(self.exported(None, polyline(0, 90, 1), polyline(90, 90, 1)),
                         'not_exported_as_arc')

    def test_chords_and_mixed_spans_rejected(self):
        text = polyline()
        bulge = str(-math.tan(math.radians(90) / 4))
        for modified in (text.replace(bulge, '0'), text.replace(bulge, '0', 1)):
            self.assertEqual(self.exported(None, modified), 'not_exported_as_arc')

    def test_different_circles_and_direction_changes_rejected(self):
        out = parse(polyline())
        for key, val in (('center_mm', (30.01, 30)), ('r_mm', 10.01), ('page_sweep_deg', -90)):
            bad = copy.deepcopy(out)
            bad['polylines'][0]['arcs'][1][key] = val
            self.assertEqual(_arc_export(truth(), bad, tolerances_for(0.1)), 'not_exported_as_arc')

    def test_discontinuous_or_incomplete_arc_metadata_rejected(self):
        out = parse(polyline())
        for key, value in (('start_mm', (29, 40)), ('end_mm', (float('nan'), 30))):
            bad = copy.deepcopy(out)
            bad['polylines'][0]['arcs'][1][key] = value
            self.assertEqual(_arc_export(truth(), bad, tolerances_for(0.1)), 'not_exported_as_arc')
        for key in ('start_mm', 'end_mm', 'page_sweep_deg'):
            bad = copy.deepcopy(out)
            del bad['polylines'][0]['arcs'][0][key]
            self.assertEqual(_arc_export(truth(), bad, tolerances_for(0.1)), 'not_exported_as_arc')

    def test_nonfinite_malformed_values_fail_closed(self):
        for token in ('nan', 'inf', '-inf', 'garbage', '1e308'):
            for old in ('10\n40.0', '20\n70.0', '42\n-0.41421356237309503', '40\n0.7', '70\n0'):
                text = polyline().replace(old, old.split('\n')[0] + '\n' + token, 1)
                with self.subTest(token=token, field=old):
                    self.assertNotEqual(text, polyline())
                    self.assertEqual(self.exported(None, text), 'not_exported_as_arc')
        # Missing coordinate must invalidate the entire object, not bridge vertices.
        for kind in ('POLYLINE', 'LWPOLYLINE'):
            self.assertEqual(self.exported(None, polyline(kind=kind).replace('20\n70.0\n', '', 1)),
                             'not_exported_as_arc')

    def test_variable_vertex_width_is_not_constant_width_evidence(self):
        for kind in ('POLYLINE', 'LWPOLYLINE'):
            text = polyline(kind=kind).replace('10\n40.0', '40\n2.0\n10\n40.0', 1)
            self.assertEqual(self.exported(None, text), 'not_exported_as_arc')

    def test_tiny_arc_still_needs_meaningful_sweep(self):
        self.assertEqual(self.exported(truth(0, 3), polyline(0, 0.1, 1)), 'not_exported_as_arc')
        self.assertEqual(self.exported(truth(0, 3), polyline(0, 3, 1)), 'dxf_bulge_arc')

    def test_no_dxf(self):
        self.assertEqual(_arc_export(truth(), None, tolerances_for(0.1)), 'no_dxf')


if __name__ == '__main__':
    unittest.main()
