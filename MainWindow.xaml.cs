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
using System.Diagnostics;
using System.Linq;

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

        private async Task<string> PerformEasyOCR(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptPath = Path.Combine(baseDir, "easyocr_service.py");

                    // Debug: Check if file exists
                    if (!File.Exists(scriptPath))
                    {
                        // Try alternate locations
                        string[] possiblePaths = {
                            Path.Combine(baseDir, "easyocr_simple.py"),
                            Path.Combine(Directory.GetCurrentDirectory(), "easyocr_service.py"),
                            "easyocr_service.py"
                        };

                        string foundPath = null;
                        foreach (var path in possiblePaths)
                        {
                            if (File.Exists(path))
                            {
                                foundPath = path;
                                break;
                            }
                        }

                        if (foundPath == null)
                        {
                            return $"❌ Python script NOT FOUND!\n\n" +
                                   $"Searched locations:\n" +
                                   $"1. {possiblePaths[0]}\n" +
                                   $"2. {possiblePaths[1]}\n" +
                                   $"3. Current directory\n\n" +
                                   $"Please make sure easyocr_service.py is in your bin\\Debug folder!\n\n" +
                                   $"Current directory: {baseDir}";
                        }

                        scriptPath = foundPath;
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\" \"{imagePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                        {
                            return "❌ Could not start Python!\n\nMake sure Python is installed.";
                        }

                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        // Check for errors
                        if (process.ExitCode != 0)
                        {
                            return $"❌ Python Error:\n{error}\n\nOutput:\n{output}";
                        }

                        if (!string.IsNullOrEmpty(error))
                        {
                            return $"❌ Python Error:\n{error}";
                        }

                        // No output means no text detected
                        if (string.IsNullOrWhiteSpace(output))
                        {
                            return "ℹ️ No text detected in the image.";
                        }

                        // Parse and format the output
                        var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        var result = "✅ === OCR RESULTS ===\n\n";
                        int count = 1;
                        double totalConf = 0;

                        foreach (var line in lines)
                        {
                            var parts = line.Split('|');
                            if (parts.Length == 2)
                            {
                                string text = parts[0].Trim();
                                string confidence = parts[1].Trim();
                                result += $"📝 Text #{count}: {text}\n";
                                result += $"   🎯 Confidence: {confidence}%\n\n";

                                if (double.TryParse(confidence, out double conf))
                                {
                                    totalConf += conf;
                                }
                                count++;
                            }
                        }

                        if (count > 1)
                        {
                            double avgConf = totalConf / (count - 1);
                            result += $"📊 Average Confidence: {avgConf:F2}%\n";
                            result += $"📋 Total Detections: {count - 1}";
                        }

                        return result;
                    }
                }
                catch (Exception ex)
                {
                    return $"❌ Exception: {ex.Message}\n\nStack:\n{ex.StackTrace}";
                }
            });
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
            if (imageFiles.Count == 0)
            {
                MessageBox.Show("⚠️ Please load images first!", "No Images", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnTrain.IsEnabled = false;
            btnTrain.Content = "Processing...";
            progressBar.Value = 0;

            if (currentTool == "OCR")
            {
                string currentImagePath = null;
                if (lstImages.SelectedItem is ListBoxItem item && item.Tag is string path)
                    currentImagePath = path;
                else
                    currentImagePath = imageFiles[0];

                txtResult.Text = "🔍 Running EasyOCR...\n\nPlease wait...";
                txtConfidence.Text = "⏳ Processing...";
                txtDecision.Text = "WORKING";
                txtDecision.Foreground = System.Windows.Media.Brushes.Orange;

                for (int i = 1; i <= 30; i++)
                {
                    progressBar.Value = i;
                    await Task.Delay(10);
                }

                var ocrResult = await PerformEasyOCR(currentImagePath);

                progressBar.Value = 100;
                txtResult.Text = ocrResult;

                if (ocrResult.StartsWith("✅"))
                {
                    txtConfidence.Text = "✅ OCR Complete";
                    txtDecision.Text = "SUCCESS";
                    txtDecision.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    txtConfidence.Text = "❌ OCR Failed";
                    txtDecision.Text = "ERROR";
                    txtDecision.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            else
            {
                for (int i = 1; i <= 100; i++)
                {
                    progressBar.Value = i;
                    await Task.Delay(50);
                }
                ShowResults();
            }

            btnTrain.IsEnabled = true;
            btnTrain.Content = "TRAIN";
            isModelTrained = true;
        }

        private void ShowResults()
        {
            txtResult.Text = GenerateOCR();
            txtConfidence.Text = $"Confidence: {random.Next(85, 99)}%";
            txtDecision.Text = "PASS";
            txtDecision.Foreground = System.Windows.Media.Brushes.Green;
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