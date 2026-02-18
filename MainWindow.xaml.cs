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

        // CHANGE THIS LINE TO SWITCH PROJECTS
        private string currentProjectName = "DefaultProject";

        private string annotationsFolder;
        private string modelsFolder;
        private string currentProjectFolder;
        private const string PROJECTS_BASE_DIR = "Projects";

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

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(IOPath.Combine(baseDir, PROJECTS_BASE_DIR));

            authTimer = new DispatcherTimer();
            authTimer.Interval = TimeSpan.FromMinutes(AUTH_TIMEOUT_MINUTES);
            authTimer.Tick += AuthTimer_Tick;
            authTimer.Start();

            MessageBox.Show($"Welcome {currentUser}!\n\nCurrent Project: {currentProjectName}", "PixTech");

            SetupProjectFolders();

            if (txtStatusBar != null)
                txtStatusBar.Text = $"User: {currentUser} | Project: {currentProjectName}";

            UpdateAnnotationStatus();
        }

        private void SetupProjectFolders()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            currentProjectFolder = IOPath.Combine(baseDir, PROJECTS_BASE_DIR, currentProjectName);
            annotationsFolder = IOPath.Combine(currentProjectFolder, "Annotations");
            modelsFolder = IOPath.Combine(baseDir, "Models");

            Directory.CreateDirectory(currentProjectFolder);
            Directory.CreateDirectory(annotationsFolder);
            Directory.CreateDirectory(modelsFolder);
            Directory.CreateDirectory(IOPath.Combine(annotationsFolder, "images"));
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
                btnAnnotate.Visibility = Visibility.Visible;
                btnStartTraining.Visibility = Visibility.Visible;
                btnCheckAnnotations.Visibility = Visibility.Visible;
                btnAnnotate.IsEnabled = imageFiles.Count > 0;

                if (imgDisplay.Source != null)
                {
                    annotationCanvas.Visibility = Visibility.Visible;
                    UpdateCanvasSize();
                    LoadAnnotationsForCurrentImage();
                }

                UpdateAnnotationStatus();
                if (CheckAnnotationsExist()) btnStartTraining.IsEnabled = true;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Training | Project: {currentProjectName}";
            }
            else
            {
                txtMode.Text = "INFERENCE MODE";
                txtMode.Background = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                pnlInference.Visibility = Visibility.Visible;
                pnlTraining.Visibility = Visibility.Collapsed;
                annotationCanvas.Visibility = Visibility.Collapsed;
                annotationCanvas.Children.Clear();
                isAnnotationMode = false;
                btnAnnotate.IsEnabled = false;
                btnStartTraining.IsEnabled = false;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Inference | Project: {currentProjectName}";
            }
        }

        #region Annotation Methods

        private void Annotate_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0)
            {
                MessageBox.Show("Please load images first!", "No Images", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show("Draw rectangles around each character, then enter the label.\n\nRight-click on rectangles to edit or delete.\n\nAnnotations are saved automatically!", "Annotation Mode Ready", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (!isTrainingMode || e.ChangedButton != MouseButton.Left) return;
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
            if (currentAnnotationRect == null || e.ChangedButton != MouseButton.Left) return;
            annotationCanvas.ReleaseMouseCapture();

            if (currentAnnotationRect.Width > 10 && currentAnnotationRect.Height > 10)
            {
                var labelDialog = new LabelInputDialog();
                if (labelDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(labelDialog.CharLabel))
                {
                    string label = labelDialog.CharLabel;
                    currentAnnotationRect.Stroke = Brushes.Lime;
                    currentAnnotationRect.StrokeThickness = 2;
                    currentAnnotationRect.StrokeDashArray = null;
                    currentAnnotationRect.MouseRightButtonUp += AnnotationRect_MouseRightClick;

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

                    var annotation = new AnnotationData
                    {
                        Label = label,
                        Bounds = new Rect(Canvas.GetLeft(currentAnnotationRect), Canvas.GetTop(currentAnnotationRect), currentAnnotationRect.Width, currentAnnotationRect.Height),
                        VisualRect = currentAnnotationRect,
                        LabelText = labelText
                    };
                    currentImageAnnotations.Add(annotation);
                    SaveCurrentImageAnnotations();
                }
                else annotationCanvas.Children.Remove(currentAnnotationRect);
            }
            else annotationCanvas.Children.Remove(currentAnnotationRect);

            currentAnnotationRect = null;
        }

        private void AnnotationRect_MouseRightClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Right) return;
            if (sender is Rectangle clickedRect)
            {
                var annotation = currentImageAnnotations.FirstOrDefault(a => a.VisualRect == clickedRect);
                if (annotation != null)
                {
                    var contextMenu = new ContextMenu();
                    var editItem = new MenuItem { Header = "Edit Label" };
                    editItem.Click += (s, args) => EditAnnotationLabel(annotation);
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Click += (s, args) => DeleteAnnotation(annotation);
                    contextMenu.Items.Add(editItem);
                    contextMenu.Items.Add(deleteItem);
                    clickedRect.ContextMenu = contextMenu;
                    contextMenu.IsOpen = true;
                }
            }
        }

        private void SeparateAnnotations_Click(object sender, RoutedEventArgs e)
        {
            SeparateAnnotationsByImage();
        }

        private void EditAnnotationLabel(AnnotationData annotation)
        {
            var labelDialog = new LabelInputDialog { CharLabel = annotation.Label };
            if (labelDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(labelDialog.CharLabel))
            {
                annotation.Label = labelDialog.CharLabel;
                annotation.LabelText.Text = labelDialog.CharLabel;
                SaveCurrentImageAnnotations();
                UpdateMasterAnnotationFile();
            }
        }

        private void DeleteAnnotation(AnnotationData annotation)
        {
            var result = MessageBox.Show($"Delete '{annotation.Label}'?", "Confirm", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                annotationCanvas.Children.Remove(annotation.VisualRect);
                annotationCanvas.Children.Remove(annotation.LabelText);
                currentImageAnnotations.Remove(annotation);
                SaveCurrentImageAnnotations();
                UpdateMasterAnnotationFile();
            }
        }

        private void ClearCurrentImageAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count)
            {
                MessageBox.Show("No image selected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Delete all annotations for THIS image only?\n\n{IOPath.GetFileName(imageFiles[currentImageIndex])}",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    annotationCanvas.Children.Clear();
                    currentImageAnnotations.Clear();
                    string imagePath = imageFiles[currentImageIndex];
                    string annotationFile = GetAnnotationFilePath(imagePath);

                    if (File.Exists(annotationFile))
                    {
                        File.Delete(annotationFile);
                        System.Diagnostics.Debug.WriteLine($"Deleted: {annotationFile}");
                    }

                    UpdateMasterAnnotationFile();
                    UpdateAnnotationStatus();

                    MessageBox.Show(
                        $"Annotations cleared for this image!\n\n{IOPath.GetFileName(imagePath)}",
                        "Cleared",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error clearing annotations: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SeparateAnnotationsByImage()
        {
            try
            {
                string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");

                if (!File.Exists(masterFile))
                {
                    MessageBox.Show("No master annotation file found!", "Info");
                    return;
                }

                string json = File.ReadAllText(masterFile);
                var allAnnotations = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);

                if (allAnnotations == null || allAnnotations.Count == 0)
                {
                    MessageBox.Show("No annotations found!", "Info");
                    return;
                }

                var groupedByImage = allAnnotations.GroupBy(a => a.ImagePath);

                int savedFiles = 0;
                foreach (var group in groupedByImage)
                {
                    string imagePath = group.Key;
                    var imageAnnotations = group.ToList();
                    string annoFile = GetAnnotationFilePath(imagePath);
                    string imageJson = JsonConvert.SerializeObject(imageAnnotations, Formatting.Indented);
                    File.WriteAllText(annoFile, imageJson);
                    savedFiles++;
                    System.Diagnostics.Debug.WriteLine($"Saved {imageAnnotations.Count} annotations to {IOPath.GetFileName(annoFile)}");
                }

                MessageBox.Show($"Separated annotations into {savedFiles} individual files!\n\nNow each image has its own annotation file.", "Success");

                if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count)
                {
                    LoadAnnotationsForCurrentImage();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error separating annotations: {ex.Message}", "Error");
            }
        }

        private void SaveCurrentImageAnnotations()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            string imagePath = imageFiles[currentImageIndex];
            string annotationFile = GetAnnotationFilePath(imagePath);

            try
            {
                if (currentImageAnnotations.Count == 0)
                {
                    if (File.Exists(annotationFile)) File.Delete(annotationFile);
                    return;
                }

                var imageSource = imgDisplay.Source as BitmapSource;
                if (imageSource == null) return;

                // IMPORTANT: Scale from canvas coordinates to actual image coordinates
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
                MessageBox.Show($"Error saving annotations: {ex.Message}", "Error");
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
                        if (imgAnnos != null) allAnnotations.AddRange(imgAnnos);
                    }
                }

                string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
                if (allAnnotations.Count > 0)
                {
                    string masterJson = JsonConvert.SerializeObject(allAnnotations, Formatting.Indented);
                    File.WriteAllText(masterFile, masterJson);
                }
                else if (File.Exists(masterFile)) File.Delete(masterFile);

                UpdateAnnotationStatus();
            }
            catch { }
        }

        #endregion

        private void LoadAnnotationsForCurrentImage()
        {
            annotationCanvas.Children.Clear();
            currentImageAnnotations.Clear();

            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count || imgDisplay.Source == null)
                return;

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

                    double scaleX = annotationCanvas.ActualWidth / imageSource.PixelWidth;
                    double scaleY = annotationCanvas.ActualHeight / imageSource.PixelHeight;

                    foreach (var anno in annotations)
                    {
                        double x = anno.X * scaleX;
                        double y = anno.Y * scaleY;
                        double width = anno.Width * scaleX;
                        double height = anno.Height * scaleY;

                        var rect = new Rectangle
                        {
                            Stroke = Brushes.Lime,
                            StrokeThickness = 2,
                            Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)),
                            Width = width,
                            Height = height
                        };
                        rect.MouseRightButtonUp += AnnotationRect_MouseRightClick;
                        Canvas.SetLeft(rect, x);
                        Canvas.SetTop(rect, y);
                        annotationCanvas.Children.Add(rect);

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
                    System.Diagnostics.Debug.WriteLine($"Error loading annotations: {ex.Message}");
                }
            }
        }

        private bool CheckAnnotationsExist()
        {
            return File.Exists(IOPath.Combine(annotationsFolder, "all_annotations.json"));
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
                        txtAnnotationStatus.Text = $" {annotations.Count} annotations\n{uniqueImages} images | {uniqueChars} unique characters";
                        txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Green);
                        btnStartTraining.IsEnabled = true;
                        if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = true;
                    }
                    else
                    {
                        txtAnnotationStatus.Text = " No annotations yet";
                        txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Orange);
                        btnStartTraining.IsEnabled = false;
                        if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
                    }
                }
                catch
                {
                    txtAnnotationStatus.Text = " Error reading annotations";
                    txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                    btnStartTraining.IsEnabled = false;
                    if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
                }
            }
            else
            {
                txtAnnotationStatus.Text = " No annotations yet";
                txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                btnStartTraining.IsEnabled = false;
                if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
            }
        }

        private void CheckAnnotations_Click(object sender, RoutedEventArgs e)
        {
            UpdateAnnotationStatus();
            MessageBox.Show("Annotation status refreshed!", "Updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                " WARNING \n\nThis will DELETE ALL annotations from ALL images!\n\nYou'll need to re-annotate everything.\n\nAre you absolutely sure?",
                "Confirm Delete All",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (Directory.Exists(annotationsFolder))
                    {
                        foreach (var file in Directory.GetFiles(annotationsFolder, "*.json"))
                        {
                            File.Delete(file);
                            System.Diagnostics.Debug.WriteLine($"Deleted: {file}");
                        }
                    }

                    currentImageAnnotations.Clear();
                    annotationCanvas.Children.Clear();
                    UpdateAnnotationStatus();

                    MessageBox.Show(
                        "All annotations deleted successfully!\n\nYou can now re-annotate your images from scratch.",
                        "Annotations Cleared",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error clearing annotations: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ========================================
        // SIAMESE NETWORK TRAINING
        // ========================================
        private async void StartTraining_Click(object sender, RoutedEventArgs e)
        {
            string pythonPath = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string trainScript = IOPath.Combine(baseDir, "train_last_layer.py");
            string annotationFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            string modelDir = IOPath.Combine(currentProjectFolder, "TrainedModel");

            int epochs = 50;
            int batchSize = 32;
            if (txtEpochs != null && int.TryParse(txtEpochs.Text, out int e2)) epochs = e2;
            if (txtBatchSize != null && int.TryParse(txtBatchSize.Text, out int b2)) batchSize = b2;

            // ── Validation ────────────────────────────────────────────────────────────
            if (!File.Exists(trainScript))
            {
                MessageBox.Show($"train_last_layer.py not found in:\n{baseDir}", "Missing Script");
                return;
            }
            if (!File.Exists(annotationFile))
            {
                MessageBox.Show("No annotations found!\n\nAnnotate the wrong characters first, then train.",
                                "No Annotations");
                return;
            }

            btnStartTraining.IsEnabled = false;
            if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
            progressBarTraining.Value = 0;
            txtTrainingStatus.Text = "Starting training...";

            var errors = new System.Text.StringBuilder();

            try
            {
                await Task.Run(() =>
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = pythonPath,
                            Arguments = $"\"{trainScript}\" \"{annotationFile}\" \"{modelDir}\" "
                                      + $"--epochs {epochs} --batch {batchSize} --augments 80",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = baseDir
                        }
                    };

                    p.OutputDataReceived += (s, args) =>
                    {
                        if (string.IsNullOrEmpty(args.Data)) return;
                        Dispatcher.Invoke(() =>
                        {
                            txtTrainingStatus.Text = args.Data;
                            System.Diagnostics.Debug.WriteLine($"[TRAIN] {args.Data}");

                            // Update progress bar from "Epoch X/Y" lines
                            if (args.Data.StartsWith("Epoch"))
                            {
                                // "Epoch   5/ 50 | Train: 98.2% | Val: 96.1% | 1.3s"
                                var parts = args.Data.Split('/');
                                if (parts.Length >= 2)
                                {
                                    int cur = int.TryParse(parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Last(), out int c) ? c : 0;
                                    int total = int.TryParse(parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).First(), out int t) ? t : epochs;
                                    progressBarTraining.Value = cur * 100.0 / total;
                                }
                            }

                            // Training finished
                            if (args.Data.StartsWith("TRAINING_COMPLETE:"))
                            {
                                string accStr = args.Data.Split(':')[1];
                                double.TryParse(accStr, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out double acc);

                                progressBarTraining.Value = 100;
                                txtTrainingStatus.Text = $"Done! Val accuracy: {acc:F1}%";

                                MessageBox.Show(
                                    $"TRAINING COMPLETE!\n\n" +
                                    $"Validation accuracy: {acc:F1}%\n\n" +
                                    $"Your model has learned what your stamp characters\n" +
                                    $"look like visually — it will now correct EasyOCR\n" +
                                    $"on ANY future image automatically.\n\n" +
                                    $"Switch to Inference Mode and test it.",
                                    "Training Complete",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                        });
                    };

                    p.ErrorDataReceived += (s, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            errors.AppendLine(args.Data);
                            System.Diagnostics.Debug.WriteLine($"[TRAIN ERR] {args.Data}");
                        }
                    };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();

                    if (p.ExitCode != 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            txtTrainingStatus.Text = "Training failed!";
                            string errText = errors.ToString();
                            string display = errText.Length > 800
                                ? "...\n" + errText.Substring(errText.Length - 800)
                                : errText;
                            MessageBox.Show(
                                $"Training failed.\n\nPython error:\n{display}",
                                "Training Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error");
                txtTrainingStatus.Text = "Training failed!";
            }
            finally
            {
                btnStartTraining.IsEnabled = true;
                if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = true;
            }
        }


        // ========================================
        // SIAMESE NETWORK INFERENCE
        // ========================================
        private async Task<SiameseOCRResult> RunCustomOCR(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string pythonPath = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptPath = IOPath.Combine(baseDir, "inference_custom.py");
                    string customModelPath = IOPath.Combine(modelsFolder, "custom_finetuned.pth");

                    if (!File.Exists(scriptPath))
                    {
                        return new SiameseOCRResult
                        {
                            Success = false,
                            Error = $"inference_custom.py not found in {baseDir}"
                        };
                    }

                    if (!File.Exists(customModelPath))
                    {
                        return new SiameseOCRResult
                        {
                            Success = false,
                            Error = "No trained custom model! Train first in Training Mode."
                        };
                    }

                    using (var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"\"{scriptPath}\" \"{customModelPath}\" \"{imagePath}\" output",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    }))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        string errors = p.StandardError.ReadToEnd();
                        p.WaitForExit();

                        System.Diagnostics.Debug.WriteLine($"[CUSTOM] Output: {output}");
                        System.Diagnostics.Debug.WriteLine($"[CUSTOM] Errors: {errors}");

                        if (p.ExitCode != 0)
                        {
                            return new SiameseOCRResult
                            {
                                Success = false,
                                Error = $"Python error:\n{errors}"
                            };
                        }

                        string resultFile = IOPath.Combine(baseDir, "output", "result.json");
                        if (File.Exists(resultFile))
                        {
                            string resultJson = File.ReadAllText(resultFile);
                            var result = JsonConvert.DeserializeObject<SiameseOCRResult>(resultJson);
                            result.Success = true;
                            return result;
                        }

                        return new SiameseOCRResult
                        {
                            Success = false,
                            Error = "No output file generated"
                        };
                    }
                }
                catch (Exception ex)
                {
                    return new SiameseOCRResult
                    {
                        Success = false,
                        Error = ex.Message
                    };
                }
            });
        }
        // ========================================
        // EASYOCR INFERENCE
        // ========================================
        private async Task<SiameseOCRResult> RunEasyOCR(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string pythonPath = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptPath = IOPath.Combine(baseDir, "final_ocr.py");
                    string correctionModel = IOPath.Combine(modelsFolder, "correction_model.pth");
                    string modelArg = File.Exists(correctionModel) ? $"\"{correctionModel}\"" : "\"\"";


                    if (!File.Exists(scriptPath))
                    {
                        return new SiameseOCRResult
                        {
                            Success = false,
                            Error = $"final_ocr.py not found in {baseDir}"
                        };
                    }

                    // Use correction model if it exists, otherwise pure EasyOCR
                    

                    using (var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"\"{scriptPath}\" \"{imagePath}\" {modelArg} output",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    }))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        string errors = p.StandardError.ReadToEnd();
                        p.WaitForExit();

                        System.Diagnostics.Debug.WriteLine($"[FINAL_OCR] Output: {output}");
                        System.Diagnostics.Debug.WriteLine($"[FINAL_OCR] Errors: {errors}");

                        if (p.ExitCode != 0)
                        {
                            return new SiameseOCRResult
                            {
                                Success = false,
                                Error = $"OCR Error:\n{errors}"
                            };
                        }

                        string resultFile = IOPath.Combine(baseDir, "output", "result.json");
                        if (File.Exists(resultFile))
                        {
                            string resultJson = File.ReadAllText(resultFile);
                            var result = JsonConvert.DeserializeObject<SiameseOCRResult>(resultJson);
                            result.Success = true;
                            return result;
                        }

                        return new SiameseOCRResult
                        {
                            Success = false,
                            Error = "No result file generated"
                        };
                    }
                }
                catch (Exception ex)
                {
                    return new SiameseOCRResult
                    {
                        Success = false,
                        Error = $"Exception: {ex.Message}"
                    };
                }
            });
        }
        private async void RunInference_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0)
            {
                MessageBox.Show("Load images first!", "No Images");
                return;
            }

            string imagePath = (lstImages.SelectedItem is ListBoxItem item && item.Tag is string p)
                ? p : imageFiles[currentImageIndex];

            string modelDir = IOPath.Combine(currentProjectFolder, "TrainedModel");
            string modelDirArg = Directory.Exists(modelDir) ? $"\"{modelDir}\"" : "\"\"";

            btnRunInference.IsEnabled = false;
            progressBar.IsIndeterminate = true;
            txtResult.Text = "Running OCR...";
            txtStatus.Text = "Loading EasyOCR (first run may take 30-60s)...";

            try
            {
                string pythonPath = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = IOPath.Combine(baseDir, "final_ocr.py");

                var result = await Task.Run(() =>
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"\"{scriptPath}\" \"{imagePath}\" {modelDirArg} output",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    });

                    string output = proc.StandardOutput.ReadToEnd();
                    string errors = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    System.Diagnostics.Debug.WriteLine($"[OCR OUT] {output}");
                    System.Diagnostics.Debug.WriteLine($"[OCR ERR] {errors}");

                    if (proc.ExitCode != 0)
                        return new SiameseOCRResult { Success = false, Error = errors };

                    string resultFile = IOPath.Combine(baseDir, "output", "result.json");
                    if (!File.Exists(resultFile))
                        return new SiameseOCRResult { Success = false, Error = "No result.json generated" };

                    var r = JsonConvert.DeserializeObject<SiameseOCRResult>(
                                File.ReadAllText(resultFile));
                    r.Success = true;
                    return r;
                });

                if (result.Success)
                {
                    txtResult.Text = result.Text;
                    txtConfidence.Text = $"Confidence: {result.Confidence:F1}%";

                    bool hasModel = Directory.Exists(modelDir) &&
                                    File.Exists(IOPath.Combine(modelDir, "best_model.pth"));

                    if (result.Confidence >= 80)
                    {
                        txtDecision.Text = "HIGH CONFIDENCE";
                        txtDecision.Foreground = new SolidColorBrush(Colors.Green);
                    }
                    else if (result.Confidence >= 60)
                    {
                        txtDecision.Text = "MEDIUM CONFIDENCE";
                        txtDecision.Foreground = new SolidColorBrush(Colors.Orange);
                    }
                    else
                    {
                        txtDecision.Text = "LOW CONFIDENCE";
                        txtDecision.Foreground = new SolidColorBrush(Colors.Red);
                    }

                    txtStatus.Text = hasModel
                        ? "EasyOCR + Your Trained Model"
                        : "EasyOCR only — train a model to improve accuracy";
                }
                else
                {
                    txtResult.Text = $"Error: {result.Error}";
                    txtStatus.Text = "Inference failed!";
                    MessageBox.Show($"OCR Error:\n{result.Error}", "Error");
                }
            }
            catch (Exception ex)
            {
                txtResult.Text = $"Exception: {ex.Message}";
                txtStatus.Text = "Failed!";
            }
            finally
            {
                btnRunInference.IsEnabled = false;
                btnRunInference.IsEnabled = true;
                progressBar.IsIndeterminate = false;
                progressBar.Value = 0;
            }
        }


        // ========================================
        // LEGACY METHODS (Keep for compatibility)
        // ========================================
        private void AuthTimer_Tick(object sender, EventArgs e)
        {
            authTimer.Stop();
            var reAuth = new ReAuthWindow(currentUser);
            if (reAuth.ShowDialog() == true && reAuth.IsAuthenticated)
            {
                authTimer.Start();
            }
            else Application.Current.Shutdown();
        }

        private void LoadFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
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
                if (isTrainingMode) btnAnnotate.IsEnabled = true;
            }
        }

        private void ShowImage(string path)
        {
            try
            {
                if (isTrainingMode && currentImageAnnotations.Count > 0)
                    SaveCurrentImageAnnotations();

                annotationCanvas.Children.Clear();
                currentImageAnnotations.Clear();
                txtPlaceholder.Visibility = Visibility.Collapsed;

                if (imgDisplay.Source != null)
                {
                    var oldSource = imgDisplay.Source;
                    imgDisplay.Source = null;
                    if (oldSource is BitmapImage bmp)
                    {
                        bmp.StreamSource?.Dispose();
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                imgDisplay.Source = bitmap;
                imgDisplay.Visibility = Visibility.Visible;

                Dispatcher.InvokeAsync(() =>
                {
                    imgDisplay.UpdateLayout();
                    UpdateCanvasSize();

                    if (isTrainingMode)
                    {
                        annotationCanvas.Visibility = Visibility.Visible;
                        LoadAnnotationsForCurrentImage();
                    }
                }, DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading image: {ex.Message}", "Error");
            }
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem item && item.Tag is string path)
            {
                currentImageIndex = imageFiles.IndexOf(path);
                ShowImage(path);
            }
        }

        private void LoadFolder_Click(object sender, RoutedEventArgs e) => LoadFiles_Click(sender, e);

        private void ToolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try { if (cmbToolType?.SelectedItem is ComboBoxItem item) currentTool = item.Content.ToString(); }
            catch { }
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            string modelPath = IOPath.Combine(modelsFolder, "siamese_model.pth");
            if (!File.Exists(modelPath))
            {
                MessageBox.Show("No trained Siamese model found!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog { Filter = "PyTorch Model|*.pth", FileName = $"{currentProjectName}_siamese_ocr.pth" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.Copy(modelPath, dialog.FileName, true);
                    MessageBox.Show($" Model saved successfully!\n\n{dialog.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    Directory.CreateDirectory(modelsFolder);
                    File.Copy(dialog.FileName, IOPath.Combine(modelsFolder, "siamese_model.pth"), true);
                    MessageBox.Show(" Model loaded successfully!", "Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(" Result ACCEPTED", "Accepted", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(" Result REJECTED", "Rejected", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateImageList()
        {
            lstImages.Items.Clear();
            foreach (var path in imageFiles)
                lstImages.Items.Add(new ListBoxItem { Content = IOPath.GetFileName(path), Tag = path });
            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        private void AutoDetectCharacters_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Auto-detection with CRAFT is optional.\n\n" +
                "For Siamese OCR, you can:\n" +
                "1. Manually annotate (recommended for accuracy)\n" +
                "2. Use CRAFT for quick detection (if craft_detector.py exists)\n\n" +
                "Manual annotation gives better results with few-shot learning!",
                "Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (isTrainingMode && currentImageAnnotations.Count > 0) SaveCurrentImageAnnotations();
            authTimer?.Stop();
            base.OnClosing(e);
        }
    }

    public class AnnotationData
    {
        public string Label { get; set; }
        public Rect Bounds { get; set; }
        public Rectangle VisualRect { get; set; }
        public TextBlock LabelText { get; set; }
    }

    public class CharacterAnnotation
    {
        public string Label { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string ImagePath { get; set; }
    }

    public class SiameseOCRResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public int CharCount { get; set; }
        public string Error { get; set; }
    }
}