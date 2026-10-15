using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vecto.Core;

namespace Vecto.App;

public sealed class PaletteSwatch
{
    public required Brush Brush { get; init; }
    public required string Tooltip { get; init; }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    RasterImage? _raster;
    TraceResult? _result;
    CancellationTokenSource? _cts;
    readonly DispatcherTimer _debounce;
    string? _sourcePath;

    public MainViewModel()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartTrace();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FitRequested;

    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    BitmapSource? _sourceBitmap;
    public BitmapSource? SourceBitmap
    {
        get => _sourceBitmap;
        private set { _sourceBitmap = value; Raise(); Raise(nameof(HasImage)); }
    }

    ImageSource? _vectorImage;
    public ImageSource? VectorImage
    {
        get => _vectorImage;
        private set { _vectorImage = value; Raise(); Raise(nameof(RightSource)); Raise(nameof(HasResult)); }
    }

    BitmapSource? _segmentationBitmap;
    public BitmapSource? SegmentationBitmap
    {
        get => _segmentationBitmap;
        private set { _segmentationBitmap = value; Raise(); Raise(nameof(RightSource)); }
    }

    int _rightMode;   // 0 = vector result, 1 = segmentation, 2 = nodes/wireframe
    public int RightMode
    {
        get => _rightMode;
        set
        {
            _rightMode = value;
            if (value == 2) RefreshWireframe();
            Raise();
            Raise(nameof(RightSource));
        }
    }

    ImageSource? _wireframeImage;
    void RefreshWireframe()
    {
        if (_result == null) return;
        _wireframeImage = VectorRendering.ToWireframeImage(_result.Document, _zoom);
        Raise(nameof(RightSource));
    }

    public ImageSource? RightSource => _rightMode switch
    {
        1 => _segmentationBitmap,
        2 => _wireframeImage,
        _ => _vectorImage,
    };

    public bool HasImage => _sourceBitmap != null;
    public bool HasResult => _result != null;

    double _zoom = 1;
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.05, 64);
            Raise();
            Raise(nameof(ZoomText));
            if (_rightMode == 2) RefreshWireframe();   // stroke/marker sizes are zoom-relative
        }
    }
    public string ZoomText => _zoom.ToString("P0");

    string _status = "Open (Ctrl+O), paste (Ctrl+V) or drop an image to vectorize.";
    public string Status
    {
        get => _status;
        set { _status = value; Raise(); }
    }

    string _statsText = "";
    public string StatsText
    {
        get => _statsText;
        private set { _statsText = value; Raise(); }
    }

    List<PaletteSwatch> _swatches = new();
    public List<PaletteSwatch> Swatches
    {
        get => _swatches;
        private set { _swatches = value; Raise(); }
    }

    bool _autoColors = true;
    public bool AutoColors
    {
        get => _autoColors;
        set { _autoColors = value; Raise(); Raise(nameof(ManualColors)); Retrace(); }
    }
    public bool ManualColors => !_autoColors;

    double _colorCount = 12;
    public double ColorCount
    {
        get => _colorCount;
        set
        {
            var v = Math.Round(value);
            if (v == _colorCount) return;
            _colorCount = v;
            Raise();
            Raise(nameof(ColorCountText));
            Retrace();
        }
    }
    public string ColorCountText => ((int)_colorCount).ToString();

    int _styleIndex;   // matches ImageStyle enum order: Auto, Crisp, Blended, Photo
    public int StyleIndex
    {
        get => _styleIndex;
        set { _styleIndex = value; Raise(); Retrace(); }
    }

    int _detailIndex = 1;   // matches DetailLevel enum order: Low, Medium, High
    public int DetailIndex
    {
        get => _detailIndex;
        set { _detailIndex = value; Raise(); Retrace(); }
    }

    public string SuggestedName => Path.GetFileNameWithoutExtension(_sourcePath ?? "vecto") + ".svg";

    public VectorDocument? Document => _result?.Document;

    public string WindowTitle => _sourcePath == null ? "Vecto" : $"Vecto — {Path.GetFileName(_sourcePath)}";

    public void LoadPastedBitmap(BitmapSource bmp) => LoadBitmap(bmp, null);

    public void LoadFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();
        LoadBitmap(bmp, path);
    }

    public void LoadBitmap(BitmapSource bmp, string? path = null)
    {
        // convert first: if this throws, the previous image, file name and trace result stay untouched
        var raster = ImageInterop.ToRaster(bmp);
        if (bmp.CanFreeze) bmp.Freeze();
        _sourcePath = path;
        _raster = raster;
        SourceBitmap = bmp;
        Raise(nameof(WindowTitle));
        FitRequested?.Invoke();
        Retrace(immediate: true);
    }

    public void Retrace(bool immediate = false)
    {
        if (_raster == null) return;
        _debounce.Stop();
        if (immediate) StartTrace();
        else _debounce.Start();
    }

    TraceOptions BuildOptions() => new()
    {
        PaletteMode = _autoColors ? PaletteMode.Auto : PaletteMode.FixedCount,
        ColorCount = (int)_colorCount,
        Style = (ImageStyle)_styleIndex,
        Detail = (DetailLevel)_detailIndex,
    };

    async void StartTrace()
    {
        if (_raster == null) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var raster = _raster;
        var options = BuildOptions();
        Status = "Tracing…";
        try
        {
            var result = await Task.Run(() => Tracer.Trace(raster, options, cts.Token));
            var hint = await Task.Run(() => ImageHints.Analyze(raster));
            if (cts.IsCancellationRequested) return;
            _result = result;
            VectorImage = VectorRendering.ToDrawingImage(result.Document);
            SegmentationBitmap = VectorRendering.ToSegmentationBitmap(result);
            _wireframeImage = null;
            if (_rightMode == 2) RefreshWireframe();
            Swatches = result.Document.Palette.Select(p =>
            {
                var brush = new SolidColorBrush(Color.FromRgb(p.Color.R, p.Color.G, p.Color.B));
                brush.Freeze();
                return new PaletteSwatch
                {
                    Brush = brush,
                    Tooltip = $"#{p.Color.R:x2}{p.Color.G:x2}{p.Color.B:x2} — {p.PixelCount:N0} px",
                };
            }).ToList();
            var d = result.Diagnostics;
            Status = $"Traced in {d.TotalMs} ms — {result.Document.Palette.Count} colors, {d.RegionCount} regions, {d.NodeCount} nodes" + HintText(hint);
            StatsText = $"style {d.Params.Style}   detail {(DetailLevel)_detailIndex}\n" +
                        $"regions {d.RegionCount}   chains {d.ChainCount}   nodes {d.NodeCount}\n" +
                        $"palette {d.PaletteMs} ms   segment {d.SegmentMs} ms\n" +
                        $"boundaries {d.BoundaryMs} ms   curves {d.FitMs} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
    }

    static string HintText(ImageHints.Result h) => h.Hint switch
    {
        ImageHint.SmallPicture => $"\nTip: this picture is small ({h.ContentWidth} × {h.ContentHeight}). A bigger version gives a cleaner result.",
        ImageHint.SmoothGradients => "\nTip: smooth colour fades come out as bands of colour. Flat colours trace best.",
        _ => "",
    };

    public string BuildSvg() =>
        SvgWriter.Write((_result ?? throw new InvalidOperationException("no trace result yet")).Document);
}
