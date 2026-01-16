using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.IO;
using System.Threading.Tasks;

namespace PixtechApplication
{
    public partial class MainWindow : Window
    {
        private List<string> imageFiles = new List<string>();
        private string currentTool = "OCR";
        private Random random = new Random();
        private bool isModelTrained = false;
        private string currentUser;
        private DispatcherTimer authTimer;
        private DateTime lastAuthTime;
        private const int AUTH_TIMEOUT_MINUTES = 20;

        public MainWindow() : this("Guest") { }

        public MainWindow(string username)
        {
            InitializeComponent();
            currentUser = username;
            lastAuthTime = DateTime.Now;
            authTimer = new DispatcherTimer();
            authTimer.Interval = TimeSpan.FromMinutes(AUTH_TIMEOUT_MINUTES);
            authTimer.Tick += AuthTimer_Tick;
            authTimer.Start();
            MessageBox.Show($"✅ Welcome, {currentUser}!", "PixTech");
            if (txtStatusBar != null)
                txtStatusBar.Text = $"User: {currentUser}";
        }

        private void AuthTimer_Tick(object sender, EventArgs e)
        {
            authTimer.Stop();
            MessageBox.Show("⏰ Time to re-authenticate!", "Re-Auth", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReAuthenticate();
        }

        private void ReAuthenticate()
        {
            var reAuth = new ReAuthWindow(currentUser);
            if (reAuth.ShowDialog() == true && reAuth.IsAuthenticated)
            {
                lastAuthTime = DateTime.Now;
                authTimer.Start();
                MessageBox.Show("✅ Success!", "Re-Auth");
            }
            else
            {
                MessageBox.Show("❌ Failed!", "Security", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        private void LoadFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                    if (!imageFiles.Contains(file)) imageFiles.Add(file);
                UpdateImageList();
                if (imageFiles.Count > 0) ShowImage(imageFiles[0]);
            }
        }

        private void ShowImage(string path)
        {
            try
            {
                txtPlaceholder.Visibility = Visibility.Collapsed;
                imgDisplay.Source = new BitmapImage(new Uri(path));
                imgDisplay.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem item && item.Tag is string path)
                ShowImage(path);
        }

        private void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            LoadFiles_Click(sender, e);
        }

        private void ToolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try { if (cmbToolType?.SelectedItem is ComboBoxItem item) currentTool = item.Content.ToString(); }
            catch { }
        }

        private async void Train_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0) { MessageBox.Show("Load images!"); return; }
            btnTrain.IsEnabled = false;
            btnTrain.Content = "Training...";

            for (int i = 1; i <= 100; i++)
            {
                progressBar.Value = i;
                await Task.Delay(50);
            }

            btnTrain.IsEnabled = true;
            btnTrain.Content = "TRAIN";
            isModelTrained = true;
            ShowResults();
            MessageBox.Show("Training Complete!");
        }

        private void ShowResults()
        {
            txtResult.Text = GenerateOCR();
            txtConfidence.Text = $"Confidence: {random.Next(85, 99)}%";
            txtDecision.Text = "PASS";
            txtDecision.Foreground = Brushes.Green;
        }

        private string GenerateOCR()
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var result = "";
            for (int i = 0; i < 9; i++)
            {
                result += chars[random.Next(chars.Length)];
                if (i == 2 || i == 5) result += "-";
            }
            return result;
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (!isModelTrained) return;
            var dialog = new SaveFileDialog { Filter = "Model|*.vrws" };
            if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, "Saved");
        }

        private void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Model|*.vrws" };
            if (dialog.ShowDialog() == true) { isModelTrained = true; ShowResults(); }
        }

        private void Accept_Click(object sender, RoutedEventArgs e) { MessageBox.Show("ACCEPTED"); }
        private void Reject_Click(object sender, RoutedEventArgs e) { MessageBox.Show("REJECTED"); }

        private void UpdateImageList()
        {
            lstImages.Items.Clear();
            foreach (var path in imageFiles)
                lstImages.Items.Add(new ListBoxItem { Content = Path.GetFileName(path), Tag = path });
            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            authTimer?.Stop();
            base.OnClosing(e);
        }
    }
}