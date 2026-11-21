using System.Windows;

namespace PDFReader;

public partial class App : Application
{
    private ExternalOpenCoordinator? _externalOpenCoordinator;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var startupPath = e.Args.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(startupPath)
            && await ExternalOpenCoordinator.TryRedirectToEmptyInstanceAsync(startupPath))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(startupPath);
        MainWindow = window;
        _externalOpenCoordinator = new ExternalOpenCoordinator(window);
        _externalOpenCoordinator.Start();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _externalOpenCoordinator?.Dispose();
        base.OnExit(e);
    }
}
