using System.Windows;

namespace PDFReader;

public partial class SettingsDialog : Window
{
    public bool PageKeysNavigatePdfs => PageKeysCheckBox.IsChecked == true;
    public bool RememberLastPage => RememberLastPageCheckBox.IsChecked == true;

    public SettingsDialog(bool pageKeysNavigatePdfs, bool rememberLastPage)
    {
        InitializeComponent();
        PageKeysCheckBox.IsChecked = pageKeysNavigatePdfs;
        RememberLastPageCheckBox.IsChecked = rememberLastPage;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
