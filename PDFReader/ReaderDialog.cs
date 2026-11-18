using System.Windows;
using System.Windows.Controls;

namespace PDFReader;

internal static class ReaderDialog
{
    internal static void Show(Window? owner, string text, string title)
    {
        _ = ShowCore(owner, text, title, confirm: false);
    }

    internal static bool Confirm(Window? owner, string text, string title) =>
        ShowCore(owner, text, title, confirm: true);

    private static bool ShowCore(Window? owner, string text, string title, bool confirm)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource("DialogWindow")
        };
        if (owner is { IsVisible: true }) window.Owner = owner;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 22, 24, 24)
        });
        panel.Children.Add(new Separator());
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 12, 16, 12)
        };
        var accepted = false;
        if (confirm)
        {
            var cancel = new Button { Content = "Cancelar", MinWidth = 78, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => window.Close();
            actions.Children.Add(cancel);
        }
        var accept = new Button { Content = confirm ? "Eliminar" : "Aceptar", MinWidth = 78, IsDefault = true };
        accept.Click += (_, _) => { accepted = true; window.Close(); };
        actions.Children.Add(accept);
        panel.Children.Add(actions);
        window.Content = panel;
        window.ShowDialog();
        return accepted;
    }
}
