using System.Windows;

namespace PDFReader;

public partial class PasswordDialog : Window
{
    public string Password => PasswordBox.Password;

    public PasswordDialog(string fileName)
    {
        InitializeComponent();
        PromptText.Text = $"Escribe la contraseña para abrir {fileName}.";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
