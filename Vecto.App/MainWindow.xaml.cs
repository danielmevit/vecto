using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vecto.Core;

namespace Vecto.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    readonly AppSettings _settings;
    bool _syncingScroll;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        _settings = AppSettings.Load();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        ViewModel.AutoColors = _settings.AutoColors;
        ViewModel.ColorCount = _settings.ColorCount;
        ViewModel.StyleIndex = _settings.StyleIndex;
        ViewModel.DetailIndex = _settings.DetailIndex;
        ViewModel.FitRequested += () =>
            Dispatcher.BeginInvoke(FitZoom, System.Windows.Threading.DispatcherPriority.Loaded);
        Closing += (_, _) =>
        {
            _settings.AutoColors = ViewModel.AutoColors;
            _settings.ColorCount = (int)ViewModel.ColorCount;
            _settings.StyleIndex = ViewModel.StyleIndex;
            _settings.DetailIndex = ViewModel.DetailIndex;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }
            _settings.Save();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE) so the chrome matches the theme
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void TryLoad(string path)
    {
        try
        {
            ViewModel.LoadFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Vecto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) == true) TryLoad(dlg.FileName);
    }

    void OnPaste(object sender, RoutedEventArgs e)
    {
        BitmapSource? bmp = null;
        try
        {
            if (!Clipboard.ContainsImage())
            {
                ViewModel.Status = "Clipboard has no image.";
                return;
            }
            var img = Clipboard.GetImage();
            if (img != null) bmp = BitmapFrame.Create(img);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            ViewModel.Status = "Clipboard is busy — try again in a moment.";
            return;
        }
        if (bmp == null)
        {
            ViewModel.Status = "Clipboard has no usable image.";
            return;
        }
        try
        {
            ViewModel.LoadPastedBitmap(bmp);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Vecto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    System.Windows.Point _panStart;
    double _panH, _panV;
    ScrollViewer? _panning;

    void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = (ScrollViewer)sender;
        _panStart = e.GetPosition(_panning);
        _panH = _panning.HorizontalOffset;
        _panV = _panning.VerticalOffset;
        _panning.CaptureMouse();
        e.Handled = true;
    }

    void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panning == null) return;
        var p = e.GetPosition(_panning);
        _panning.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
        _panning.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
    }

    void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _panning == null) return;
        _panning.ReleaseMouseCapture();
        _panning = null;
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasResult) return;
        var dlg = new SaveFileDialog { Filter = "SVG|*.svg", FileName = ViewModel.SuggestedName };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ViewModel.BuildSvg());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.Status = "Could not export: " + ex.Message;
            return;
        }
        ViewModel.Status = "Exported " + dlg.FileName;
    }

    void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is not { } doc) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = Path.ChangeExtension(ViewModel.SuggestedName, ".png"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = VectorRendering.ToBitmapSource(Rasterizer.Render(doc));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var stream = File.Create(dlg.FileName))
                encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.Status = "Could not export: " + ex.Message;
            return;
        }
        ViewModel.Status = "Exported " + dlg.FileName;
    }

    void OnExportPdf(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is not { } doc) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF|*.pdf",
            FileName = Path.ChangeExtension(ViewModel.SuggestedName, ".pdf"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            PdfWriter.Write(doc, dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.Status = "Could not export: " + ex.Message;
            return;
        }
        ViewModel.Status = "Exported " + dlg.FileName;
    }

    void OnCopySvg(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasResult) return;
        try
        {
            Clipboard.SetText(ViewModel.BuildSvg());
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            ViewModel.Status = "Clipboard is busy — try again in a moment.";
            return;
        }
        ViewModel.Status = "SVG copied to clipboard.";
    }

    void OnZoomIn(object sender, RoutedEventArgs e) => ViewModel.Zoom *= 1.25;
    void OnZoomOut(object sender, RoutedEventArgs e) => ViewModel.Zoom /= 1.25;
    void OnZoomActual(object sender, RoutedEventArgs e) => ViewModel.Zoom = 1;
    void OnZoomFit(object sender, RoutedEventArgs e) => FitZoom();

    void FitZoom()
    {
        var bmp = ViewModel.SourceBitmap;
        if (bmp == null) return;
        double vw = LeftScroll.ViewportWidth, vh = LeftScroll.ViewportHeight;
        if (vw < 1 || vh < 1)
        {
            vw = LeftScroll.ActualWidth;
            vh = LeftScroll.ActualHeight;
        }
        if (vw < 1 || vh < 1) return;
        double fit = Math.Min((vw - 24) / bmp.PixelWidth, (vh - 24) / bmp.PixelHeight);
        ViewModel.Zoom = Math.Min(8, fit);
    }

    void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        ViewModel.Zoom *= e.Delta > 0 ? 1.25 : 0.8;
    }

    void OnLeftScrollChanged(object sender, ScrollChangedEventArgs e) => SyncScroll(LeftScroll, RightScroll, e);
    void OnRightScrollChanged(object sender, ScrollChangedEventArgs e) => SyncScroll(RightScroll, LeftScroll, e);

    void SyncScroll(ScrollViewer from, ScrollViewer to, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (e.HorizontalChange == 0 && e.VerticalChange == 0) return;
        _syncingScroll = true;
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        to.ScrollToVerticalOffset(from.VerticalOffset);
        _syncingScroll = false;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) TryLoad(files[0]);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.O:
                OnOpen(sender, e);
                e.Handled = true;
                break;
            case Key.V:
                OnPaste(sender, e);
                e.Handled = true;
                break;
            case Key.S:
                OnExport(sender, e);
                e.Handled = true;
                break;
        }
    }
}
