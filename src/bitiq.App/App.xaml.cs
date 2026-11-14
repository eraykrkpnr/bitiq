using System.Windows;

namespace Bitiq.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Beklenmeyen bir arayüz hatası oluştu:\n{args.Exception.Message}",
                "bitiq",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Uygulama başlatılamadı:\n{ex.Message}",
                "bitiq",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
