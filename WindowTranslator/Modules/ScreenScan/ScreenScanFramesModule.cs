using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using R3;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowTranslator.Controls;
using WindowTranslator.Modules;
using WindowTranslator.Modules.Ocr;
using WindowTranslator.Stores;
using DrawingSize = System.Drawing.Size;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace WindowTranslator.Modules.ScreenScan;

/// <summary>
/// デスクトップ上の独立したOCR領域を管理する。
/// </summary>
public sealed class ScreenScanFramesModule : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<ScreenScanFramesModule> logger;
    private readonly Dictionary<Guid, FrameEntry> frames = [];
    private readonly string settingsPath = Path.Combine(PathUtility.UserDir, "screen-scan-frames.json");
    private bool restored;
    private bool disposed;
    private bool editMode;

    public ScreenScanFramesModule(IServiceProvider serviceProvider, ILogger<ScreenScanFramesModule> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <summary>保存済みの領域を復元する。</summary>
    public void Restore()
    {
        if (this.restored || this.disposed)
        {
            return;
        }

        this.restored = true;
        if (!File.Exists(this.settingsPath))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<ScreenScanFrameGeometry>>(File.ReadAllText(this.settingsPath)) ?? [];
            foreach (var geometry in saved.Where(IsValid))
            {
                OpenFrame(geometry);
            }
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "画面OCR領域の設定を復元できませんでした。");
        }
    }

    /// <summary>選択したデスクトップ領域に新しいフレームを作成する。</summary>
    public void AddFrame(Rect bounds)
    {
        if (this.disposed || bounds.Width < 80 || bounds.Height < 40)
        {
            return;
        }

        this.editMode = true;
        foreach (var entry in this.frames.Values)
        {
            entry.Window.SetEditMode(true);
        }
        OpenFrame(new(Guid.NewGuid(), bounds.Left, bounds.Top, bounds.Width, bounds.Height));
    }

    private void OpenFrame(ScreenScanFrameGeometry geometry)
    {
        if (this.disposed || this.frames.ContainsKey(geometry.Id))
        {
            return;
        }

        var scope = this.serviceProvider.CreateScope();
        try
        {
            var processInfo = scope.ServiceProvider.GetRequiredService<IProcessInfoStoreInternal>();
            processInfo.SetTargetProcess(IntPtr.Zero, $"ScreenScanFrame-{geometry.Id:N}");

            var worker = new ScreenScanFrameWorker(scope.ServiceProvider, this.logger);
            var window = new ScreenScanFrameWindow(
                geometry,
                worker,
                UpdateGeometry,
                () => _ = CloseFrameAsync(geometry.Id),
                this.logger);

            this.frames.Add(geometry.Id, new(scope, worker, window, geometry));
            window.Show();
            window.SetEditMode(this.editMode);
            Save();
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public void ToggleEditMode()
    {
        this.editMode = !this.editMode;
        foreach (var entry in this.frames.Values)
        {
            entry.Window.SetEditMode(this.editMode);
        }
    }

    private void UpdateGeometry(ScreenScanFrameGeometry geometry)
    {
        if (!this.frames.TryGetValue(geometry.Id, out var entry))
        {
            return;
        }

        this.frames[geometry.Id] = entry with { Geometry = geometry };
        Save();
    }

    private async Task CloseFrameAsync(Guid id)
    {
        if (!this.frames.Remove(id, out var entry))
        {
            return;
        }

        try
        {
            await entry.Worker.DisposeAsync();
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "画面OCR領域の処理を終了できませんでした。");
        }
        finally
        {
            entry.Scope.Dispose();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(PathUtility.UserDir);
            var geometries = this.frames.Values.Select(x => x.Geometry).ToArray();
            File.WriteAllText(this.settingsPath, JsonSerializer.Serialize(geometries, JsonOptions));
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "画面OCR領域の設定を保存できませんでした。");
        }
    }

    private static bool IsValid(ScreenScanFrameGeometry geometry)
        => geometry.Id != Guid.Empty
        && double.IsFinite(geometry.Left)
        && double.IsFinite(geometry.Top)
        && double.IsFinite(geometry.Width)
        && double.IsFinite(geometry.Height)
        && geometry.Width >= 80
        && geometry.Height >= 40;

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (var entry in this.frames.Values.ToArray())
        {
            entry.Window.Close();
        }
    }

    private sealed record FrameEntry(
        IServiceScope Scope,
        ScreenScanFrameWorker Worker,
        ScreenScanFrameWindow Window,
        ScreenScanFrameGeometry Geometry);
}

/// <summary>
/// 画面上に配置するOCRフレームの位置と大きさ。
/// </summary>
public sealed record ScreenScanFrameGeometry(Guid Id, double Left, double Top, double Width, double Height);

/// <summary>
/// 1つのデスクトップOCRフレーム専用の認識・翻訳パイプライン。
/// </summary>
internal sealed class ScreenScanFrameWorker : IAsyncDisposable
{
    private readonly IOcrModule ocr;
    private readonly IOcrTextTracker tracker;
    private readonly ITranslateModule translator;
    private readonly ICacheModule cache;
    private readonly IColorModule color;
    private readonly IFilterModule[] filters;
    private readonly ILogger logger;
    private readonly double fontScale;
    private readonly double overlayOpacity;
    private readonly SemaphoreSlim captureLock = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();

    public ObservableCollection<TextRect> OcrTexts { get; } = [];
    public string Font { get; }
    public TimeSpan CaptureInterval { get; }
    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    public ScreenScanFrameWorker(IServiceProvider provider, ILogger logger)
    {
        var settings = provider.GetRequiredService<IOptionsSnapshot<TargetSettings>>().Value;
        var ocrSettings = provider.GetRequiredService<IOptionsSnapshot<BasicOcrParam>>().Value;

        this.ocr = provider.GetRequiredService<IOcrModule>();
        this.tracker = provider.GetRequiredService<IOcrTextTracker>();
        this.translator = provider.GetRequiredService<ITranslateModule>();
        this.cache = provider.GetRequiredService<ICacheModule>();
        this.color = provider.GetRequiredService<IColorModule>();
        this.filters = provider.GetServices<IFilterModule>().ToArray();
        this.logger = logger;
        this.Font = settings.Font;
        this.fontScale = settings.FontScale;
        this.overlayOpacity = settings.OverlayOpacity;
        this.CaptureInterval = settings.CaptureInterval > 0
            ? TimeSpan.FromSeconds(Math.Max(0.25, settings.CaptureInterval))
            : TimeSpan.FromMilliseconds(400);
    }

    public async Task CaptureAsync(Rect bounds, double dpiScaleX, double dpiScaleY)
    {
        if (bounds.Width < 40 || bounds.Height < 30 || this.cancellation.IsCancellationRequested)
        {
            return;
        }

        if (!await this.captureLock.WaitAsync(0, this.cancellation.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            using var bitmap = await ScreenCaptureUtility.CaptureAsync(
                bounds,
                dpiScaleX,
                dpiScaleY,
                this.cancellation.Token).ConfigureAwait(false);

            this.ImageWidth = bitmap.PixelWidth;
            this.ImageHeight = bitmap.PixelHeight;
            var imageSize = new DrawingSize(bitmap.PixelWidth, bitmap.PixelHeight);

            var regions = new[]
            {
                new OcrRegionInput(new RectInfo(0, 0, bitmap.PixelWidth, bitmap.PixelHeight))
            };
            var observations = await this.ocr.RecognizeAsync(new(bitmap, regions)).ConfigureAwait(false);
            this.cancellation.Token.ThrowIfCancellationRequested();

            IEnumerable<TextRect> texts = this.tracker.Update(observations, imageSize)
                .Select(text => text with { FontSize = text.FontSize * this.fontScale });
            texts = (await this.color.ConvertColorAsync(bitmap, texts).ConfigureAwait(false)).ToArray();
            texts = texts.Select(text => text switch
            {
                { TranslatedText: null } when this.cache.Contains(text.SourceText)
                    => text with { TranslatedText = this.cache.Get(text.SourceText) },
                _ => text,
            }).ToArray();

            var context = new FilterContext
            {
                SoftwareBitmap = bitmap,
                ImageSize = imageSize,
            };

            var preTranslate = texts.ToAsyncEnumerable();
            foreach (var filter in this.filters.OrderByDescending(filter => filter.Priority))
            {
                preTranslate = filter.ExecutePreTranslate(preTranslate, context);
            }
            texts = await preTranslate.ToArrayAsync(this.cancellation.Token).ConfigureAwait(false);

            var requests = texts.Where(text => text.TranslatedText is null).ToArray();
            if (requests.Length > 0)
            {
                var translated = await this.translator.TranslateAsync(requests).ConfigureAwait(false);
                this.cancellation.Token.ThrowIfCancellationRequested();
                this.cache.AddRange(requests.Select(text => text.SourceText).Zip(translated));
                var translatedIndex = 0;
                texts = texts.Select(text => text.TranslatedText is null
                    ? text with { TranslatedText = translated[translatedIndex++] }
                    : text).ToArray();
            }

            var postTranslate = texts.ToAsyncEnumerable();
            foreach (var filter in this.filters.OrderBy(filter => filter.Priority))
            {
                postTranslate = filter.ExecutePostTranslate(postTranslate, context);
            }
            texts = await postTranslate.ToArrayAsync(this.cancellation.Token).ConfigureAwait(false);
            this.cancellation.Token.ThrowIfCancellationRequested();

            var alpha = (int)Math.Clamp(255 * this.overlayOpacity, 0, 255);
            var result = texts
                .Select(text => text with { Background = DrawingColor.FromArgb(alpha, text.Background) })
                .ToArray();

            if (Application.Current is { } app && !app.Dispatcher.HasShutdownStarted)
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    this.OcrTexts.Clear();
                    foreach (var text in result)
                    {
                        this.OcrTexts.Add(text);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "デスクトップOCRフレームの認識または翻訳に失敗しました。");
        }
        finally
        {
            this.captureLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellation.Cancel();
        await this.captureLock.WaitAsync().ConfigureAwait(false);
        this.captureLock.Release();
        this.captureLock.Dispose();
        this.cancellation.Dispose();
    }
}

/// <summary>
/// デスクトップの指定位置だけをビットマップとして取得する。
/// </summary>
internal static class ScreenCaptureUtility
{
    public static Task<SoftwareBitmap> CaptureAsync(
        Rect bounds,
        double dpiScaleX,
        double dpiScaleY,
        CancellationToken cancellationToken)
    {
        var sourceX = (int)Math.Round(bounds.Left * dpiScaleX);
        var sourceY = (int)Math.Round(bounds.Top * dpiScaleY);
        var width = Math.Max(1, (int)Math.Round(bounds.Width * dpiScaleX));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * dpiScaleY));

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    sourceX,
                    sourceY,
                    0,
                    0,
                    new DrawingSize(width, height),
                    CopyPixelOperation.SourceCopy);
            }

            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Bmp);
            memory.Position = 0;
            using var stream = memory.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
        }, cancellationToken);
    }
}

/// <summary>
/// 枠をドラッグ移動・リサイズできる独立したデスクトップウィンドウ。
/// </summary>
internal sealed class ScreenScanFrameWindow : Window
{
    private const uint WindowDisplayAffinityExcludeFromCapture = 0x11;
    private readonly ScreenScanFrameGeometry geometry;
    private readonly ScreenScanFrameWorker worker;
    private readonly Action<ScreenScanFrameGeometry> geometryChanged;
    private readonly DispatcherTimer timer = new();
    private readonly ILogger logger;
    private readonly OverlayTextsControl overlay;
    private FrameworkElement titleBar = null!;
    private Thumb resizeThumb = null!;
    private int capturing;

    public ScreenScanFrameWindow(
        ScreenScanFrameGeometry geometry,
        ScreenScanFrameWorker worker,
        Action<ScreenScanFrameGeometry> geometryChanged,
        Action closed,
        ILogger logger)
    {
        this.geometry = geometry;
        this.worker = worker;
        this.geometryChanged = geometryChanged;
        this.logger = logger;

        Title = "WindowTranslator - Screen OCR";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 140;
        MinHeight = 90;
        Left = geometry.Left;
        Top = geometry.Top;
        Width = geometry.Width;
        Height = geometry.Height;

        this.overlay = new OverlayTextsControl
        {
            Texts = worker.OcrTexts,
            FontFamily = new FontFamily(worker.Font),
            IsHitTestVisible = false,
        };
        Content = BuildContent();

        this.timer.Interval = worker.CaptureInterval;
        this.timer.Tick += async (_, _) => await CaptureTickAsync();
        Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!SetWindowDisplayAffinity(handle, WindowDisplayAffinityExcludeFromCapture))
            {
                this.logger.LogWarning("画面OCRフレームを画面キャプチャから除外できませんでした。Win32 error: {Error}", Marshal.GetLastWin32Error());
            }
            SetEditMode(this.titleBar.Visibility == Visibility.Visible);
            this.timer.Start();
            _ = CaptureTickAsync();
        };
        Closed += (_, _) =>
        {
            this.timer.Stop();
            closed();
        };
    }

    private UIElement BuildContent()
    {
        var root = new Grid();

        this.overlay.SetCurrentValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        this.overlay.SetCurrentValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        Panel.SetZIndex(this.overlay, 0);
        root.Children.Add(this.overlay);

        var frameBorder = new Border
        {
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(235, 0, 190, 255)),
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(frameBorder, 1);
        root.Children.Add(frameBorder);

        var titleBar = new Border
        {
            Height = 25,
            Background = new SolidColorBrush(MediaColor.FromArgb(225, 18, 24, 32)),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.SizeAll,
        };
        var titlePanel = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 0, 3, 0) };
        var closeButton = new Button
        {
            Content = "×",
            Width = 25,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            ToolTip = "إغلاق إطار المسح",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(closeButton, Dock.Right);
        titlePanel.Children.Add(closeButton);
        titlePanel.Children.Add(new TextBlock
        {
            Text = "منطقة المسح",
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleBar.Child = titlePanel;
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.OriginalSource is Button)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            NotifyGeometryChanged();
        };
        Panel.SetZIndex(titleBar, 2);
        root.Children.Add(titleBar);
        this.titleBar = titleBar;

        var resizeThumb = new Thumb
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = new SolidColorBrush(MediaColor.FromArgb(240, 0, 190, 255)),
            Opacity = 0.9,
            ToolTip = "تغيير حجم الإطار",
        };
        resizeThumb.DragDelta += (_, e) =>
        {
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        };
        resizeThumb.DragCompleted += (_, _) => NotifyGeometryChanged();
        Panel.SetZIndex(resizeThumb, 3);
        root.Children.Add(resizeThumb);
        this.resizeThumb = resizeThumb;

        return root;
    }

    public void SetEditMode(bool enabled)
    {
        this.titleBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        this.resizeThumb.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, -20);
        const long transparent = 0x00000020;
        const long noActivate = 0x08000000;
        const long toolWindow = 0x00000080;
        style |= toolWindow;
        if (enabled)
        {
            style &= ~transparent;
            style &= ~noActivate;
        }
        else
        {
            style |= transparent;
            style |= noActivate;
        }
        SetWindowLong(handle, -20, style);
    }

    private async Task CaptureTickAsync()
    {
        if (Interlocked.Exchange(ref this.capturing, 1) != 0 || !IsLoaded)
        {
            return;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            await this.worker.CaptureAsync(bounds, dpi.DpiScaleX, dpi.DpiScaleY);
            this.overlay.RectWidth = this.worker.ImageWidth;
            this.overlay.RectHeight = this.worker.ImageHeight;
        }
        finally
        {
            Interlocked.Exchange(ref this.capturing, 0);
        }
    }

    private void NotifyGeometryChanged()
        => this.geometryChanged(this.geometry with
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
        });

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}

/// <summary>
/// 画面全体を一時的に覆い、ドラッグした範囲を選択する。
/// </summary>
public sealed class ScreenScanSelectionWindow : Window
{
    private readonly Canvas canvas = new();
    private readonly System.Windows.Shapes.Rectangle selection = new()
    {
        Fill = new SolidColorBrush(MediaColor.FromArgb(65, 0, 190, 255)),
        Stroke = new SolidColorBrush(MediaColor.FromArgb(255, 0, 210, 255)),
        StrokeThickness = 2,
        StrokeDashArray = new DoubleCollection([4, 2]),
        Visibility = Visibility.Collapsed,
    };
    private Point startPoint;
    private bool selecting;

    public Rect? SelectedBounds { get; private set; }

    public ScreenScanSelectionWindow()
    {
        Title = "تحديد منطقة المسح";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(MediaColor.FromArgb(95, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        this.canvas.Background = Brushes.Transparent;
        this.canvas.Cursor = Cursors.Cross;
        this.canvas.Children.Add(this.selection);

        var instruction = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromArgb(220, 18, 24, 32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            Child = new TextBlock
            {
                Text = "اسحب لتحديد المنطقة المراد مسحها — اضغط Esc للإلغاء",
                Foreground = Brushes.White,
                FontSize = 15,
            },
        };
        var host = new Grid();
        host.Children.Add(this.canvas);
        host.Children.Add(instruction);
        Content = host;

        this.canvas.MouseLeftButtonDown += CanvasMouseLeftButtonDown;
        this.canvas.MouseMove += CanvasMouseMove;
        this.canvas.MouseLeftButtonUp += CanvasMouseLeftButtonUp;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void CanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        this.startPoint = e.GetPosition(this.canvas);
        this.selecting = true;
        this.canvas.CaptureMouse();
        this.selection.Visibility = Visibility.Visible;
        UpdateSelection(e.GetPosition(this.canvas));
    }

    private void CanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (this.selecting)
        {
            UpdateSelection(e.GetPosition(this.canvas));
        }
    }

    private void CanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!this.selecting)
        {
            return;
        }

        this.selecting = false;
        this.canvas.ReleaseMouseCapture();
        var left = Canvas.GetLeft(this.selection);
        var top = Canvas.GetTop(this.selection);
        var width = this.selection.Width;
        var height = this.selection.Height;
        if (width < 80 || height < 40)
        {
            this.selection.Visibility = Visibility.Collapsed;
            return;
        }

        this.SelectedBounds = new Rect(
            SystemParameters.VirtualScreenLeft + left,
            SystemParameters.VirtualScreenTop + top,
            width,
            height);
        DialogResult = true;
        Close();
    }

    private void UpdateSelection(Point currentPoint)
    {
        var left = Math.Clamp(Math.Min(this.startPoint.X, currentPoint.X), 0, this.canvas.ActualWidth);
        var top = Math.Clamp(Math.Min(this.startPoint.Y, currentPoint.Y), 0, this.canvas.ActualHeight);
        var right = Math.Clamp(Math.Max(this.startPoint.X, currentPoint.X), 0, this.canvas.ActualWidth);
        var bottom = Math.Clamp(Math.Max(this.startPoint.Y, currentPoint.Y), 0, this.canvas.ActualHeight);

        Canvas.SetLeft(this.selection, left);
        Canvas.SetTop(this.selection, top);
        this.selection.Width = right - left;
        this.selection.Height = bottom - top;
    }
}
