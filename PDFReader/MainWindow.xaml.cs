using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Util;

namespace PDFReader;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const int DwmWindowCornerDoNotRound = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint ShowWindow = 0x0040;
    private const uint MoveWithoutResizing = 0x0001 | 0x0004 | 0x0010;
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFReader", "window-settings.json");
    private readonly DispatcherTimer _resizeTimer;
    private readonly List<BitmapSource?> _displayedPages = [];
    private readonly List<PageTextData?> _pageText = [];
    private readonly List<Image> _pageImages = [];
    private readonly List<Canvas> _selectionLayers = [];
    private int _currentPage;
    private int _totalPages;
    private int _openRequestVersion;
    private int _renderRequestVersion;
    private string? _currentPath;
    private BitmapSource? _displayedPage;
    private bool _isDocumentDark;
    private bool _hasSavedGeometry;
    private bool _restoreMaximized;
    private bool _showVerticalScrollBar = true;
    private Point? _documentPressStart;
    private Point? _windowDragOffset;
    private int _selectionPage = -1;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private double _pageAspectRatio = 378d / 539d;
    private double _zoomFactor = 1;
    private int _rotation;
    private string? _documentPassword;
    private bool _isInternetDocument;
    private readonly List<string> _recentFiles = [];
    private readonly List<(int Page, int Word)> _findResults = [];
    private int _findResultIndex = -1;
    private IReadOnlyList<string> _folderPdfs = [];
    private int _folderPdfIndex = -1;
    private bool _pageKeysNavigatePdfs;
    private bool _isFullscreen;
    private Rect _fullscreenRestoreBounds;
    private WindowState _fullscreenRestoreState;
    private readonly Dictionary<string, int> _lastPages = new(StringComparer.OrdinalIgnoreCase);
    private bool _rememberLastPage;

    public MainWindow(string? startupPath = null)
    {
        InitializeComponent();
        LoadWindowSettings();
        RebuildRecentFilesMenu();
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _resizeTimer.Tick += async (_, _) =>
        {
            _resizeTimer.Stop();
            await RenderDocumentAsync(showLoading: false);
        };
        if (!string.IsNullOrWhiteSpace(startupPath))
        {
            Loaded += (_, _) => OpenPdf(startupPath);
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir documento PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true) OpenPdf(dialog.FileName);
    }

    private async void OpenPdf(string path, string? password = null, bool preserveView = false)
    {
        if (!IsPdf(path))
        {
            ShowReaderError("No se pudo abrir el documento", "Selecciona un archivo PDF válido.");
            return;
        }

        RememberCurrentPage();
        var requestVersion = ++_openRequestVersion;
        var fullPath = Path.GetFullPath(path);
        var rememberedPage = _rememberLastPage ? _lastPages.GetValueOrDefault(fullPath, 1) : 1;
        _isInternetDocument = IsInternetOrigin(fullPath);
        _documentPassword = password;
        if (!preserveView)
        {
            _rotation = 0;
            _zoomFactor = 1;
            ZoomText.Text = "100%";
        }
        RefreshFolderPdfs(fullPath);
        _currentPath = fullPath;
        _currentPage = 1;
        _totalPages = 0;
        DocumentTitle.Text = Path.GetFileName(fullPath);
        DocumentPath.Text = fullPath;
        Title = $"{Path.GetFileName(fullPath)} — Lector PDF";
        StatusText.Text = "Cargando documento…";
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        UpdatePageControls();

        try
        {
            var documentInfo = await Task.Run(() =>
            {
                using var document = OpenPdfPig(fullPath, password);
                var firstPage = document.GetPage(1);
                return (Count: document.NumberOfPages, AspectRatio: (double)firstPage.Width / (double)firstPage.Height);
            });
            if (requestVersion != _openRequestVersion || !string.Equals(_currentPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            _totalPages = Math.Max(1, documentInfo.Count);
            _pageAspectRatio = documentInfo.AspectRatio is > 0.2 and < 5 ? documentInfo.AspectRatio : 378d / 539d;
            _currentPage = Math.Clamp(rememberedPage, 1, _totalPages);
            SizeWindowForPage();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            UpdatePageControls();
            await RenderDocumentAsync();
            AddRecentFile(fullPath);
        }
        catch (PdfDocumentEncryptedException)
        {
            if (requestVersion != _openRequestVersion) return;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            var entered = PromptForPassword(Path.GetFileName(fullPath));
            if (entered is not null)
            {
                if (_rememberLastPage) _lastPages[fullPath] = rememberedPage;
                OpenPdf(fullPath, entered);
            }
            else ShowReaderError("Se necesita una contraseña", "No se abrió el documento protegido.");
        }
        catch
        {
            if (requestVersion == _openRequestVersion)
                ShowReaderError("No se pudo leer el documento", "Comprueba que el archivo PDF no esté dañado o protegido.");
        }
    }

    private static PdfDocument OpenPdfPig(string path, string? password) => string.IsNullOrEmpty(password)
        ? PdfDocument.Open(path)
        : PdfDocument.Open(path, new ParsingOptions { Password = password });

    private async void NavigateToPage(int page)
    {
        if (_currentPath is null || _totalPages < 1) return;
        var target = Math.Clamp(page, 1, _totalPages);
        if (target != _currentPage)
        {
            _currentPage = target;
            RememberCurrentPage();
            UpdateReadingStatus();
            ScrollToPage(target);
        }
        UpdatePageControls();
    }

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(_currentPage - 1);
    private void NextPageButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(_currentPage + 1);

    private void PageNumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTypedPage();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PageNumberBox.Text = _currentPage > 0 ? _currentPage.ToString() : "—";
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void PageNumberBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTypedPage();

    private void CommitTypedPage()
    {
        if (int.TryParse(PageNumberBox.Text, out var page)) NavigateToPage(page);
        else PageNumberBox.Text = _currentPage > 0 ? _currentPage.ToString() : "—";
    }

    private void UpdatePageControls()
    {
        var ready = _totalPages > 0;
        PageNumberBox.IsEnabled = ready;
        PageNumberBox.Text = ready ? _currentPage.ToString() : "—";
        PageCountText.Text = ready ? $"/ {_totalPages}" : "/ —";
        PreviousPageButton.IsEnabled = ready && _currentPage > 1;
        NextPageButton.IsEnabled = ready && _currentPage < _totalPages;
        PreviousPdfButton.IsEnabled = ready && _folderPdfIndex > 0;
        NextPdfButton.IsEnabled = ready && _folderPdfIndex >= 0 && _folderPdfIndex < _folderPdfs.Count - 1;
        PdfPositionText.Text = ready && _folderPdfIndex >= 0
            ? $"{_folderPdfIndex + 1} / {_folderPdfs.Count}"
            : "— / —";
        DeletePdfButton.IsEnabled = ready && _currentPath is not null;
        DocumentThemeButton.IsEnabled = ready && _displayedPage is not null;
        CopyPageButton.IsEnabled = ready && _displayedPage is not null;
        SaveInternetButton.IsEnabled = ready && _isInternetDocument;
    }

    private void ShowReaderError(string title, string description)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        EmptyStateTitle.Text = title;
        EmptyStateDescription.Text = description;
        EmptyStateAction.Content = "Elegir otro PDF";
        EmptyState.Visibility = Visibility.Visible;
        StatusText.Text = title;
        _currentPath = null;
        _isInternetDocument = false;
        _displayedPage = null;
        PagesPanel.Children.Clear();
        _displayedPages.Clear();
        _totalPages = 0;
        _folderPdfs = [];
        _folderPdfIndex = -1;
        UpdatePageControls();
    }

    private async Task RenderDocumentAsync(bool showLoading = true)
    {
        if (_currentPath is null || _totalPages < 1 || !File.Exists(_currentPath)) return;

        var requestVersion = ++_renderRequestVersion;
        var path = _currentPath;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var availableWidth = DocumentViewport.ViewportWidth > 0 ? DocumentViewport.ViewportWidth : ActualWidth;
        var pixelWidth = Math.Clamp((int)Math.Ceiling(Math.Max(MinWidth, availableWidth) * _zoomFactor * dpi), 360, 4000);
        if (showLoading) LoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            EnsurePageSurfaces();
            var firstIndex = Math.Clamp(_currentPage - 1, 0, _totalPages - 1);
            // Position the lightweight page surfaces before revealing any bitmap.
            // This prevents page 1 from flashing while the remembered page renders.
            PagesPanel.UpdateLayout();
            ScrollToPage(firstIndex + 1);
            if (!await RenderPageImageAsync(path, firstIndex, pixelWidth, requestVersion)) return;
            _currentPage = firstIndex + 1;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            UpdateReadingStatus();
            UpdatePageControls();

            await LoadPageTextAsync(path, firstIndex, requestVersion);
            for (var pageIndex = 0; pageIndex < _totalPages; pageIndex++)
            {
                if (pageIndex == firstIndex) continue;
                if (!await RenderPageImageAsync(path, pageIndex, pixelWidth, requestVersion)) return;
                await LoadPageTextAsync(path, pageIndex, requestVersion);
            }
        }
        catch (Exception exception)
        {
            if (requestVersion == _renderRequestVersion)
                ShowReaderError("No se pudo mostrar el documento", exception.Message);
        }
        finally
        {
            if (requestVersion == _renderRequestVersion) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsurePageSurfaces()
    {
        if (PagesPanel.Children.Count == _totalPages && _pageImages.Count == _totalPages) return;
        PagesPanel.Children.Clear();
        _displayedPages.Clear();
        _pageText.Clear();
        _pageImages.Clear();
        _selectionLayers.Clear();
        ClearTextSelection();
        var width = Math.Max(1, DocumentViewport.ViewportWidth) * _zoomFactor;
        for (var index = 0; index < _totalPages; index++)
        {
            var pageImage = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false };
            var selectionLayer = new Canvas { Background = Brushes.Transparent };
            var initialRatio = _rotation % 2 == 0 ? _pageAspectRatio : 1 / _pageAspectRatio;
            var surface = new Grid { Width = width, Height = width / initialRatio,
                Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
            surface.Children.Add(pageImage);
            surface.Children.Add(selectionLayer);
            _displayedPages.Add(null);
            _pageText.Add(null);
            _pageImages.Add(pageImage);
            _selectionLayers.Add(selectionLayer);
            PagesPanel.Children.Add(surface);
        }
    }

    private async Task<bool> RenderPageImageAsync(string path, int pageIndex, int pixelWidth, int requestVersion)
    {
        var displayed = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            using var bitmap = Conversion.ToImage(stream, page: pageIndex, leaveOpen: false,
                password: _documentPassword,
                options: new PDFtoImage.RenderOptions(Width: pixelWidth, WithAspectRatio: true,
                    WithAnnotations: true, WithFormFill: true, Rotation: RotationForRender()));
            var source = CreateBitmapSource(bitmap);
            return _isDocumentDark ? CreateDarkDocumentBitmap(source) : source;
        });
        if (requestVersion != _renderRequestVersion || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) return false;
        _displayedPages[pageIndex] = displayed;
        _pageImages[pageIndex].Source = displayed;
        if (PagesPanel.Children[pageIndex] is Grid surface)
            surface.Height = surface.Width * displayed.PixelHeight / displayed.PixelWidth;
        if (pageIndex == _currentPage - 1) _displayedPage = displayed;
        return true;
    }

    private PdfRotation RotationForRender() => _rotation switch
    {
        1 => PdfRotation.Rotate90,
        2 => PdfRotation.Rotate180,
        3 => PdfRotation.Rotate270,
        _ => PdfRotation.Rotate0
    };

    private async Task LoadPageTextAsync(string path, int pageIndex, int requestVersion)
    {
        var textData = await Task.Run(() =>
        {
            using var document = OpenPdfPig(path, _documentPassword);
            var page = document.GetPage(pageIndex + 1);
            var words = RepairFragmentedWords(page.GetWords().Select(word => new TextWord(word.Text,
                word.BoundingBox.Left, word.BoundingBox.Bottom, word.BoundingBox.Width, word.BoundingBox.Height)));
            return new PageTextData((double)page.Width, (double)page.Height, words);
        });
        if (requestVersion != _renderRequestVersion || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _pageText[pageIndex] = textData;
        if (FindPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(FindTextBox.Text))
            RefreshFindResults();
    }

    internal static IReadOnlyList<TextWord> RepairFragmentedWords(IEnumerable<TextWord> source)
    {
        var repaired = new List<TextWord>();
        foreach (var word in source.Where(item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            if (repaired.Count == 0)
            {
                repaired.Add(word);
                continue;
            }

            var previous = repaired[^1];
            var gap = word.Left - (previous.Left + previous.Width);
            var sharedHeight = Math.Min(previous.Height, word.Height);
            var sameLine = Math.Abs(previous.Bottom - word.Bottom) <= Math.Max(0.5, sharedHeight * 0.2);
            var tinyGap = gap >= -Math.Max(0.5, sharedHeight * 0.1) &&
                          gap <= Math.Max(0.35, sharedHeight * 0.07);
            var continuesRight = word.Left >= previous.Left;

            if (!sameLine || !tinyGap || !continuesRight)
            {
                repaired.Add(word);
                continue;
            }

            var right = Math.Max(previous.Left + previous.Width, word.Left + word.Width);
            var bottom = Math.Min(previous.Bottom, word.Bottom);
            var top = Math.Max(previous.Bottom + previous.Height, word.Bottom + word.Height);
            repaired[^1] = new TextWord(previous.Text + word.Text, previous.Left, bottom,
                right - previous.Left, top - bottom);
        }

        return repaired;
    }

    private static BitmapSource CreateBitmapSource(SKBitmap bitmap)
    {
        var stride = bitmap.RowBytes;
        var pixels = bitmap.GetPixelSpan().ToArray();
        var format = bitmap.ColorType == SKColorType.Bgra8888
            ? (bitmap.AlphaType == SKAlphaType.Premul ? PixelFormats.Pbgra32 : PixelFormats.Bgra32)
            : PixelFormats.Bgra32;

        if (bitmap.ColorType == SKColorType.Rgba8888)
        {
            for (var index = 0; index < pixels.Length; index += 4)
                (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }

        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, format, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateDarkDocumentBitmap(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var blue = pixels[index] / 255d;
            var green = pixels[index + 1] / 255d;
            var red = pixels[index + 2] / 255d;
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            var chroma = maximum - minimum;
            var luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;

            if (chroma < 0.12)
            {
                // Map neutral paper and ink to comfortable charcoal and soft-white tones.
                var tone = 0.11 + (1d - luminance) * 0.80;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = ToByte(tone);
                continue;
            }

            // Preserve chromatic identity instead of RGB inversion. Bright colors are softened,
            // while dark saturated colors receive enough lift to remain legible on charcoal.
            var targetLuminance = Math.Clamp(luminance, 0.30, 0.72);
            var scale = targetLuminance / Math.Max(0.01, luminance);
            red = Math.Clamp(red * scale, 0, 1);
            green = Math.Clamp(green * scale, 0, 1);
            blue = Math.Clamp(blue * scale, 0, 1);
            var average = (red + green + blue) / 3d;
            const double saturation = 0.88;
            pixels[index + 2] = ToByte(average + (red - average) * saturation);
            pixels[index + 1] = ToByte(average + (green - average) * saturation);
            pixels[index] = ToByte(average + (blue - average) * saturation);
        }

        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);


    private async void DocumentThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _isDocumentDark = !_isDocumentDark;
        DocumentThemeGlyph.Text = _isDocumentDark ? "\uE706" : "\uE708";
        DocumentThemeButton.ToolTip = _isDocumentDark ? "Mostrar documento claro" : "Mostrar documento oscuro";
        await RenderDocumentAsync(showLoading: false);
    }

    private void CopyPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedPage is null) return;
        Clipboard.SetImage(_displayedPage);
        StatusText.Text = $"Página {_currentPage} copiada como imagen";
    }

    private void DocumentViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDisplayedPageWidths(Math.Max(1, e.NewSize.Width) * _zoomFactor);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            UpdateDisplayedPageWidths(Math.Max(1, DocumentViewport.ViewportWidth) * _zoomFactor));
        if (_currentPath is null || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 12) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void UpdateDisplayedPageWidths(double width)
    {
        for (var index = 0; index < PagesPanel.Children.Count; index++)
            if (PagesPanel.Children[index] is Grid pageSurface)
            {
                pageSurface.Width = width;
                var ratio = index < _pageText.Count && _pageText[index] is PageTextData text
                    ? text.Width / text.Height
                    : _pageAspectRatio;
                if (_rotation % 2 != 0) ratio = 1 / ratio;
                pageSurface.Height = width / ratio;
            }
        DrawTextSelection();
    }

    private void DocumentViewport_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (PagesPanel.Children.Count == 0) return;
        var visiblePage = GetVisiblePage();
        if (_currentPage == visiblePage) return;
        _currentPage = visiblePage;
        RememberCurrentPage();
        _displayedPage = _displayedPages.ElementAtOrDefault(_currentPage - 1);
        UpdateReadingStatus();
        UpdatePageControls();
    }

    private int GetVisiblePage()
    {
        if (PagesPanel.Children.Count == 0) return Math.Max(1, _currentPage);
        var viewportCenter = DocumentViewport.VerticalOffset + DocumentViewport.ViewportHeight / 2;
        double top = 0;
        var visiblePage = 1;
        for (var index = 0; index < PagesPanel.Children.Count; index++)
        {
            if (PagesPanel.Children[index] is not FrameworkElement page) continue;
            var bottom = top + page.ActualHeight + page.Margin.Top + page.Margin.Bottom;
            if (viewportCenter < bottom) { visiblePage = index + 1; break; }
            top = bottom;
            visiblePage = index + 1;
        }
        return visiblePage;
    }

    private void ScrollToPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PagesPanel.Children.Count) return;
        double offset = 0;
        for (var index = 0; index < pageNumber - 1; index++)
        {
            if (PagesPanel.Children[index] is FrameworkElement page)
                offset += page.ActualHeight + page.Margin.Top + page.Margin.Bottom;
        }
        DocumentViewport.ScrollToVerticalOffset(offset);
    }

    private void DocumentViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ClearTextSelection();
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && Keyboard.Modifiers == ModifierKeys.None)
        {
            _documentPressStart = e.GetPosition(this);
            DocumentViewport.CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryHitTextWord(e, out var pageIndex, out var wordIndex))
            {
                _selectionPage = pageIndex;
                _selectionAnchor = _selectionEnd = wordIndex;
                DocumentViewport.CaptureMouse();
                DrawTextSelection();
                e.Handled = true;
            }
            else ClearTextSelection();
        }
    }

    private void DocumentViewport_MouseMove(object sender, MouseEventArgs e)
    {
        UpdateScrollBarHover(e.GetPosition(DocumentViewport).X);
        if (_selectionPage >= 0 && e.LeftButton == MouseButtonState.Pressed &&
            TryHitTextWord(e, out var pageIndex, out var wordIndex) && pageIndex == _selectionPage)
        {
            _selectionEnd = wordIndex;
            DrawTextSelection();
            e.Handled = true;
            return;
        }
        if (_documentPressStart is Point start && e.MiddleButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition(this);
            if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (WindowState != WindowState.Normal) { EndDocumentWindowDrag(); return; }
            var origin = PointToScreen(new Point());
            var pressed = PointToScreen(start);
            _windowDragOffset = new Point(pressed.X - origin.X, pressed.Y - origin.Y);
            _documentPressStart = null;
            Cursor = Cursors.Hand;
        }

        if (_windowDragOffset is not Point offset) return;
        if (e.MiddleButton != MouseButtonState.Pressed) { EndDocumentWindowDrag(); return; }
        var pointer = PointToScreen(e.GetPosition(this));
        _ = SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Round(pointer.X - offset.X), (int)Math.Round(pointer.Y - offset.Y),
            0, 0, MoveWithoutResizing);
        e.Handled = true;
    }

    private void DocumentViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { EndDocumentWindowDrag(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.Left && _selectionPage >= 0)
        {
            if (Mouse.Captured == DocumentViewport) Mouse.Capture(null);
            e.Handled = true;
        }
    }

    private void DocumentViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _documentPressStart = null;
        _windowDragOffset = null;
        Cursor = null;
    }

    private void DocumentViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (GetVerticalScrollBar() is { IsMouseCaptureWithin: false } scrollBar)
            scrollBar.Opacity = 0;
    }

    private void UpdateScrollBarHover(double pointerX)
    {
        if (GetVerticalScrollBar() is not { } scrollBar) return;
        var hoverThreshold = DocumentViewport.ActualWidth * 0.75;
        scrollBar.Opacity = pointerX >= hoverThreshold || scrollBar.IsMouseCaptureWithin ? 1 : 0;
    }

    private ScrollBar? GetVerticalScrollBar() =>
        DocumentViewport.Template.FindName("PART_VerticalScrollBar", DocumentViewport) as ScrollBar;

    private bool TryHitTextWord(MouseEventArgs e, out int pageIndex, out int wordIndex)
    {
        pageIndex = wordIndex = -1;
        if (_rotation != 0) return false;
        for (var index = 0; index < PagesPanel.Children.Count && index < _pageText.Count; index++)
        {
            if (PagesPanel.Children[index] is not Grid surface || surface.ActualWidth <= 0) continue;
            var point = e.GetPosition(surface);
            if (point.X < 0 || point.Y < 0 || point.X > surface.ActualWidth || point.Y > surface.ActualHeight) continue;
            if (_pageText[index] is not PageTextData data) return false;
            var scale = surface.ActualWidth / data.Width;
            var nearestDistance = double.MaxValue;
            for (var word = 0; word < data.Words.Count; word++)
            {
                var item = data.Words[word];
                var rect = new Rect(item.Left * scale, (data.Height - item.Bottom - item.Height) * scale,
                    Math.Max(2, item.Width * scale), Math.Max(3, item.Height * scale));
                if (rect.Contains(point)) { pageIndex = index; wordIndex = word; return true; }
                var dx = Math.Max(rect.Left - point.X, Math.Max(0, point.X - rect.Right));
                var dy = Math.Max(rect.Top - point.Y, Math.Max(0, point.Y - rect.Bottom));
                var distance = dx * dx + dy * dy;
                if (distance < nearestDistance && distance < 144) { nearestDistance = distance; pageIndex = index; wordIndex = word; }
            }
            return wordIndex >= 0;
        }
        return false;
    }

    private void DrawTextSelection()
    {
        foreach (var layer in _selectionLayers) layer.Children.Clear();
        if (_rotation != 0) return;
        if (_selectionPage < 0 || _selectionPage >= _pageText.Count || _selectionPage >= PagesPanel.Children.Count) return;
        if (PagesPanel.Children[_selectionPage] is not Grid surface) return;
        if (_pageText[_selectionPage] is not PageTextData data) return;
        var scale = surface.ActualWidth / data.Width;
        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);
        for (var index = first; index <= last && index < data.Words.Count; index++)
        {
            var word = data.Words[index];
            var highlight = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(105, 0, 120, 215)),
                Width = Math.Max(2, word.Width * scale), Height = Math.Max(3, word.Height * scale), IsHitTestVisible = false };
            Canvas.SetLeft(highlight, word.Left * scale);
            Canvas.SetTop(highlight, (data.Height - word.Bottom - word.Height) * scale);
            _selectionLayers[_selectionPage].Children.Add(highlight);
        }
    }

    private void ClearTextSelection()
    {
        _selectionPage = _selectionAnchor = _selectionEnd = -1;
        foreach (var layer in _selectionLayers) layer.Children.Clear();
    }

    private void CopySelectedText()
    {
        if (_selectionPage < 0 || _selectionPage >= _pageText.Count) return;
        if (_pageText[_selectionPage] is not PageTextData textData) return;
        var words = textData.Words;
        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Min(words.Count - 1, Math.Max(_selectionAnchor, _selectionEnd));
        if (first < 0 || last < first) return;
        Clipboard.SetText(string.Join(" ", words.Skip(first).Take(last - first + 1).Select(word => word.Text)));
        StatusText.Text = "Texto copiado";
    }

    private void CopySelectedTextMenuItem_Click(object sender, RoutedEventArgs e) => CopySelectedText();

    private void EndDocumentWindowDrag()
    {
        _documentPressStart = null;
        _windowDragOffset = null;
        Cursor = null;
        if (Mouse.Captured == DocumentViewport) Mouse.Capture(null);
    }

    private void ApplyVerticalScrollBarVisibility()
    {
        // The custom ScrollViewer template overlays its bars on the document.
        // Their transparent hit strip reveals the thumb only when hovered.
        DocumentViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void LoadWindowSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(_settingsPath));
            if (settings is not null)
            {
                _showVerticalScrollBar = settings.ShowVerticalScrollBar ?? true;
                _pageKeysNavigatePdfs = settings.PageKeysNavigatePdfs;
                _rememberLastPage = settings.RememberLastPage;
                if (settings.LastPages is not null)
                    foreach (var item in settings.LastPages)
                        if (item.Value > 0) _lastPages[item.Key] = item.Value;
                ApplyVerticalScrollBarVisibility();
                _recentFiles.AddRange(settings.RecentFiles?.Where(File.Exists).Take(10) ?? []);
                RebuildRecentFilesMenu();
                if (settings.Height is not > 0 || settings.Left is null || settings.Top is null) return;

                var virtualArea = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                var width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, virtualArea.Width));
                var height = Math.Clamp(settings.Height.Value, MinHeight, Math.Max(MinHeight, virtualArea.Height));
                var savedBounds = new Rect(settings.Left.Value, settings.Top.Value, width, height);
                if (!savedBounds.IntersectsWith(virtualArea))
                {
                    var workArea = SystemParameters.WorkArea;
                    savedBounds.X = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
                    savedBounds.Y = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
                }
                else
                {
                    savedBounds.X = Math.Clamp(savedBounds.X, virtualArea.Left, virtualArea.Right - Math.Min(120, width));
                    savedBounds.Y = Math.Clamp(savedBounds.Y, virtualArea.Top, virtualArea.Bottom - Math.Min(40, height));
                }

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = savedBounds.Left;
                Top = savedBounds.Top;
                Width = savedBounds.Width;
                Height = savedBounds.Height;
                _hasSavedGeometry = true;
                _restoreMaximized = settings.IsMaximized;
            }
        }
        catch { }
    }

    private void SizeWindowForPage()
    {
        if (WindowState != WindowState.Normal || _hasSavedGeometry) return;

        const double chromeHeight = 68;
        Width = 480;
        var maximumHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * 0.94);
        var targetHeight = Width / _pageAspectRatio + chromeHeight;
        if (targetHeight > maximumHeight)
        {
            targetHeight = maximumHeight;
            Width = Math.Max(MinWidth, (targetHeight - chromeHeight) * _pageAspectRatio);
        }
        Height = Math.Max(MinHeight, targetHeight);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_currentPath is not null && _totalPages > 0)
                _currentPage = Math.Clamp(GetVisiblePage(), 1, _totalPages);
            RememberCurrentPage();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var bounds = _isFullscreen
                ? _fullscreenRestoreBounds
                : WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
            var wasMaximized = _isFullscreen
                ? _fullscreenRestoreState == WindowState.Maximized
                : WindowState == WindowState.Maximized;
            var settings = new WindowSettings(Math.Max(MinWidth, bounds.Width), _showVerticalScrollBar,
                Math.Max(MinHeight, bounds.Height), bounds.Left, bounds.Top,
                wasMaximized, _recentFiles, _pageKeysNavigatePdfs,
                _lastPages.TakeLast(100).ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.OrdinalIgnoreCase), _rememberLastPage);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }

    private void NewWindowButton_Click(object sender, RoutedEventArgs e) => LaunchNewInstance();
    private void NewWindowMenuItem_Click(object sender, RoutedEventArgs e) => LaunchNewInstance();

    private static void LaunchNewInstance(string? pdfPath = null)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(pdfPath)) startInfo.ArgumentList.Add(Path.GetFullPath(pdfPath));
        Process.Start(startInfo);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPdfs(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPdfs(e.Data);
        if (paths.Count == 0) return;

        var firstExternalIndex = 0;
        if (_currentPath is null)
        {
            OpenPdf(paths[0]);
            firstExternalIndex = 1;
        }

        for (var index = firstExternalIndex; index < paths.Count; index++)
            LaunchNewInstance(paths[index]);
        e.Handled = true;
    }

    private static IReadOnlyList<string> GetDroppedPdfs(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return [];
        return files.Where(IsPdf).ToArray();
    }

    private static bool IsPdf(string path) => File.Exists(path)
        && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    private void RefreshFolderPdfs(string currentPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(currentPath);
            _folderPdfs = string.IsNullOrWhiteSpace(directory)
                ? []
                : Directory.EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            _folderPdfIndex = _folderPdfs.ToList().FindIndex(path =>
                string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            _folderPdfs = [currentPath];
            _folderPdfIndex = 0;
        }
    }

    private void NavigatePdf(int delta)
    {
        var target = _folderPdfIndex + delta;
        if (target < 0 || target >= _folderPdfs.Count) return;
        OpenPdf(_folderPdfs[target], preserveView: true);
    }

    private void PreviousPdfButton_Click(object sender, RoutedEventArgs e) => NavigatePdf(-1);
    private void NextPdfButton_Click(object sender, RoutedEventArgs e) => NavigatePdf(1);

    private void DeletePdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || !File.Exists(_currentPath)) return;
        var path = _currentPath;
        var fileName = Path.GetFileName(path);
        if (!ReaderDialog.Confirm(this,
                $"¿Quieres enviar “{fileName}” a la Papelera de reciclaje?",
                "Eliminar PDF")) return;

        var nextPath = _folderPdfIndex >= 0 && _folderPdfIndex < _folderPdfs.Count - 1
            ? _folderPdfs[_folderPdfIndex + 1]
            : _folderPdfIndex > 0 ? _folderPdfs[_folderPdfIndex - 1] : null;

        try
        {
            ++_openRequestVersion;
            ++_renderRequestVersion;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            _recentFiles.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            _lastPages.Remove(path);
            RebuildRecentFilesMenu();

            if (nextPath is not null && File.Exists(nextPath))
            {
                OpenPdf(nextPath, preserveView: true);
                StatusText.Text = $"{fileName} se envió a la Papelera";
                return;
            }

            ShowEmptyReader();
            StatusText.Text = $"{fileName} se envió a la Papelera";
        }
        catch (Exception exception)
        {
            ReaderDialog.Show(this, exception.Message, "No se pudo eliminar el PDF");
        }
    }

    private void ShowEmptyReader()
    {
        _currentPath = null;
        _currentPage = 0;
        _totalPages = 0;
        _folderPdfs = [];
        _folderPdfIndex = -1;
        _displayedPage = null;
        _isInternetDocument = false;
        PagesPanel.Children.Clear();
        _displayedPages.Clear();
        DocumentTitle.Text = "Lector PDF";
        Title = "Lector PDF";
        EmptyStateTitle.Text = "Abre un documento PDF";
        EmptyStateDescription.Text = "Selecciona un archivo o arrastra un PDF para abrirlo aquí.";
        EmptyStateAction.Content = "Seleccionar un PDF";
        EmptyState.Visibility = Visibility.Visible;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        UpdatePageControls();
    }

    private void UpdateReadingStatus()
    {
        StatusText.Text = $"Página {_currentPage} de {_totalPages}";
    }

    private void RememberCurrentPage()
    {
        if (_rememberLastPage && _currentPath is not null && _currentPage > 0)
        {
            // Reinsert so dictionary order also represents recency for bounded persistence.
            _lastPages.Remove(_currentPath);
            _lastPages[_currentPath] = _currentPage;
        }
    }

    private void AddRecentFile(string path)
    {
        _recentFiles.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > 10) _recentFiles.RemoveRange(10, _recentFiles.Count - 10);
        RebuildRecentFilesMenu();
    }

    private void RebuildRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        foreach (var path in _recentFiles.Where(File.Exists))
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, Tag = path };
            item.Click += (_, _) => OpenPdf((string)item.Tag);
            RecentFilesMenu.Items.Add(item);
        }
        if (RecentFilesMenu.Items.Count == 0)
            RecentFilesMenu.Items.Add(new MenuItem { Header = "No hay archivos recientes", IsEnabled = false });
        else
        {
            RecentFilesMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Limpiar lista" };
            clear.Click += (_, _) => { _recentFiles.Clear(); RebuildRecentFilesMenu(); };
            RecentFilesMenu.Items.Add(clear);
        }
    }

    private string? PromptForPassword(string fileName)
    {
        var dialog = new PasswordDialog(fileName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = true });
    }

    private void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || !File.Exists(_currentPath)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Guardar una copia del PDF",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_currentPath),
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var destination = Path.GetFullPath(dialog.FileName);
            if (string.Equals(destination, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "El documento ya está guardado en esa ubicación";
                return;
            }
            File.Copy(_currentPath, destination, overwrite: true);
            StatusText.Text = $"Copia guardada: {Path.GetFileName(destination)}";
            AddRecentFile(destination);
        }
        catch (Exception exception)
        {
            ReaderDialog.Show(this, exception.Message, "No se pudo guardar la copia");
        }
    }

    private void SaveInternetButton_Click(object sender, RoutedEventArgs e) => SaveInternetDocumentToDocuments();

    private void SaveInternetDocumentToDocuments()
    {
        if (!_isInternetDocument || _currentPath is null || !File.Exists(_currentPath)) return;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(documents);
            if (string.Equals(Path.GetDirectoryName(_currentPath), documents, StringComparison.OrdinalIgnoreCase))
            {
                _isInternetDocument = false;
                UpdatePageControls();
                StatusText.Text = "El PDF ya está guardado en Documentos";
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(_currentPath);
            var extension = Path.GetExtension(_currentPath);
            var destination = Path.Combine(documents, stem + extension);
            for (var suffix = 1; File.Exists(destination); suffix++)
                destination = Path.Combine(documents, $"{stem} ({suffix}){extension}");

            File.Copy(_currentPath, destination);
            _isInternetDocument = false;
            UpdatePageControls();
            AddRecentFile(destination);
            StatusText.Text = $"Guardado en Documentos: {Path.GetFileName(destination)}";
        }
        catch (Exception exception)
        {
            ReaderDialog.Show(this, exception.Message, "No se pudo guardar en Documentos");
        }
    }

    internal static bool IsInternetOrigin(string path)
    {
        try
        {
            var zonePath = path + ":Zone.Identifier";
            if (File.Exists(zonePath))
            {
                var zone = File.ReadAllText(zonePath);
                if (zone.Contains("ZoneId=3", StringComparison.OrdinalIgnoreCase) ||
                    zone.Contains("ZoneId=4", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }

        var fullPath = Path.GetFullPath(path);
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var internetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "INetCache") + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(internetCache, StringComparison.OrdinalIgnoreCase);
    }

    private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null) return;
        try
        {
            using var document = OpenPdfPig(_currentPath, _documentPassword);
            var info = document.Information;
            var file = new FileInfo(_currentPath);
            ReaderDialog.Show(this,
                $"Nombre: {file.Name}\nRuta: {file.DirectoryName}\nTamaño: {file.Length / 1024d:N0} KB\nPáginas: {document.NumberOfPages}\n" +
                $"Título: {info.Title ?? "—"}\nAutor: {info.Author ?? "—"}\nCreador: {info.Creator ?? "—"}\nProductor: {info.Producer ?? "—"}",
                "Propiedades del documento");
        }
        catch (Exception exception)
        {
            ReaderDialog.Show(this, exception.Message, "No se pudieron leer las propiedades");
        }
    }

    private void PrintMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var pages = _displayedPages.Where(item => item is not null).Cast<BitmapSource>().ToList();
        if (pages.Count == 0) return;
        if (pages.Count != _totalPages)
        {
            ReaderDialog.Show(this, "Espera a que terminen de cargarse todas las páginas antes de imprimir.",
                "Documento cargando");
            return;
        }
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        foreach (var bitmap in pages)
        {
            var fixedPage = new FixedPage { Width = dialog.PrintableAreaWidth, Height = dialog.PrintableAreaHeight };
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform,
                Width = fixedPage.Width, Height = fixedPage.Height };
            fixedPage.Children.Add(image);
            var content = new PageContent();
            ((IAddChild)content).AddChild(fixedPage);
            document.Pages.Add(content);
        }
        dialog.PrintDocument(document.DocumentPaginator, Path.GetFileName(_currentPath));
    }

    private void FindMenuItem_Click(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
        RefreshFindResults();
    }

    private void CloseFindButton_Click(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _findResults.Clear();
        _findResultIndex = -1;
        ClearTextSelection();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFindResults();
    private void FindNextButton_Click(object sender, RoutedEventArgs e) => MoveFindResult(1);
    private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => MoveFindResult(-1);
    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { MoveFindResult(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseFindButton_Click(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void RefreshFindResults()
    {
        _findResults.Clear();
        var query = FindTextBox.Text.Trim();
        if (query.Length > 0)
            for (var page = 0; page < _pageText.Count; page++)
                if (_pageText[page] is PageTextData data)
                    for (var word = 0; word < data.Words.Count; word++)
                        if (data.Words[word].Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                            _findResults.Add((page, word));
        _findResultIndex = _findResults.Count > 0 ? 0 : -1;
        ShowCurrentFindResult();
    }

    private void MoveFindResult(int delta)
    {
        if (_findResults.Count == 0) return;
        _findResultIndex = (_findResultIndex + delta + _findResults.Count) % _findResults.Count;
        ShowCurrentFindResult();
    }

    private void ShowCurrentFindResult()
    {
        FindResultText.Text = _findResults.Count == 0 ? "0 de 0" : $"{_findResultIndex + 1} de {_findResults.Count}";
        if (_findResultIndex < 0) return;
        var result = _findResults[_findResultIndex];
        _selectionPage = result.Page;
        _selectionAnchor = _selectionEnd = result.Word;
        ScrollToPage(result.Page + 1);
        DrawTextSelection();
    }

    private void ZoomInMenuItem_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.1);
    private void ZoomOutMenuItem_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.1);
    private void FitWidthMenuItem_Click(object sender, RoutedEventArgs e) => SetZoom(1);
    private void DocumentViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    private void ChangeZoom(double delta) => SetZoom(Math.Round((_zoomFactor + delta) * 10) / 10);
    private void SetZoom(double zoom)
    {
        _zoomFactor = Math.Clamp(zoom, 0.5, 3);
        ZoomText.Text = $"{_zoomFactor:P0}";
        UpdateDisplayedPageWidths(Math.Max(1, DocumentViewport.ViewportWidth) * _zoomFactor);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async void RotateLeftMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 3) % 4;
        ClearTextSelection();
        await RenderDocumentAsync(showLoading: false);
    }

    private async void RotateRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 1) % 4;
        ClearTextSelection();
        await RenderDocumentAsync(showLoading: false);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isFullscreen && e.Key == Key.Escape) { ExitFullscreen(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && _selectionPage >= 0) { CopySelectedText(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { LaunchNewInstance(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S && _isInternetDocument) { SaveInternetDocumentToDocuments(); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S) { SaveAsMenuItem_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { FindMenuItem_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P) { PrintMenuItem_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Add || e.Key == Key.OemPlus)) { ChangeZoom(0.1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Subtract || e.Key == Key.OemMinus)) { ChangeZoom(-0.1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { SetZoom(1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.PageUp && PreviousPdfButton.IsEnabled) { NavigatePdf(-1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.PageDown && NextPdfButton.IsEnabled) { NavigatePdf(1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.PageUp)
        {
            if (_pageKeysNavigatePdfs && PreviousPdfButton.IsEnabled) NavigatePdf(-1);
            else if (!_pageKeysNavigatePdfs && PreviousPageButton.IsEnabled) NavigateToPage(_currentPage - 1);
            else return;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.PageDown)
        {
            if (_pageKeysNavigatePdfs && NextPdfButton.IsEnabled) NavigatePdf(1);
            else if (!_pageKeysNavigatePdfs && NextPageButton.IsEnabled) NavigateToPage(_currentPage + 1);
            else return;
            e.Handled = true;
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_pageKeysNavigatePdfs, _rememberLastPage) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _pageKeysNavigatePdfs = dialog.PageKeysNavigatePdfs;
        _rememberLastPage = dialog.RememberLastPage;
        if (_rememberLastPage) RememberCurrentPage();
        StatusText.Text = _rememberLastPage
            ? "Se recordará la última página de cada PDF"
            : "Los PDF se abrirán desde la primera página";
    }

    private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e) => ReaderDialog.Show(this,
        "Ctrl+N       Nueva ventana\nCtrl+O       Abrir un PDF\nCtrl+S       Guardar PDF de Internet en Documentos\nCtrl+Shift+S Guardar una copia\nCtrl+F       Buscar texto\nCtrl+P       Imprimir\nCtrl++/-     Zoom\nCtrl+0       Ajustar al ancho\nCtrl+RePág   PDF anterior\nCtrl+AvPág   PDF siguiente\nRePág/AvPág  Página anterior/siguiente (configurable)\nCtrl+rueda   Zoom",
        "Atajos de teclado");

    private void TitleRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximizeRestore();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void TitleRegion_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        SystemCommands.ShowSystemMenu(this, point);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximizeRestore() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return;

        _fullscreenRestoreState = WindowState;
        _fullscreenRestoreBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _isFullscreen = true;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        TitleBarRow.Height = new GridLength(0);
        FooterRow.Height = new GridLength(0);
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
        }
        ApplyRoundedCorners();
        _ = SetWindowPos(handle, IntPtr.Zero, monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top, ShowWindow);
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;
        ResizeMode = ResizeMode.CanResize;
        TitleBarRow.Height = new GridLength(40);
        FooterRow.Height = new GridLength(28);
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = 40;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CornerRadius = new CornerRadius(8);
        }
        Left = _fullscreenRestoreBounds.Left;
        Top = _fullscreenRestoreBounds.Top;
        Width = _fullscreenRestoreBounds.Width;
        Height = _fullscreenRestoreBounds.Height;
        WindowState = _fullscreenRestoreState;
        ApplyRoundedCorners();
    }
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyRoundedCorners();
        if (_restoreMaximized)
            Dispatcher.BeginInvoke(() => WindowState = WindowState.Maximized, DispatcherPriority.Loaded);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        ApplyRoundedCorners();
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var preference = _isFullscreen ? DwmWindowCornerDoNotRound : DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // WindowChrome.CornerRadius remains the fallback on older Windows versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}

public sealed record WindowSettings(double Width, bool? ShowVerticalScrollBar = null,
    double? Height = null, double? Left = null, double? Top = null, bool IsMaximized = false,
    IReadOnlyList<string>? RecentFiles = null, bool PageKeysNavigatePdfs = false,
    IReadOnlyDictionary<string, int>? LastPages = null, bool RememberLastPage = false);
public sealed record TextWord(string Text, double Left, double Bottom, double Width, double Height);
public sealed record PageTextData(double Width, double Height, IReadOnlyList<TextWord> Words);
