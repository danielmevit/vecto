"""Finite planar geometry helpers (standard library only, millimetres, y-down)."""

import math


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def bez(seg, t):
    p0, p1, p2, p3 = seg["p0"], seg["p1"], seg["p2"], seg["p3"]
    mt = 1.0 - t
    a, b, c, d = mt * mt * mt, 3 * mt * mt * t, 3 * mt * t * t, t * t * t
    return (a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0],
            a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1])


def sample_seg(seg, n=16):
    return [bez(seg, i / n) for i in range(n + 1)]


def sample_segments(segs, n=16):
    pts = []
    for i, s in enumerate(segs):
        p = sample_seg(s, n)
        pts.extend(p if i == 0 else p[1:])
    return pts


def chords_of(points):
    return [(points[i], points[i + 1]) for i in range(len(points) - 1)]


def point_seg_distance(p, a, b):
    """Distance from p to the FINITE segment a-b (projection clamped)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    if l2 < 1e-18:
        return dist(p, a)
    t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2
    t = max(0.0, min(1.0, t))
    return dist(p, (a[0] + t * dx, a[1] + t * dy))


def point_chords_distance(p, chords):
    return min((point_seg_distance(p, a, b) for a, b in chords), default=float("inf"))


def seg_is_finite_line(seg, tol):
    """A cubic is a two-anchor straight line only if both control points lie
    within `tol` of the FINITE chord p0-p3. A collinear control point outside
    the chord (overshoot) makes the drawn curve extend past an anchor, so it
    is rejected; the infinite-line test of the original grader accepted it."""
    a, b = seg["p0"], seg["p3"]
    if dist(a, b) < 1e-9:
        return False
    return all(point_seg_distance(p, a, b) <= tol for p in (seg["p1"], seg["p2"]))


def angle_deg(a, b):
    return math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 180.0


def ang_diff(a, b):
    d = abs(a - b) % 180.0
    return min(d, 180.0 - d)


def line_samples(a, b, n=40):
    return [(a[0] + (b[0] - a[0]) * i / n, a[1] + (b[1] - a[1]) * i / n) for i in range(n + 1)]


def arc_samples(center, r, start_deg, sweep_deg, n=72):
    """Points along the arc in the drawn direction (signed sweep, y-down page angles)."""
    out = []
    for i in range(n + 1):
        a = math.radians(start_deg + sweep_deg * i / n)
        out.append((center[0] + r * math.cos(a), center[1] + r * math.sin(a)))
    return out


def circle_fit(points):
    """Kasa least-squares circle -> (cx, cy, r, rms) or None."""
    n = len(points)
    if n < 3:
        return None
    sx = sum(p[0] for p in points)
    sy = sum(p[1] for p in points)
    sxx = sum(p[0] * p[0] for p in points)
    syy = sum(p[1] * p[1] for p in points)
    sxy = sum(p[0] * p[1] for p in points)
    sxz = sum(p[0] * (p[0] ** 2 + p[1] ** 2) for p in points)
    syz = sum(p[1] * (p[0] ** 2 + p[1] ** 2) for p in points)
    m = [[sxx, sxy, sx], [sxy, syy, sy], [sx, sy, float(n)]]
    rhs = [sxz, syz, sxx + syy]

    def det3(q):
        return (q[0][0] * (q[1][1] * q[2][2] - q[1][2] * q[2][1])
                - q[0][1] * (q[1][0] * q[2][2] - q[1][2] * q[2][0])
                + q[0][2] * (q[1][0] * q[2][1] - q[1][1] * q[2][0]))

    d = det3(m)
    if abs(d) < 1e-12:
        return None
    sol = []
    for k in range(3):
        mk = [row[:] for row in m]
        for i in range(3):
            mk[i][k] = rhs[i]
        sol.append(det3(mk) / d)
    cx, cy = sol[0] / 2.0, sol[1] / 2.0
    r2 = sol[2] + cx * cx + cy * cy
    if r2 <= 0:
        return None
    r = math.sqrt(r2)
    rms = math.sqrt(sum((dist(p, (cx, cy)) - r) ** 2 for p in points) / n)
    return cx, cy, r, rms


def bulge_arc(p, q, bulge):
    """Exact arc of a DXF bulge segment p->q -> (center, r, start_deg, sweep_deg)
    in the frame of p/q, or None for a straight (zero-bulge) segment."""
    if abs(bulge) < 1e-9:
        return None
    theta = 4.0 * math.atan(bulge)          # signed included angle
    c = dist(p, q)
    if c < 1e-12:
        return None
    r = c / (2.0 * math.sin(abs(theta) / 2.0))
    mx, my = (p[0] + q[0]) / 2.0, (p[1] + q[1]) / 2.0
    h = r * math.cos(abs(theta) / 2.0)       # midpoint-to-center distance
    ux, uy = (q[0] - p[0]) / c, (q[1] - p[1]) / c
    nx, ny = -uy, ux                         # left normal in this frame
    side = 1.0 if (theta > 0) == (abs(theta) < math.pi) else -1.0
    cen = (mx + side * h * nx, my + side * h * ny)
    a0 = math.degrees(math.atan2(p[1] - cen[1], p[0] - cen[0]))
    return cen, r, a0, math.degrees(theta)
