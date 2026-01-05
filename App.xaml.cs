using System;
using System.Windows;

namespace CognexStyleApp
{
    public partial class App : Application
    {
        private MainWindow mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Show login window
            LoginWindow loginWindow = new LoginWindow();
            loginWindow.ShowDialog();

            // Check authentication
            if (loginWindow.IsAuthenticated)
            {
                // Create main window INSIDE Application.Run context
                mainWindow = new MainWindow(loginWindow.AuthenticatedUser);

                // Set as main window
                this.MainWindow = mainWindow;

                // Show it
                mainWindow.Show();
            }
            else
            {
                // Exit
                this.Shutdown();
            }
        }
    }
}