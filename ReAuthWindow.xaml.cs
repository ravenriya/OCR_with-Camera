using System;
using System.Windows;
using System.Windows.Input;

namespace CognexStyleApp
{
    public partial class ReAuthWindow : Window
    {
        public bool IsAuthenticated { get; private set; }
        private string username;
        private bool passwordVisible = false;

        public ReAuthWindow(string currentUser)
        {
            InitializeComponent();
            username = currentUser;
            txtUsername.Text = $"Username: {username}";
            txtPassword.Focus();
        }

        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            passwordVisible = !passwordVisible;
            if (passwordVisible)
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

        private void BtnAuthenticate_Click(object sender, RoutedEventArgs e) { Authenticate(); }
        private void TxtPassword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Authenticate(); }

        private void Authenticate()
        {
            string password = passwordVisible ? txtPasswordVisible.Text : txtPassword.Password;
            if (string.IsNullOrEmpty(password))
            {
                txtError.Text = "❌ Please enter your password";
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
                txtError.Text = "❌ Incorrect password!";
                if (passwordVisible) txtPasswordVisible.Clear();
                else txtPassword.Clear();
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