using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace PixtechApplication
{
    public partial class MainWindow : Window
    {
        private List<string> imageFiles = new List<string>();
        private int currentImageIndex = 0;
        private string currentTool = "OCR";
        private Random random = new Random();
        private bool isModelTrained = false;
        private string currentUser;
        private DispatcherTimer authTimer;
        private DateTime lastAuthTime;
        private const int AUTH_TIMEOUT_MINUTES = 20;
        private bool isTrainingMode = false;
        private string annotationsFolder;
        private string modelsFolder;

        // Annotation fields
        private bool isAnnotationMode = false;
        private Point annotationStartPoint;
        private Rectangle currentAnnotationRect;
        private List<AnnotationData> currentImageAnnotations = new List<AnnotationData>();
        private const int LABEL_OFFSET = 2;

        public MainWindow() : this("Guest") { }

        public MainWindow(string username)
        {
            InitializeComponent();
            currentUser = username;
            lastAuthTime = DateTime.Now;

            // Setup folders
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            annotationsFolder = IOPath.Combine(baseDir, "Annotations");
            modelsFolder = IOPath.Combine(baseDir, "Models");
            Directory.CreateDirectory(annotationsFolder);
            Directory.CreateDirectory(modelsFolder);

            // Setup auth timer
            authTimer = new DispatcherTimer();
            authTimer.Interval = TimeSpan.FromMinutes(AUTH_TIMEOUT_MINUTES);
            authTimer.Tick += AuthTimer_Tick;
            authTimer.Start();

            MessageBox.Show($"✅ Welcome, {currentUser}!", "PixTech");
            if (txtStatusBar != null)
                txtStatusBar.Text = $"User: {currentUser} | Mode: Inference";

            UpdateAnnotationStatus();
        }

        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (rbTrainingMode == null || rbInferenceMode == null) return;

            isTrainingMode = rbTrainingMode.IsChecked == true;

            if (isTrainingMode)
            {
                txtMode.Text = "TRAINING MODE";
                txtMode.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                pnlInference.Visibility = Visibility.Collapsed;
                pnlTraining.Visibility = Visibility.Visible;

                // FORCE SHOW BUTTONS
                btnAnnotate.Visibility = Visibility.Visible;
                btnStartTraining.Visibility = Visibility.Visible;
                btnCheckAnnotations.Visibility = Visibility.Visible;

                btnAnnotate.IsEnabled = imageFiles.Count > 0;

                // Show annotation canvas and load existing annotations
                if (imgDisplay.Source != null)
                {
                    annotationCanvas.Visibility = Visibility.Visible;
                    UpdateCanvasSize();
                    LoadAnnotationsForCurrentImage();
                }

                UpdateAnnotationStatus();

                // Force enable training button if annotations exist
                if (CheckAnnotationsExist())
                {
                    btnStartTraining.IsEnabled = true;
                }

                txtStatusBar.Text = $"User: {currentUser} | Mode: Training";
            }
            else
            {
                txtMode.Text = "INFERENCE MODE";
                txtMode.Background = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                pnlInference.Visibility = Visibility.Visible;
                pnlTraining.Visibility = Visibility.Collapsed;

                // Hide annotation canvas
                annotationCanvas.Visibility = Visibility.Collapsed;
                annotationCanvas.Children.Clear();
                isAnnotationMode = false;

                btnAnnotate.IsEnabled = false;
                btnStartTraining.IsEnabled = false;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Inference";
            }
        }

        #region Annotation Methods

        private void Annotate_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0)
            {
                MessageBox.Show("⚠️ Please load images first!", "No Images",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("📝 In-Place Annotation Mode!\n\n" +
                "Instructions:\n" +
                "1. Draw rectangles directly on the image\n" +
                "2. Enter character label when prompted\n" +
                "3. Labels appear in top-left corner of rectangles\n" +
                "4. RIGHT-CLICK any annotation to edit or delete\n" +
                "5. Navigate between images - annotations save automatically",
                "Annotation Ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateCanvasSize()
        {
            if (imgDisplay.Source != null && imgDisplay.ActualWidth > 0)
            {
                annotationCanvas.Width = imgDisplay.ActualWidth;
                annotationCanvas.Height = imgDisplay.ActualHeight;
            }
        }

        private void AnnotationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!isTrainingMode) return;
            if (e.ChangedButton != MouseButton.Left) return;

            annotationStartPoint = e.GetPosition(annotationCanvas);

            currentAnnotationRect = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0))
            };

            Canvas.SetLeft(currentAnnotationRect, annotationStartPoint.X);
            Canvas.SetTop(currentAnnotationRect, annotationStartPoint.Y);
            annotationCanvas.Children.Add(currentAnnotationRect);

            annotationCanvas.CaptureMouse();
        }

        private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (currentAnnotationRect == null) return;

            Point currentPoint = e.GetPosition(annotationCanvas);

            double x = Math.Min(annotationStartPoint.X, currentPoint.X);
            double y = Math.Min(annotationStartPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - annotationStartPoint.X);
            double height = Math.Abs(currentPoint.Y - annotationStartPoint.Y);

            Canvas.SetLeft(currentAnnotationRect, x);
            Canvas.SetTop(currentAnnotationRect, y);
            currentAnnotationRect.Width = width;
            currentAnnotationRect.Height = height;
        }

        private void AnnotationCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (currentAnnotationRect == null) return;
            if (e.ChangedButton != MouseButton.Left) return;

            annotationCanvas.ReleaseMouseCapture();

            // Only save if rectangle has meaningful size
            if (currentAnnotationRect.Width > 10 && currentAnnotationRect.Height > 10)
            {
                // Prompt for label
                var labelDialog = new LabelInputDialog();
                if (labelDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(labelDialog.CharLabel))
                {
                    string label = labelDialog.CharLabel;

                    // Make rectangle permanent
                    currentAnnotationRect.Stroke = Brushes.Lime;
                    currentAnnotationRect.StrokeThickness = 2;
                    currentAnnotationRect.StrokeDashArray = null;

                    // Enable right-click editing
                    currentAnnotationRect.MouseRightButtonUp += AnnotationRect_MouseRightClick;

                    // Add label text in top-left corner
                    var labelText = new TextBlock
                    {
                        Text = label,
                        Foreground = Brushes.Lime,
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                        Padding = new Thickness(2)
                    };

                    Canvas.SetLeft(labelText, Canvas.GetLeft(currentAnnotationRect) + LABEL_OFFSET);
                    Canvas.SetTop(labelText, Canvas.GetTop(currentAnnotationRect) + LABEL_OFFSET);
                    annotationCanvas.Children.Add(labelText);

                    // Save annotation data
                    var annotation = new AnnotationData
                    {
                        Label = label,
                        Bounds = new Rect(
                            Canvas.GetLeft(currentAnnotationRect),
                            Canvas.GetTop(currentAnnotationRect),
                            currentAnnotationRect.Width,
                            currentAnnotationRect.Height
                        ),
                        VisualRect = currentAnnotationRect,
                        LabelText = labelText
                    };

                    currentImageAnnotations.Add(annotation);
                    SaveCurrentImageAnnotations();
                }
                else
                {
                    // Remove rectangle if no label provided
                    annotationCanvas.Children.Remove(currentAnnotationRect);
                }
            }
            else
            {
                // Remove too-small rectangle
                annotationCanvas.Children.Remove(currentAnnotationRect);
            }

            currentAnnotationRect = null;
        }

        // NEW METHOD: Handle right-click on annotations
        private void AnnotationRect_MouseRightClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Right) return;

            // Find which annotation was right-clicked
            if (sender is Rectangle clickedRect)
            {
                var annotation = currentImageAnnotations.FirstOrDefault(a => a.VisualRect == clickedRect);
                if (annotation != null)
                {
                    // Show context menu
                    var contextMenu = new ContextMenu();

                    var editItem = new MenuItem { Header = " Edit Label" };
                    editItem.Click += (s, args) => EditAnnotationLabel(annotation);

                    var deleteItem = new MenuItem { Header = " Delete Annotation" };
                    deleteItem.Click += (s, args) => DeleteAnnotation(annotation);

                    contextMenu.Items.Add(editItem);
                    contextMenu.Items.Add(deleteItem);

                    clickedRect.ContextMenu = contextMenu;
                    contextMenu.IsOpen = true;
                }
            }
        }

        // NEW METHOD: Edit annotation label
        private void EditAnnotationLabel(AnnotationData annotation)
        {
            var labelDialog = new LabelInputDialog { CharLabel = annotation.Label };
            if (labelDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(labelDialog.CharLabel))
            {
                // Update label
                annotation.Label = labelDialog.CharLabel;
                annotation.LabelText.Text = labelDialog.CharLabel;

                // Save changes
                SaveCurrentImageAnnotations();
                UpdateMasterAnnotationFile();

                MessageBox.Show($"Label updated to: {labelDialog.CharLabel}", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // NEW METHOD: Delete annotation
        private void DeleteAnnotation(AnnotationData annotation)
        {
            var result = MessageBox.Show($"Delete annotation '{annotation.Label}'?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Remove from canvas
                annotationCanvas.Children.Remove(annotation.VisualRect);
                annotationCanvas.Children.Remove(annotation.LabelText);

                // Remove from list
                currentImageAnnotations.Remove(annotation);

                // Save changes
                SaveCurrentImageAnnotations();
                UpdateMasterAnnotationFile();

                MessageBox.Show("Annotation deleted!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SaveCurrentImageAnnotations()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;

            string imagePath = imageFiles[currentImageIndex];
            string annotationFile = GetAnnotationFilePath(imagePath);

            try
            {
                // If no annotations, delete the file
                if (currentImageAnnotations.Count == 0)
                {
                    if (File.Exists(annotationFile))
                        File.Delete(annotationFile);
                    return;
                }

                var imageSource = imgDisplay.Source as BitmapSource;
                if (imageSource == null) return;

                // Convert canvas coordinates to image coordinates
                double scaleX = imageSource.PixelWidth / annotationCanvas.ActualWidth;
                double scaleY = imageSource.PixelHeight / annotationCanvas.ActualHeight;

                var annotations = currentImageAnnotations.Select(a => new CharacterAnnotation
                {
                    Label = a.Label,
                    X = a.Bounds.X * scaleX,
                    Y = a.Bounds.Y * scaleY,
                    Width = a.Bounds.Width * scaleX,
                    Height = a.Bounds.Height * scaleY,
                    ImagePath = imagePath
                }).ToList();

                string json = JsonConvert.SerializeObject(annotations, Formatting.Indented);
                File.WriteAllText(annotationFile, json);

                UpdateMasterAnnotationFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving annotations: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAnnotationsForCurrentImage()
        {
            annotationCanvas.Children.Clear();
            currentImageAnnotations.Clear();

            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            if (imgDisplay.Source == null) return;

            string imagePath = imageFiles[currentImageIndex];
            string annotationFile = GetAnnotationFilePath(imagePath);

            if (File.Exists(annotationFile))
            {
                try
                {
                    string json = File.ReadAllText(annotationFile);
                    var annotations = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);

                    if (annotations == null) return;

                    var imageSource = imgDisplay.Source as BitmapSource;
                    if (imageSource == null) return;

                    // Convert image coordinates to canvas coordinates
                    double scaleX = annotationCanvas.ActualWidth / imageSource.PixelWidth;
                    double scaleY = annotationCanvas.ActualHeight / imageSource.PixelHeight;

                    foreach (var anno in annotations)
                    {
                        double x = anno.X * scaleX;
                        double y = anno.Y * scaleY;
                        double width = anno.Width * scaleX;
                        double height = anno.Height * scaleY;

                        // Draw rectangle
                        var rect = new Rectangle
                        {
                            Stroke = Brushes.Lime,
                            StrokeThickness = 2,
                            Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)),
                            Width = width,
                            Height = height
                        };

                        // Enable right-click editing for loaded annotations
                        rect.MouseRightButtonUp += AnnotationRect_MouseRightClick;

                        Canvas.SetLeft(rect, x);
                        Canvas.SetTop(rect, y);
                        annotationCanvas.Children.Add(rect);

                        // Draw label
                        var labelText = new TextBlock
                        {
                            Text = anno.Label,
                            Foreground = Brushes.Lime,
                            FontWeight = FontWeights.Bold,
                            FontSize = 14,
                            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                            Padding = new Thickness(2)
                        };

                        Canvas.SetLeft(labelText, x + LABEL_OFFSET);
                        Canvas.SetTop(labelText, y + LABEL_OFFSET);
                        annotationCanvas.Children.Add(labelText);

                        currentImageAnnotations.Add(new AnnotationData
                        {
                            Label = anno.Label,
                            Bounds = new Rect(x, y, width, height),
                            VisualRect = rect,
                            LabelText = labelText
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading annotations: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string GetAnnotationFilePath(string imagePath)
        {
            string fileName = IOPath.GetFileNameWithoutExtension(imagePath) + ".json";
            return IOPath.Combine(annotationsFolder, fileName);
        }

        private void UpdateMasterAnnotationFile()
        {
            try
            {
                var allAnnotations = new List<CharacterAnnotation>();

                foreach (var imgPath in imageFiles)
                {
                    string annoFile = GetAnnotationFilePath(imgPath);
                    if (File.Exists(annoFile))
                    {
                        string json = File.ReadAllText(annoFile);
                        var imgAnnos = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);
                        if (imgAnnos != null)
                            allAnnotations.AddRange(imgAnnos);
                    }
                }

                string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");

                if (allAnnotations.Count > 0)
                {
                    string masterJson = JsonConvert.SerializeObject(allAnnotations, Formatting.Indented);
                    File.WriteAllText(masterFile, masterJson);
                }
                else
                {
                    // Delete master file if no annotations exist
                    if (File.Exists(masterFile))
                        File.Delete(masterFile);
                }

                UpdateAnnotationStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating master file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        private bool CheckAnnotationsExist()
        {
            string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            return File.Exists(masterFile);
        }

        private void UpdateAnnotationStatus()
        {
            string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");

            if (File.Exists(masterFile))
            {
                try
                {
                    string json = File.ReadAllText(masterFile);
                    var annotations = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);

                    if (annotations != null && annotations.Count > 0)
                    {
                        var uniqueChars = annotations.Select(a => a.Label).Distinct().Count();
                        var uniqueImages = annotations.Select(a => a.ImagePath).Distinct().Count();

                        txtAnnotationStatus.Text = $"✅ {annotations.Count} annotations\n" +
                                                   $"{uniqueImages} images | {uniqueChars} unique chars";
                        txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Green);

                        // Enable ALL training buttons
                        btnStartTraining.IsEnabled = true;

                        // Enable the big button in the panel
                        if (btnStartTrainingBig != null)
                            btnStartTrainingBig.IsEnabled = true;
                    }
                    else
                    {
                        txtAnnotationStatus.Text = "⚠️ Annotation file empty\nPlease annotate images";
                        txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Orange);
                        btnStartTraining.IsEnabled = false;
                        if (btnStartTrainingBig != null)
                            btnStartTrainingBig.IsEnabled = false;
                    }
                }
                catch (Exception ex)
                {
                    txtAnnotationStatus.Text = $"❌ Error reading annotations:\n{ex.Message}";
                    txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                    btnStartTraining.IsEnabled = false;
                    if (btnStartTrainingBig != null)
                        btnStartTrainingBig.IsEnabled = false;
                }
            }
            else
            {
                txtAnnotationStatus.Text = "❌ No annotations found\nPlease annotate images first";
                txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                btnStartTraining.IsEnabled = false;
                if (btnStartTrainingBig != null)
                    btnStartTrainingBig.IsEnabled = false;
            }
        }

        private void CheckAnnotations_Click(object sender, RoutedEventArgs e)
        {
            UpdateAnnotationStatus();
            MessageBox.Show("Annotation status refreshed!", "Status Updated",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void StartTraining_Click(object sender, RoutedEventArgs e)
        {
            // 1. HARDCODED PYTHON PATH (From your "where python" command)
            string pythonPath = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptPath = IOPath.Combine(baseDir, "ocr_trainer.py");
            string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            int epochs = int.TryParse(txtEpochs.Text, out int e_val) ? e_val : 50;

            // 2. DEBUG CHECKS - Verify files exist before running
            if (!File.Exists(pythonPath))
            {
                MessageBox.Show($"❌ C# cannot find Python at:\n{pythonPath}", "Path Error");
                return;
            }
            if (!File.Exists(scriptPath))
            {
                MessageBox.Show($"❌ C# cannot find the script at:\n{scriptPath}\n\nDid you set 'Copy to Output Directory'?", "Path Error");
                return;
            }

            btnStartTraining.IsEnabled = false;
            btnAnnotate.IsEnabled = false;
            progressBarTraining.Value = 0;
            txtTrainingStatus.Text = "🚀 Starting Process...";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    // We wrap arguments in quotes to handle the space in "Pixtech Workstation"
                    Arguments = $"\"{scriptPath}\" \"{masterFile}\" {epochs}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = baseDir
                };

                await Task.Run(() =>
                {
                    using (var process = new Process { StartInfo = startInfo })
                    {
                        process.OutputDataReceived += (s, args) =>
                        {
                            if (args.Data == null) return;
                            Dispatcher.Invoke(() =>
                            {
                                // Update UI with Python Output
                                txtTrainingStatus.Text = args.Data;

                                // Parse Progress
                                if (args.Data.Contains("Epoch ["))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(args.Data, @"Epoch \[(\d+)/(\d+)\]");
                                    if (match.Success)
                                    {
                                        int current = int.Parse(match.Groups[1].Value);
                                        int total = int.Parse(match.Groups[2].Value);
                                        progressBarTraining.Value = (current * 100.0) / total;
                                    }
                                }
                            });
                        };

                        // CAPTURE ERRORS HERE
                        var errorOutput = new System.Text.StringBuilder();
                        process.ErrorDataReceived += (s, args) =>
                        {
                            if (args.Data == null) return;
                            errorOutput.AppendLine(args.Data);
                            Dispatcher.Invoke(() => txtTrainingStatus.Text = $"⚠️ {args.Data}");
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();

                        Dispatcher.Invoke(() =>
                        {
                            if (process.ExitCode == 0)
                            {
                                progressBarTraining.Value = 100;
                                MessageBox.Show("✅ Training Complete!", "Success");
                                isModelTrained = true;
                            }
                            else
                            {
                                // 3. SHOW THE REAL ERROR
                                MessageBox.Show($"❌ Training Failed (Code {process.ExitCode})\n\nError Log:\n{errorOutput}",
                                                "Python Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ C# Execution Error:\n{ex.Message}");
            }
            finally
            {
                btnStartTraining.IsEnabled = true;
                btnAnnotate.IsEnabled = true;
            }
        }

        private async Task<string> PerformCustomOCR(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string modelPath = IOPath.Combine(modelsFolder, "best_model.pth");
                    string scriptPath = IOPath.Combine(baseDir, "ocr_inference.py");

                    if (!File.Exists(modelPath))
                        return "❌ Trained model not found!\n\nPlease train a model first in Training Mode.";

                    if (!File.Exists(scriptPath))
                        return $"❌ Inference script not found!\n\nExpected: {scriptPath}";

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\" \"{modelPath}\" \"{imagePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                            return "❌ Could not start Python!";

                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode != 0)
                            return $"❌ Inference Error:\n{error}";

                        if (string.IsNullOrWhiteSpace(output))
                            return "ℹ️ No prediction available";

                        var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        var result = "✅ === CUSTOM OCR RESULTS ===\n\n";
                        int count = 1;

                        foreach (var line in lines)
                        {
                            if (line.Contains("|"))
                            {
                                var parts = line.Split('|');
                                if (parts.Length == 2)
                                {
                                    string character = parts[0].Trim();
                                    string confidence = parts[1].Trim();

                                    if (count == 1)
                                        result += $"🎯 Top Prediction: {character}\n";
                                    else
                                        result += $"   Alternative #{count - 1}: {character}\n";

                                    result += $"   Confidence: {confidence}%\n\n";
                                    count++;
                                }
                            }
                        }

                        return result;
                    }
                }
                catch (Exception ex)
                {
                    return $"❌ Exception: {ex.Message}";
                }
            });
        }

        private async Task<string> PerformEasyOCR(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptPath = IOPath.Combine(baseDir, "easyocr_service.py");

                    if (!File.Exists(scriptPath))
                    {
                        return $"❌ EasyOCR script NOT FOUND!\n\nPlace easyocr_service.py in:\n{baseDir}";
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
                            return "❌ Could not start Python!";

                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode != 0)
                            return $"❌ Python Error:\n{error}";

                        if (string.IsNullOrWhiteSpace(output))
                            return "ℹ️ No text detected";

                        var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        var result = "✅ === EASYOCR RESULTS ===\n\n";
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
                                    totalConf += conf;
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
                    return $"❌ Exception: {ex.Message}";
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
            var dialog = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                    if (!imageFiles.Contains(file)) imageFiles.Add(file);

                UpdateImageList();

                if (imageFiles.Count > 0)
                {
                    currentImageIndex = 0;
                    ShowImage(imageFiles[0]);
                }

                if (isTrainingMode)
                    btnAnnotate.IsEnabled = true;
            }
        }

        private void ShowImage(string path)
        {
            try
            {
                // Save annotations from previous image
                if (isTrainingMode && currentImageAnnotations.Count > 0)
                {
                    SaveCurrentImageAnnotations();
                }

                txtPlaceholder.Visibility = Visibility.Collapsed;
                var bitmap = new BitmapImage(new Uri(path));
                imgDisplay.Source = bitmap;
                imgDisplay.Visibility = Visibility.Visible;

                // Update canvas after image loads
                Dispatcher.InvokeAsync(() =>
                {
                    UpdateCanvasSize();

                    if (isTrainingMode)
                    {
                        annotationCanvas.Visibility = Visibility.Visible;
                        LoadAnnotationsForCurrentImage();
                    }
                }, DispatcherPriority.Loaded);
            }
            catch { }
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem item && item.Tag is string path)
            {
                currentImageIndex = imageFiles.IndexOf(path);
                ShowImage(path);
            }
        }

        private void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            LoadFiles_Click(sender, e);
        }

        private void ToolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (cmbToolType?.SelectedItem is ComboBoxItem item)
                    currentTool = item.Content.ToString();
            }
            catch { }
        }

        private async void RunInference_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0)
            {
                MessageBox.Show("⚠️ Please load images first!", "No Images",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnRunInference.IsEnabled = false;
            btnRunInference.Content = "Processing...";
            progressBar.Value = 0;

            string currentImagePath = null;
            if (lstImages.SelectedItem is ListBoxItem item && item.Tag is string path)
                currentImagePath = path;
            else
                currentImagePath = imageFiles[0];

            txtResult.Text = "🔍 Running OCR...\n\nPlease wait...";
            txtConfidence.Text = "⏳ Processing...";
            txtDecision.Text = "WORKING";
            txtDecision.Foreground = Brushes.Orange;

            for (int i = 1; i <= 30; i++)
            {
                progressBar.Value = i;
                await Task.Delay(10);
            }

            string ocrResult;
            string modelPath = IOPath.Combine(modelsFolder, "best_model.pth");

            if (File.Exists(modelPath) && currentTool == "OCR")
            {
                txtStatus.Text = "Using custom trained model...";
                ocrResult = await PerformCustomOCR(currentImagePath);
            }
            else
            {
                txtStatus.Text = "Using EasyOCR pretrained model...";
                ocrResult = await PerformEasyOCR(currentImagePath);
            }

            progressBar.Value = 100;
            txtResult.Text = ocrResult;

            if (ocrResult.StartsWith("✅"))
            {
                txtConfidence.Text = "✅ OCR Complete";
                txtDecision.Text = "SUCCESS";
                txtDecision.Foreground = Brushes.Green;
            }
            else
            {
                txtConfidence.Text = "❌ OCR Failed";
                txtDecision.Text = "ERROR";
                txtDecision.Foreground = Brushes.Red;
            }

            btnRunInference.IsEnabled = true;
            btnRunInference.Content = "▶️ RUN INFERENCE";
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (!isModelTrained && !File.Exists(IOPath.Combine(modelsFolder, "best_model.pth")))
            {
                MessageBox.Show("⚠️ No trained model available!",
                    "No Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog { Filter = "PyTorch Model|*.pth" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string sourcePath = IOPath.Combine(modelsFolder, "best_model.pth");
                    File.Copy(sourcePath, dialog.FileName, true);
                    MessageBox.Show("✅ Model saved successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"❌ Error: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "PyTorch Model|*.pth" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string destPath = IOPath.Combine(modelsFolder, "best_model.pth");
                    File.Copy(dialog.FileName, destPath, true);
                    isModelTrained = true;
                    MessageBox.Show("✅ Model loaded successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"❌ Error: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("✅ ACCEPTED");
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("❌ REJECTED");
        }

        private void UpdateImageList()
        {
            lstImages.Items.Clear();
            foreach (var path in imageFiles)
                lstImages.Items.Add(new ListBoxItem
                {
                    Content = IOPath.GetFileName(path),
                    Tag = path
                });
            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Save any pending annotations
            if (isTrainingMode && currentImageAnnotations.Count > 0)
            {
                SaveCurrentImageAnnotations();
            }

            authTimer?.Stop();
            base.OnClosing(e);
        }
    }

    // Annotation data structure
    public class AnnotationData
    {
        public string Label { get; set; }
        public Rect Bounds { get; set; }
        public Rectangle VisualRect { get; set; }
        public TextBlock LabelText { get; set; }
    }

    // Character annotation class for JSON
    public class CharacterAnnotation
    {
        public string Label { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string ImagePath { get; set; }

        public string BoundsText => $"({X:F0}, {Y:F0}) - {Width:F0}x{Height:F0}";
    }
}