using Vecto.Core;

namespace Vecto.Tests;

public class CenterlineCircleProposalTests
{
    [Theory]
    [InlineData(8.0, 2.0)]
    [InlineData(19.0, 3.0)]
    [InlineData(36.0, 6.0)]
    [InlineData(118.11, 16.54)]
    [InlineData(180.0, 24.0)]
    public void IsolatedRingsRecoverCentreRadiusAndRadialWidth(double radius, double width)
    {
        int side = (int)Math.Ceiling(2 * (radius + width + 12));
        var center = new Vec2(side / 2.0 + 0.27, side / 2.0 - 0.31);
        var (ink, ids) = Source(side, side, p => Ring(p, center, radius, width));
        var proposal = Assert.Single(CenterlineCircles.Propose(ink, ids, new HashSet<int> { 0 }, side, side));
        AssertCircle(proposal, center, radius, width);
        Assert.Equal(0, proposal.SourceComponentId);
        Assert.False(ink[proposal.CavitySeedIndex]);
        Assert.True(proposal.Evidence.InnerRadius < proposal.Radius);
    }

    [Fact]
    public void TouchingHatchesCrossingAttachmentsAndLeaderKeepOneCoherentCircle()
    {
        const int side = 200;
        var center = new Vec2(94.3, 95.1);
        const double radius = 36, width = 6;
        bool Shape(Vec2 p)
        {
            if (Ring(p, center, radius, width)) return true;
            foreach (double dy in new[] { -22.0, -7.0, 10.0 })
            {
                double endX = center.X - Math.Sqrt(radius * radius - dy * dy) + 1;
                if (Line(p, new Vec2(14, center.Y + dy), new Vec2(endX, center.Y + dy), 3)) return true;
            }
            return Line(p, new Vec2(14, 69), new Vec2(14, 110), 3) ||
                Line(p, center + new Vec2(25.5, -25.5), new Vec2(164, 25.4), 3) ||
                Line(p, new Vec2(143, 15), new Vec2(143, 58), 3);
        }
        var (ink, ids) = Source(side, side, Shape);
        var beforeInk = (bool[])ink.Clone();
        var beforeIds = (int[])ids.Clone();
        var result = CenterlineCircles.Propose(ink, ids, AllIds(ids), side, side);
        AssertCircle(Assert.Single(result), center, radius, width);
        Assert.Equal(beforeInk, ink);
        Assert.Equal(beforeIds, ids);
    }

    [Fact]
    public void BroadAttachedSolidDoesNotInflateRingWidth()
    {
        var center = new Vec2(90.2, 90.4);
        // The solid starts one pixel outside the inner paper cavity (centre + 37.8 px)
        // and inside the ring's outer skin, so it attaches without cutting the cavity.
        var (ink, ids) = Source(210, 190, p => Ring(p, center, 40, 6) ||
            (p.X >= 128 && p.X < 195 && p.Y >= 53 && p.Y < 128));
        var proposal = Assert.Single(CenterlineCircles.Propose(ink, ids, AllIds(ids), 210, 190));
        AssertCircle(proposal, center, 40, 6);
        Assert.True(proposal.Evidence.ConsistentWidthSamples < proposal.Evidence.CircumferenceSamples);
    }

    [Theory]
    [InlineData("ellipse")]
    [InlineData("near-ellipse")]
    [InlineData("slot")]
    [InlineData("rectangle")]
    [InlineData("partial")]
    [InlineData("occluded")]
    [InlineData("interior-crossing")]
    [InlineData("interior-island")]
    [InlineData("solid-with-round-hole")]
    public void NonCircularOrOccludedLoopsAreNotPromoted(string shape)
    {
        var center = new Vec2(90, 90);
        bool Shape(Vec2 p)
        {
            var d = p - center;
            double r = d.Length;
            return shape switch
            {
                "ellipse" => Math.Abs(Math.Sqrt(d.X * d.X / (42 * 42) + d.Y * d.Y / (27 * 27)) - 1) < 0.075,
                "near-ellipse" => Math.Abs(Math.Sqrt(d.X * d.X / (42 * 42) + d.Y * d.Y / (39 * 39)) - 1) < 0.06,
                "slot" => Math.Abs((new Vec2(Math.Max(0, Math.Abs(d.X) - 18), d.Y)).Length - 25) < 3,
                "rectangle" => Math.Abs(d.X) < 42 && Math.Abs(d.Y) < 42 && (Math.Abs(d.X) >= 36 || Math.Abs(d.Y) >= 36),
                "partial" => Ring(p, center, 39, 6) && !(d.X > 0 && Math.Abs(d.Y) < 9),
                "occluded" => Ring(p, center, 39, 6) || (r < 39 && d.Y > 5),
                "interior-crossing" => Ring(p, center, 39, 6) || Line(p, new Vec2(25, 90), new Vec2(155, 90), 3),
                "interior-island" => Ring(p, center, 39, 6) || r < 2,
                "solid-with-round-hole" => r >= 36 && Math.Abs(d.X) < 70 && Math.Abs(d.Y) < 70,
                _ => throw new InvalidOperationException()
            };
        }
        var (ink, ids) = Source(180, 180, Shape);
        Assert.Empty(CenterlineCircles.Propose(ink, ids, AllIds(ids), 180, 180));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceGapIsNotBridgedEvenInsideAnEnclosingBox(bool diagonal)
    {
        var center = new Vec2(75, 75);
        var (ink, ids) = Source(150, 150, p =>
        {
            var d = p - center;
            bool gap = diagonal ? d.X > 0 && Math.Abs(d.X - d.Y) < 1.5 : d.X > 0 && Math.Abs(d.Y) < 0.6;
            bool box = p.X >= 8 && p.X < 142 && p.Y >= 8 && p.Y < 142 &&
                (p.X < 11 || p.X >= 139 || p.Y < 11 || p.Y >= 139);
            return (Ring(p, center, 35, 6) && !gap) || box;
        });
        Assert.Empty(CenterlineCircles.Propose(ink, ids, AllIds(ids), 150, 150));
    }

    [Fact]
    public void RoundCavityAloneCannotSupplyMissingCentrelineInk()
    {
        var center = new Vec2(90, 90);
        var (ink, ids) = Source(180, 180, p =>
        {
            var d = p - center;
            // Keep a closed 1px inner skin across a missing outer sector. The cavity
            // still fits a perfect circle; full-circumference ink verification must fail.
            return Ring(p, center, 40, 8) && !(d.X > 0 && Math.Abs(d.Y) < 5 && d.Length > 37);
        });
        Assert.Empty(CenterlineCircles.Propose(ink, ids, AllIds(ids), 180, 180));
    }

    [Fact]
    public void ExplicitExclusionWinsEvenForPerfectlyRoundLetter()
    {
        var (ink, ids) = Source(120, 120, p => Ring(p, new Vec2(60, 60), 30, 6));
        Assert.Empty(CenterlineCircles.Propose(ink, ids, new HashSet<int>(), 120, 120));
        Assert.Empty(CenterlineCircles.Propose(ink, ids, new HashSet<int> { 123 }, 120, 120));
        var protection = new bool[ink.Length];
        protection[Array.FindIndex(ink, b => b)] = true;
        Assert.Empty(CenterlineCircles.Propose(ink, ids, AllIds(ids), 120, 120, protection));
    }

    [Fact]
    public void ProtectedRemoteAttachmentVetoesItsWholeComponent()
    {
        var (ink, ids) = Source(180, 140, p => Ring(p, new Vec2(60, 70), 30, 6) ||
            Line(p, new Vec2(88, 70), new Vec2(160, 70), 4));
        var protection = new bool[ink.Length];
        protection[70 * 180 + 158] = true;
        Assert.True(ink[70 * 180 + 158]);
        Assert.Empty(CenterlineCircles.Propose(ink, ids, AllIds(ids), 180, 140, protection));
    }

    [Fact]
    public void UnknownComponentCannotBecomeSupport()
    {
        var (ink, ids) = Source(120, 120, p => Ring(p, new Vec2(60, 60), 30, 6));
        Array.Fill(ids, -1);
        Assert.Empty(CenterlineCircles.Propose(ink, ids, new HashSet<int> { -1, 0 }, 120, 120));
    }

    [Fact]
    public void EligibilityAndSourceIdentityRemainSeparateForNeighbouringLoops()
    {
        var (ink, ids) = Source(240, 120, p => Ring(p, new Vec2(60, 60), 30, 6) ||
            Ring(p, new Vec2(180, 60), 30, 6));
        int rightId = ids[60 * 240 + 209];
        var proposal = Assert.Single(CenterlineCircles.Propose(ink, ids, new HashSet<int> { rightId }, 240, 120));
        AssertCircle(proposal, new Vec2(180, 60), 30, 6);
        Assert.Equal(rightId, proposal.SourceComponentId);
    }

    [Fact]
    public void TwoRingsMayShareSourceComponentWithoutSharingCavityIdentity()
    {
        var (ink, ids) = Source(240, 120, p => Ring(p, new Vec2(60, 60), 30, 6) ||
            Ring(p, new Vec2(180, 60), 30, 6) || Line(p, new Vec2(89, 60), new Vec2(151, 60), 3));
        var result = CenterlineCircles.Propose(ink, ids, AllIds(ids), 240, 120);
        Assert.Equal(2, result.Count);
        Assert.Equal(result[0].SourceComponentId, result[1].SourceComponentId);
        Assert.NotEqual(result[0].CavitySeedIndex, result[1].CavitySeedIndex);
    }

    [Fact]
    public void TranslatedGeometryAndRepeatedCallsStayStable()
    {
        var center = new Vec2(1040.3, 1090.7);
        var (ink, ids) = Source(1120, 1170, p => Ring(p, center, 33, 5));
        var first = Assert.Single(CenterlineCircles.Propose(ink, ids, AllIds(ids), 1120, 1170));
        var second = Assert.Single(CenterlineCircles.Propose(ink, ids, AllIds(ids), 1120, 1170));
        Assert.Equal(first, second);
        AssertCircle(first, center, 33, 5);
    }

    [Fact]
    public void InvalidPlanesAndCancellationAreExplicit()
    {
        Assert.Throws<ArgumentException>(() => CenterlineCircles.Propose(new bool[4], new int[3], new HashSet<int> { 0 }, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CenterlineCircles.Propose([], [], new HashSet<int>(), int.MaxValue, 2));
        Assert.Throws<ArgumentException>(() => CenterlineCircles.Propose(new bool[4], new int[4], new HashSet<int>(), 2, 2, new bool[3]));
        Assert.Throws<OperationCanceledException>(() => CenterlineCircles.Propose(new bool[4], new int[4], new HashSet<int> { 0 }, 2, 2, ct: new CancellationToken(true)));
        Assert.Empty(CenterlineCircles.Propose(new bool[4], new int[4], new HashSet<int> { 0 }, 2, 2));
    }

    static void AssertCircle(CenterlineCircles.Proposal proposal, Vec2 center, double radius, double width)
    {
        Assert.InRange((proposal.Center - center).Length, 0, 0.5);
        Assert.InRange(Math.Abs(proposal.Radius - radius), 0, 0.65);
        Assert.InRange(Math.Abs(proposal.Width - width), 0, 0.8);
        Assert.Equal(proposal.Evidence.CircumferenceSamples, proposal.Evidence.SupportedSamples);
        Assert.True(proposal.Evidence.CircumferenceSamples >= 2 * Math.PI * radius / 0.5);
        Assert.True(proposal.Evidence.ConsistentWidthSamples >= 0.75 * proposal.Evidence.CircumferenceSamples);
    }

    static bool Ring(Vec2 p, Vec2 center, double radius, double width) =>
        Math.Abs((p - center).Length - radius) <= width / 2;

    static bool Line(Vec2 p, Vec2 a, Vec2 b, double width)
    {
        var d = b - a;
        double t = Math.Clamp((p - a).Dot(d) / d.LengthSq, 0, 1);
        return p.DistSq(a + d * t) <= width * width / 4;
    }

    static HashSet<int> AllIds(int[] ids) => ids.Where(id => id >= 0).ToHashSet();

    // Independent analytic pixel-centre rasterization and four-connected labelling.
    // No production fitting code or fixture coordinates are used to decide eligibility.
    static (bool[] Ink, int[] Ids) Source(int w, int h, Func<Vec2, bool> shape)
    {
        var ink = new bool[w * h];
        var ids = new int[ink.Length];
        Array.Fill(ids, -1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) ink[y * w + x] = shape(new Vec2(x + 0.5, y + 0.5));
        var queue = new Queue<int>();
        int id = 0;
        for (int i = 0; i < ink.Length; i++)
        {
            if (!ink[i] || ids[i] >= 0) continue;
            ids[i] = id;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue(), x = p % w, y = p / w;
                if (x > 0) Add(p - 1);
                if (x + 1 < w) Add(p + 1);
                if (y > 0) Add(p - w);
                if (y + 1 < h) Add(p + w);
            }
            id++;
        }
        return (ink, ids);

        void Add(int p)
        {
            if (!ink[p] || ids[p] >= 0) return;
            ids[p] = id;
            queue.Enqueue(p);
        }
    }
}
