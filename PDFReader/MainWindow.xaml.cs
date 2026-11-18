using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PDFReader;

public partial class MainWindow : Window
{
    private const int MaxRecentFiles = 10;
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFReader", "recent-files.json");
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private bool _isSelectingRecent;

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadRecentFiles();
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

        if (dialog.ShowDialog(this) == true)
        {
            OpenPdf(dialog.FileName);
        }
    }

    private void OpenPdf(string path, bool addToHistory = true)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Selecciona un archivo PDF válido.", "No se pudo abrir",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (addToHistory)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            if (_history.Count == 0 || !string.Equals(_history[^1], fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _history.Add(fullPath);
            }
            _historyIndex = _history.Count - 1;
        }

        DocumentTitle.Text = Path.GetFileName(fullPath);
        DocumentPath.Text = fullPath;
        StatusText.Text = "Cargando…";
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        PdfView.Source = new Uri(fullPath);
        AddRecentFile(fullPath);
        UpdateNavigationButtons();
    }

    private void AddRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentFiles.Remove(existing);
        }

        RecentFiles.Insert(0, new RecentFile(path));
        while (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
        SaveRecentFiles();
    }

    private void RecentFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingRecent || RecentFilesList.SelectedItem is not RecentFile recent)
        {
            return;
        }

        _isSelectingRecent = true;
        RecentFilesList.SelectedItem = null;
        _isSelectingRecent = false;

        if (File.Exists(recent.Path))
        {
            OpenPdf(recent.Path);
        }
        else
        {
            RecentFiles.Remove(recent);
            SaveRecentFiles();
            MessageBox.Show(this, "El archivo ya no está disponible en esa ubicación.",
                "Archivo no encontrado", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateHistory(-1);
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => NavigateHistory(1);

    private void NavigateHistory(int delta)
    {
        var targetIndex = _historyIndex + delta;
        if (targetIndex < 0 || targetIndex >= _history.Count) return;

        _historyIndex = targetIndex;
        OpenPdf(_history[_historyIndex], addToHistory: false);
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    }

    private void PdfView_NavigationCompleted(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = e.IsSuccess ? "Documento listo" : "No se pudo mostrar el documento";
        if (!e.IsSuccess) EmptyState.Visibility = Visibility.Visible;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPdf(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedPdf(e.Data, out var path)) OpenPdf(path!);
    }

    private static bool TryGetDroppedPdf(IDataObject data, out string? path)
    {
        path = null;
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length != 1)
        {
            return false;
        }

        path = files[0];
        return string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Left && BackButton.IsEnabled)
        {
            NavigateHistory(-1);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Right && ForwardButton.IsEnabled)
        {
            NavigateHistory(1);
            e.Handled = true;
        }
    }

    private void LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_settingsPath)) ?? [];
            foreach (var path in paths.Where(File.Exists).Take(MaxRecentFiles))
            {
                RecentFiles.Add(new RecentFile(path));
            }
        }
        catch
        {
            // Damaged preferences should never prevent the reader from opening.
        }
    }

    private void SaveRecentFiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(RecentFiles.Select(item => item.Path)));
        }
        catch
        {
            StatusText.Text = "No se pudo guardar la lista de archivos recientes";
        }
    }
}

public sealed class RecentFile(string path)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string Directory { get; } = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
}
