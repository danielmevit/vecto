namespace Vecto.Core;

public enum ImageStyle
{
    Auto,
    /// <summary>Aliased art (flat colors, hard edges) — palette taken verbatim when small enough.</summary>
    Crisp,
    /// <summary>Anti-aliased art — palette inferred from flat areas, fringes excluded.</summary>
    Blended,
    /// <summary>Continuous-tone input — denoised first, tighter cluster merging.</summary>
    Photo,
    /// <summary>Scanned line drawings / CAD art — Sauvola-binarized to ink/paper before tracing;
    /// never selected by Auto (see ScanPreprocess).</summary>
    LineArt,
}

public enum DetailLevel { Low, Medium, High }

public enum PaletteMode { Auto, FixedCount }

public sealed class TraceOptions
{
    public PaletteMode PaletteMode { get; set; } = PaletteMode.Auto;
    /// <summary>Upper bound on inferred colors when <see cref="PaletteMode.Auto"/>.</summary>
    public int MaxColors { get; set; } = 16;
    /// <summary>Exact color count when <see cref="PaletteMode.FixedCount"/>.</summary>
    public int ColorCount { get; set; } = 12;
    public ImageStyle Style { get; set; } = ImageStyle.Auto;
    public DetailLevel Detail { get; set; } = DetailLevel.Medium;
    /// <summary>false → emit simplified polygons instead of fitted curves (debug/inspection).</summary>
    public bool CurveFitting { get; set; } = true;
    /// <summary>Polygon-mode simplification tolerance; negative → derive from Detail, 0 → exact lattice polygons.</summary>
    public double PolygonEpsilon { get; set; } = -1;
    public int Seed { get; set; } = 1;
}

/// <summary>Every tuning constant the pipeline actually runs with, resolved from options + image stats.</summary>
public sealed class EffectiveParams
{
    public ImageStyle Style;
    public bool ExactPalette;
    public bool ExcludeEdgeSamples;
    public int KMeansK;
    public bool MergeClusters;
    public double MergeThreshold;     // Oklab distance (black↔white ≈ 1.0)
    public int MinRegionArea;         // px; smaller regions are absorbed into a neighbor
    public bool MedianPrefilter;
    public bool Noisy;
    /// <summary>Continuous-tone input (photo/textured art/old scan): fringe relabel and noisy treatment skipped.</summary>
    public bool Textured;
    public double NoiseShare;
    public int SmoothIterations;
    public double SmoothLambda;
    public double SmoothClamp;        // px; max drift of a boundary point from the raster crack
    public double CornerThresholdDeg; // turn between adjacent optimal-polygon edges (lattice) to become a corner
    public double CornerKeepDeg;      // the same turn re-measured on the refined geometry to stay one
    public double SubpixelMaxShift;   // px; cap on normal-direction edge refinement
    public double CornerRelocateMaxShift; // px; a corner may leave its lattice pin by at most this
    public double AngleSnapDeg;       // flank directions this close to a 45° multiple snap exactly
    public double FitToleranceSq;     // px²
    public double PolygonEpsilon;
    public bool CurveFitting;
    public int Seed;
}
