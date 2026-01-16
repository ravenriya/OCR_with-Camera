using PixtechApplication;
using System;
using System.Windows;
using System.Windows.Input;

namespace PixtechApplication
{
    public partial class ReAuthWindow : Window
    {
        public bool IsAuthenticated { get; private set; }
        private string username;

        public ReAuthWindow(string user)
        {
            InitializeComponent();
            username = user;
            txtUsername.Text = $"Username: {username}";
        }

        private void BtnAuthenticate_Click(object sender, RoutedEventArgs e)
        {
            Authenticate();
        }

        private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Authenticate();
        }

        private void Authenticate()
        {
            string password = txtPassword.Visibility == Visibility.Visible
                ? txtPassword.Password
                : txtPasswordVisible.Text;

            if (string.IsNullOrEmpty(password))
            {
                txtError.Text = "❌ Please enter password";
                return;
            }

            if (AuthenticationService.ValidateUser(username, password))
            {
                IsAuthenticated = true;
                DialogResult = true;
                Close();
            }
            else
            {
                txtError.Text = "❌ Invalid password!";
                txtPassword.Clear();
                txtPasswordVisible.Clear();
            }
        }

        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            if (txtPassword.Visibility == Visibility.Visible)
            {
                txtPasswordVisible.Text = txtPassword.Password;
                txtPassword.Visibility = Visibility.Collapsed;
                txtPasswordVisible.Visibility = Visibility.Visible;
                txtPasswordVisible.Focus();
            }
            else
            {
                txtPassword.Password = txtPasswordVisible.Text;
                txtPasswordVisible.Visibility = Visibility.Collapsed;
                txtPassword.Visibility = Visibility.Visible;
                txtPassword.Focus();
            }
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            IsAuthenticated = false;
            DialogResult = false;
            Close();
        }
    }
}