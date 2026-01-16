using PixtechApplication;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PixtechApplication
{
    public partial class LoginWindow : Window
    {
        public bool IsAuthenticated { get; private set; }
        public string AuthenticatedUser { get; private set; }

        private const int LOCKOUT_MINUTES = 5;

        public LoginWindow()
        {
            InitializeComponent();
            txtSignupPassword.PasswordChanged += (s, e) => UpdatePasswordStrength(txtSignupPassword.Password);
        }

        private void UpdatePasswordStrength(string password)
        {
            strength1.Background = new SolidColorBrush(Color.FromRgb(224, 224, 224));
            strength2.Background = new SolidColorBrush(Color.FromRgb(224, 224, 224));
            strength3.Background = new SolidColorBrush(Color.FromRgb(224, 224, 224));
            strength4.Background = new SolidColorBrush(Color.FromRgb(224, 224, 224));

            bool hasLength = password.Length >= 8;
            bool hasUpper = Regex.IsMatch(password, @"[A-Z]");
            bool hasLower = Regex.IsMatch(password, @"[a-z]");
            bool hasNumber = Regex.IsMatch(password, @"[0-9]");
            bool hasSpecial = Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]");

            req1.Text = hasLength ? "✅ At least 8 characters" : "❌ At least 8 characters";
            req1.Foreground = hasLength ? Brushes.Green : new SolidColorBrush(Color.FromRgb(153, 153, 153));
            req2.Text = hasUpper ? "✅ Uppercase letter (A-Z)" : "❌ Uppercase letter (A-Z)";
            req2.Foreground = hasUpper ? Brushes.Green : new SolidColorBrush(Color.FromRgb(153, 153, 153));
            req3.Text = hasLower ? "✅ Lowercase letter (a-z)" : "❌ Lowercase letter (a-z)";
            req3.Foreground = hasLower ? Brushes.Green : new SolidColorBrush(Color.FromRgb(153, 153, 153));
            req4.Text = hasNumber ? "✅ Number (0-9)" : "❌ Number (0-9)";
            req4.Foreground = hasNumber ? Brushes.Green : new SolidColorBrush(Color.FromRgb(153, 153, 153));
            req5.Text = hasSpecial ? "✅ Special character" : "❌ Special character";
            req5.Foreground = hasSpecial ? Brushes.Green : new SolidColorBrush(Color.FromRgb(153, 153, 153));

            int strength = 0;
            if (hasLength) strength++;
            if (hasUpper) strength++;
            if (hasLower) strength++;
            if (hasNumber) strength++;
            if (hasSpecial) strength++;

            if (strength >= 1) strength1.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69));
            if (strength >= 2) strength2.Background = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            if (strength >= 3) strength3.Background = new SolidColorBrush(Color.FromRgb(0, 123, 255));
            if (strength >= 4)
            {
                strength3.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                strength4.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            if (strength == 5) strength4.Background = new SolidColorBrush(Color.FromRgb(0, 200, 83));

            if (string.IsNullOrEmpty(password)) txtPasswordStrength.Text = "";
            else if (strength < 3) txtPasswordStrength.Text = "Weak password";
            else if (strength < 4) txtPasswordStrength.Text = "Medium password";
            else if (strength < 5) txtPasswordStrength.Text = "Strong password";
            else txtPasswordStrength.Text = "Very strong password!";
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e) { Login(); }
        private void TxtLoginPassword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Login(); }

        private void Login()
        {
            string username = txtLoginUsername.Text.Trim();
            string password = txtLoginPassword.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                txtLoginError.Text = "❌ Please enter username and password";
                return;
            }

            if (AuthenticationService.IsAccountLocked(username))
            {
                TimeSpan remaining = AuthenticationService.GetLockoutTimeRemaining(username);
                txtLoginError.Text = $"🔒 Account locked!\n\nToo many failed attempts.\nTry again in {remaining.Minutes}m {remaining.Seconds}s";
                return;
            }

            if (AuthenticationService.ValidateUser(username, password))
            {
                IsAuthenticated = true;
                AuthenticatedUser = username;
                DialogResult = true;
                Close();
            }
            else
            {
                int remaining = AuthenticationService.GetRemainingAttempts(username);

                if (remaining > 0)
                {
                    txtLoginError.Text = $"❌ Invalid password!\n\n⚠️ {remaining} attempt(s) remaining before lockout";
                }
                else
                {
                    TimeSpan lockoutTime = AuthenticationService.GetLockoutTimeRemaining(username);
                    txtLoginError.Text = $"🔒 Account locked for {LOCKOUT_MINUTES} minutes!\n\nToo many failed attempts.\nTry again at {DateTime.Now.AddMinutes(LOCKOUT_MINUTES):HH:mm}";
                }

                txtLoginPassword.Clear();
                txtLoginPassword.Focus();
            }
        }

        private void BtnSignup_Click(object sender, RoutedEventArgs e)
        {
            string username = txtSignupUsername.Text.Trim();
            string password = txtSignupPassword.Password;
            string confirm = txtSignupConfirm.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                txtSignupError.Text = "❌ Please fill all fields";
                return;
            }
            if (username.Length < 3)
            {
                txtSignupError.Text = "❌ Username must be at least 3 characters";
                return;
            }
            if (password.Length < 8)
            {
                txtSignupError.Text = "❌ Password must be at least 8 characters";
                return;
            }
            if (!Regex.IsMatch(password, @"[A-Z]"))
            {
                txtSignupError.Text = "❌ Password must contain uppercase letter";
                return;
            }
            if (!Regex.IsMatch(password, @"[a-z]"))
            {
                txtSignupError.Text = "❌ Password must contain lowercase letter";
                return;
            }
            if (!Regex.IsMatch(password, @"[0-9]"))
            {
                txtSignupError.Text = "❌ Password must contain a number";
                return;
            }
            if (!Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]"))
            {
                txtSignupError.Text = "❌ Password must contain special character";
                return;
            }
            if (password != confirm)
            {
                txtSignupError.Text = "❌ Passwords don't match!";
                return;
            }

            if (AuthenticationService.AddUser(username, password))
            {
                MessageBox.Show($"✅ Account created!\n\nUsername: {username}\n\nLogging you in...", "Success");
                IsAuthenticated = true;
                AuthenticatedUser = username;
                DialogResult = true;
                Close();
            }
            else
            {
                txtSignupError.Text = $"❌ Username '{username}' already exists!";
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            IsAuthenticated = false;
            DialogResult = false;
            Close();
        }
    }
}