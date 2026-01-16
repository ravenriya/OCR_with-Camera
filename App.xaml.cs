using PixtechApplication;
using System.Windows;

namespace PixtechApplication
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var loginWindow = new LoginWindow();
            if (loginWindow.ShowDialog() == true && loginWindow.IsAuthenticated)
            {
                var mainWindow = new MainWindow(loginWindow.AuthenticatedUser);
                mainWindow.Show();
            }
            else
            {
                Shutdown();
            }
        }
    }
}