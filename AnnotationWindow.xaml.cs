using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.IO;
using Newtonsoft.Json;
using System.Linq;

// Add this alias to resolve ambiguity
using IOPath = System.IO.Path;
namespace PixtechApplication
{
    public partial class AnnotationWindow : Window
    {
        private List<string> imageFiles;
        private int currentImageIndex;
        private Point startPoint;
        private Rectangle currentRectangle;
        private bool isDrawing = false;
        private ObservableCollection<CharacterAnnotation> annotations;
        private double zoomLevel = 1.0;
        private string annotationsFolder;

        public AnnotationWindow(List<string> images, int startIndex = 0)
        {
            InitializeComponent();
            imageFiles = images;
            currentImageIndex = startIndex;
            annotations = new ObservableCollection<CharacterAnnotation>();
            lstAnnotations.ItemsSource = annotations;
            
            // Create annotations folder
            annotationsFolder = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Annotations");
            Directory.CreateDirectory(annotationsFolder);
            
            LoadImage(currentImageIndex);
            txtCharLabel.Focus();
        }

        private void LoadImage(int index)
        {
            if (index < 0 || index >= imageFiles.Count) return;

            currentImageIndex = index;
            string imagePath = imageFiles[index];
            
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                
                annotationImage.Source = bitmap;
                imageCanvas.Width = bitmap.PixelWidth;
                imageCanvas.Height = bitmap.PixelHeight;
                
                txtImageName.Text = $"Image: {IOPath.GetFileName(imagePath)} ({index + 1}/{imageFiles.Count})";
                
                // Load existing annotations for this image
                LoadAnnotationsForImage(imagePath);
                ResetView_Click(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading image: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAnnotationsForImage(string imagePath)
        {
            annotations.Clear();
            
            // Clear existing rectangles from canvas
            var rectanglesToRemove = imageCanvas.Children.OfType<Rectangle>()
                .Where(r => r != currentRectangle).ToList();
            foreach (var rect in rectanglesToRemove)
            {
                imageCanvas.Children.Remove(rect);
            }
            
            string annotationFile = GetAnnotationFilePath(imagePath);
            if (File.Exists(annotationFile))
            {
                try
                {
                    string json = File.ReadAllText(annotationFile);
                    var loadedAnnotations = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);
                    
                    foreach (var anno in loadedAnnotations)
                    {
                        annotations.Add(anno);
                        DrawRectangleForAnnotation(anno);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading annotations: {ex.Message}", "Warning", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            
            UpdateAnnotationCount();
        }

        private string GetAnnotationFilePath(string imagePath)
        {
            string fileName = IOPath.GetFileNameWithoutExtension(imagePath) + ".json";
            return IOPath.Combine(annotationsFolder, fileName);
        }

        private void DrawRectangleForAnnotation(CharacterAnnotation anno)
        {
            var rect = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0)),
                Width = anno.Width,
                Height = anno.Height
            };
            
            Canvas.SetLeft(rect, anno.X);
            Canvas.SetTop(rect, anno.Y);
            rect.Tag = anno;
            
            imageCanvas.Children.Add(rect);
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtCharLabel.Text))
            {
                txtStatus.Text = "⚠️ Please enter a character label first!";
                txtCharLabel.Focus();
                return;
            }

            startPoint = e.GetPosition(imageCanvas);
            isDrawing = true;
            
            currentRectangle = new Rectangle
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 0, 255))
            };
            
            Canvas.SetLeft(currentRectangle, startPoint.X);
            Canvas.SetTop(currentRectangle, startPoint.Y);
            imageCanvas.Children.Add(currentRectangle);
            
            imageCanvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDrawing || currentRectangle == null) return;

            Point currentPoint = e.GetPosition(imageCanvas);
            
            double x = Math.Min(startPoint.X, currentPoint.X);
            double y = Math.Min(startPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - startPoint.X);
            double height = Math.Abs(currentPoint.Y - startPoint.Y);
            
            Canvas.SetLeft(currentRectangle, x);
            Canvas.SetTop(currentRectangle, y);
            currentRectangle.Width = width;
            currentRectangle.Height = height;
            
            txtStatus.Text = $"Drawing: {width:F0} x {height:F0} pixels";
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDrawing || currentRectangle == null) return;

            isDrawing = false;
            imageCanvas.ReleaseMouseCapture();
            
            // Only create annotation if rectangle has meaningful size
            if (currentRectangle.Width > 5 && currentRectangle.Height > 5)
            {
                var annotation = new CharacterAnnotation
                {
                    Label = txtCharLabel.Text.Trim(),
                    X = Canvas.GetLeft(currentRectangle),
                    Y = Canvas.GetTop(currentRectangle),
                    Width = currentRectangle.Width,
                    Height = currentRectangle.Height,
                    ImagePath = imageFiles[currentImageIndex]
                };
                
                annotations.Add(annotation);
                
                // Change rectangle appearance to permanent
                currentRectangle.Stroke = Brushes.Red;
                currentRectangle.StrokeDashArray = null;
                currentRectangle.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0));
                currentRectangle.Tag = annotation;
                
                UpdateAnnotationCount();
                SaveAnnotationsForCurrentImage();
                
                // Clear the label for next annotation
                txtCharLabel.Clear();
                txtCharLabel.Focus();
                txtStatus.Text = $"✅ Added '{annotation.Label}' - Ready for next annotation";
            }
            else
            {
                imageCanvas.Children.Remove(currentRectangle);
                txtStatus.Text = "❌ Rectangle too small - try again";
            }
            
            currentRectangle = null;
        }

        private void SaveAnnotationsForCurrentImage()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            
            string imagePath = imageFiles[currentImageIndex];
            string annotationFile = GetAnnotationFilePath(imagePath);
            
            try
            {
                var imageAnnotations = annotations.Where(a => a.ImagePath == imagePath).ToList();
                string json = JsonConvert.SerializeObject(imageAnnotations, Formatting.Indented);
                File.WriteAllText(annotationFile, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving annotations: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAnnotations_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save current image annotations
                SaveAnnotationsForCurrentImage();

                // Create master annotation file
                string masterFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
                var allAnnotations = new List<CharacterAnnotation>();
                
                foreach (var imgPath in imageFiles)
                {
                    string annoFile = GetAnnotationFilePath(imgPath);
                    if (File.Exists(annoFile))
                    {
                        string json = File.ReadAllText(annoFile);
                        var imgAnnos = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(json);
                        allAnnotations.AddRange(imgAnnos);
                    }
                }
                
                string masterJson = JsonConvert.SerializeObject(allAnnotations, Formatting.Indented);
                File.WriteAllText(masterFile, masterJson);
                
                MessageBox.Show($"✅ Saved {allAnnotations.Count} annotations!\n\nLocation: {annotationsFolder}", 
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                
                txtStatus.Text = $"✅ Saved {allAnnotations.Count} total annotations";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving annotations: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CharacterAnnotation anno)
            {
                // Remove from list
                annotations.Remove(anno);
                
                // Remove rectangle from canvas
                var rectToRemove = imageCanvas.Children.OfType<Rectangle>()
                    .FirstOrDefault(r => r.Tag == anno);
                if (rectToRemove != null)
                {
                    imageCanvas.Children.Remove(rectToRemove);
                }
                
                UpdateAnnotationCount();
                SaveAnnotationsForCurrentImage();
                txtStatus.Text = $"🗑️ Deleted annotation for '{anno.Label}'";
            }
        }

        private void Annotations_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstAnnotations.SelectedItem is CharacterAnnotation anno)
            {
                // Highlight the selected rectangle
                var allRects = imageCanvas.Children.OfType<Rectangle>();
                foreach (var rect in allRects)
                {
                    if (rect.Tag == anno)
                    {
                        rect.StrokeThickness = 4;
                    }
                    else if (rect.Tag is CharacterAnnotation)
                    {
                        rect.StrokeThickness = 2;
                    }
                }
            }
        }

        private void CharLabel_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(txtCharLabel.Text))
            {
                txtStatus.Text = $"✏️ Draw a rectangle for character '{txtCharLabel.Text}'";
            }
        }

        private void PreviousImage_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex > 0)
            {
                SaveAnnotationsForCurrentImage();
                LoadImage(currentImageIndex - 1);
            }
        }

        private void NextImage_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex < imageFiles.Count - 1)
            {
                SaveAnnotationsForCurrentImage();
                LoadImage(currentImageIndex + 1);
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            zoomLevel *= 1.2;
            ApplyZoom();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            zoomLevel /= 1.2;
            ApplyZoom();
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            zoomLevel = 1.0;
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            var scaleTransform = new ScaleTransform(zoomLevel, zoomLevel);
            imageCanvas.LayoutTransform = scaleTransform;
            txtStatus.Text = $"🔍 Zoom: {zoomLevel:P0}";
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear all annotations for this image?",
                "Clear Annotations",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
                
            if (result == MessageBoxResult.Yes)
            {
                annotations.Clear();
                
                var rectanglesToRemove = imageCanvas.Children.OfType<Rectangle>()
                    .Where(r => r.Tag is CharacterAnnotation).ToList();
                foreach (var rect in rectanglesToRemove)
                {
                    imageCanvas.Children.Remove(rect);
                }
                
                UpdateAnnotationCount();
                SaveAnnotationsForCurrentImage();
                txtStatus.Text = "🗑️ All annotations cleared";
            }
        }

        private void UpdateAnnotationCount()
        {
            txtAnnotationCount.Text = $"Annotations: {annotations.Count}";
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveAnnotationsForCurrentImage();
            base.OnClosing(e);
        }
    }
}