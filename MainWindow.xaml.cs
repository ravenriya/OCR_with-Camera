using Microsoft.Win32;
using Newtonsoft.Json;
//using System;
//using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
//using System.Linq;
//using System.Threading.Tasks;
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
        private string currentUser;
        private DispatcherTimer authTimer;
        private const int AUTH_TIMEOUT_MINUTES = 20;
        private bool isTrainingMode = false;

        private string currentProjectName = "DefaultProject";
        private string annotationsFolder;
        private string modelsFolder;
        private string currentProjectFolder;
        private const string PROJECTS_BASE_DIR = "Projects";

        private List<AnnotationData> currentImageAnnotations = new List<AnnotationData>();

        // ── Template cyan box ────────────────────────────────────────────
        private double templateWidth = 60;
        private double templateHeight = 80;
        private double tplLeft = 10, tplTop = 40;
        private Rectangle tplRect;
        private TextBlock tplLabel;

        private bool isDraggingTpl = false;
        private bool isResizingTpl = false;
        private string resizeDir = "";
        private Point dragStart;
        private double dragStartW, dragStartH, dragStartL, dragStartT;

        // ── Stamp preview ─────────────────────────────────────────────────
        private bool isStamping = false;
        private Rectangle stampPreview = null;

        // ── Annotation move / rotate state ───────────────────────────────
        private AnnotationData rotatingAnn = null;
        private Point rotDragStart;
        private double rotStartAngle;
        private AnnotationData movingAnn = null;
        private double moveStartLeft, moveStartTop;

        public MainWindow() : this("Guest") { }
        public MainWindow(string username)
        {
            InitializeComponent();
            currentUser = username;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(IOPath.Combine(baseDir, PROJECTS_BASE_DIR));
            authTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(AUTH_TIMEOUT_MINUTES) };
            authTimer.Tick += AuthTimer_Tick;
            authTimer.Start();
            MessageBox.Show($"Welcome {currentUser}!\n\nCurrent Project: {currentProjectName}", "PixTech");
            SetupProjectFolders();
            if (txtStatusBar != null) txtStatusBar.Text = $"User: {currentUser} | Project: {currentProjectName}";
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

        // ════════════════════════════════════════════════════════════════
        // HELPER: Get trained model directory path
        // ════════════════════════════════════════════════════════════════
        private string GetTrainedModelDir()
        {
            return IOPath.Combine(currentProjectFolder, "TrainedModel");
        }

        private bool HasTrainedModel()
        {
            string md = GetTrainedModelDir();
            return Directory.Exists(md) && File.Exists(IOPath.Combine(md, "model_config.json"));
        }

        /// <summary>
        /// Deletes the trained model directory so inference goes back to pure EasyOCR.
        /// </summary>
        private void ClearTrainedModel()
        {
            string md = GetTrainedModelDir();
            if (Directory.Exists(md))
            {
                try
                {
                    Directory.Delete(md, true);
                    Debug.WriteLine("Trained model cleared.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error clearing model: {ex.Message}");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // MODE SWITCH
        // ════════════════════════════════════════════════════════════════
        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (rbTrainingMode == null || rbInferenceMode == null) return;
            isTrainingMode = rbTrainingMode.IsChecked == true;
            if (isTrainingMode)
            {
                txtMode.Text = "TRAINING MODE";
                txtMode.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
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
                    EnsureTemplate();
                }
                UpdateAnnotationStatus();
                if (CheckAnnotationsExist()) btnStartTraining.IsEnabled = true;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Training | Project: {currentProjectName}";
            }
            else
            {
                txtMode.Text = "INFERENCE MODE";
                txtMode.Foreground = new SolidColorBrush(Color.FromRgb(184, 134, 11));
                pnlInference.Visibility = Visibility.Visible;
                pnlTraining.Visibility = Visibility.Collapsed;
                annotationCanvas.Visibility = Visibility.Collapsed;
                annotationCanvas.Children.Clear();
                tplRect = null; tplLabel = null;
                btnAnnotate.IsEnabled = btnStartTraining.IsEnabled = false;
                // Show whether model is loaded
                txtStatusBar.Text = HasTrainedModel()
                    ? $"User: {currentUser} | Mode: Inference (Model Loaded) | Project: {currentProjectName}"
                    : $"User: {currentUser} | Mode: Inference (No Model) | Project: {currentProjectName}";
            }
        }

        // ════════════════════════════════════════════════════════════════
        // TEMPLATE
        // ════════════════════════════════════════════════════════════════
        private bool IsTemplateElement(object el) => el == tplRect || el == tplLabel;

        private void EnsureTemplate()
        {
            if (tplRect != null) return;
            if (annotationCanvas.ActualWidth > 0)
            {
                tplLeft = (annotationCanvas.ActualWidth - templateWidth) / 2;
                tplTop = (annotationCanvas.ActualHeight - templateHeight) / 2;
            }
            DrawTemplate();
        }

        private void RemoveTemplateVisuals()
        {
            if (tplRect != null) annotationCanvas.Children.Remove(tplRect);
            if (tplLabel != null) annotationCanvas.Children.Remove(tplLabel);
            tplRect = null; tplLabel = null;
        }

        private const double EDGE_THRESH = 10;

        private void DrawTemplate()
        {
            RemoveTemplateVisuals();
            tplRect = new Rectangle
            {
                Width = templateWidth,
                Height = templateHeight,
                Stroke = Brushes.Cyan,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(15, 0, 255, 255)),
                Tag = "TPL",
            };
            PutOnCanvas(tplRect, tplLeft, tplTop, 900);
            tplRect.MouseLeftButtonDown += TplRect_MouseDown;
            tplRect.MouseMove += TplRect_MouseMove_Cursor;
            tplRect.MouseLeftButtonUp += TplAny_Up;

            tplLabel = new TextBlock
            {
                Text = $"{(int)templateWidth}×{(int)templateHeight}",
                Foreground = Brushes.Cyan,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
            };
            PutOnCanvas(tplLabel, tplLeft + 2, tplTop + 2, 901);
        }

        private void TplRect_MouseMove_Cursor(object sender, MouseEventArgs e)
        {
            if (isDraggingTpl || isResizingTpl) return;
            var p = e.GetPosition(tplRect);
            double W = tplRect.ActualWidth, H = tplRect.ActualHeight;
            bool onRight = p.X >= W - EDGE_THRESH, onBottom = p.Y >= H - EDGE_THRESH;
            bool onLeft = p.X <= EDGE_THRESH, onTop = p.Y <= EDGE_THRESH;
            if ((onRight && onBottom) || (onLeft && onTop)) tplRect.Cursor = Cursors.SizeNWSE;
            else if ((onRight && onTop) || (onLeft && onBottom)) tplRect.Cursor = Cursors.SizeNESW;
            else if (onRight || onLeft) tplRect.Cursor = Cursors.SizeWE;
            else if (onBottom || onTop) tplRect.Cursor = Cursors.SizeNS;
            else tplRect.Cursor = Cursors.SizeAll;
        }

        private void TplRect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(tplRect);
            double W = tplRect.ActualWidth, H = tplRect.ActualHeight;
            bool onRight = p.X >= W - EDGE_THRESH, onBottom = p.Y >= H - EDGE_THRESH;
            bool onLeft = p.X <= EDGE_THRESH, onTop = p.Y <= EDGE_THRESH;
            dragStart = e.GetPosition(annotationCanvas);
            dragStartW = templateWidth; dragStartH = templateHeight;
            dragStartL = tplLeft; dragStartT = tplTop;
            if (onRight || onBottom || onLeft || onTop)
            {
                isResizingTpl = true;
                resizeDir = $"{(onRight ? "R" : "")}{(onLeft ? "L" : "")}{(onBottom ? "B" : "")}{(onTop ? "T" : "")}";
            }
            else isDraggingTpl = true;
            tplRect.CaptureMouse(); e.Handled = true;
        }

        private void PutOnCanvas(UIElement el, double l, double t, int z)
        {
            Canvas.SetLeft(el, l); Canvas.SetTop(el, t); Panel.SetZIndex(el, z);
            if (!annotationCanvas.Children.Contains(el)) annotationCanvas.Children.Add(el);
        }

        private void UpdateTemplate()
        {
            if (tplRect == null) return;
            Canvas.SetLeft(tplRect, tplLeft); Canvas.SetTop(tplRect, tplTop);
            tplRect.Width = templateWidth; tplRect.Height = templateHeight;
            Canvas.SetLeft(tplLabel, tplLeft + 2); Canvas.SetTop(tplLabel, tplTop + 2);
            tplLabel.Text = $"{(int)templateWidth}×{(int)templateHeight}";
        }

        private void TplAny_Up(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = isResizingTpl = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            Mouse.Capture(null); e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════════
        // CANVAS MOUSE — stamp new annotations
        // ════════════════════════════════════════════════════════════════
        private void AnnotationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!isTrainingMode || e.ChangedButton != MouseButton.Left) return;
            var src = e.OriginalSource as UIElement;
            if (src != null && IsTemplateElement(src)) return;
            if (src != null && IsChildOfAnnotation(src)) return;

            isStamping = true;
            var pt = e.GetPosition(annotationCanvas);
            stampPreview = new Rectangle
            {
                Width = templateWidth,
                Height = templateHeight,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(20, 0, 255, 0)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(stampPreview, pt.X - templateWidth / 2);
            Canvas.SetTop(stampPreview, pt.Y - templateHeight / 2);
            Panel.SetZIndex(stampPreview, 500);
            annotationCanvas.Children.Add(stampPreview);
            annotationCanvas.CaptureMouse();
            e.Handled = true;
        }

        private bool IsChildOfAnnotation(UIElement el)
        {
            DependencyObject cur = el;
            while (cur != null)
            {
                if (cur is Canvas c && currentImageAnnotations.Any(a => a.VisualContainer == c)) return true;
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isTrainingMode) return;
            var pos = e.GetPosition(annotationCanvas);

            if (isDraggingTpl)
            {
                tplLeft = dragStartL + (pos.X - dragStart.X);
                tplTop = dragStartT + (pos.Y - dragStart.Y);
                UpdateTemplate(); return;
            }
            if (isResizingTpl)
            {
                double dx = pos.X - dragStart.X, dy = pos.Y - dragStart.Y;
                if (resizeDir.Contains("R")) templateWidth = Math.Max(10, dragStartW + dx);
                if (resizeDir.Contains("L")) { templateWidth = Math.Max(10, dragStartW - dx); tplLeft = dragStartL + dx; }
                if (resizeDir.Contains("B")) templateHeight = Math.Max(10, dragStartH + dy);
                if (resizeDir.Contains("T")) { templateHeight = Math.Max(10, dragStartH - dy); tplTop = dragStartT + dy; }
                UpdateTemplate(); return;
            }
            if (isStamping && stampPreview != null)
            {
                Canvas.SetLeft(stampPreview, pos.X - templateWidth / 2);
                Canvas.SetTop(stampPreview, pos.Y - templateHeight / 2);
                return;
            }
            if (movingAnn != null)
            {
                double nl = moveStartLeft + (pos.X - rotDragStart.X);
                double nt = moveStartTop + (pos.Y - rotDragStart.Y);
                Canvas.SetLeft(movingAnn.VisualContainer, nl);
                Canvas.SetTop(movingAnn.VisualContainer, nt);
                movingAnn.Bounds = new Rect(nl, nt, movingAnn.Bounds.Width, movingAnn.Bounds.Height);
                return;
            }
            if (rotatingAnn != null)
            {
                double cx = Canvas.GetLeft(rotatingAnn.VisualContainer) + rotatingAnn.Bounds.Width / 2;
                double cy = Canvas.GetTop(rotatingAnn.VisualContainer) + rotatingAnn.Bounds.Height / 2;
                double a0 = Math.Atan2(rotDragStart.Y - cy, rotDragStart.X - cx);
                double a1 = Math.Atan2(pos.Y - cy, pos.X - cx);
                double newAngle = (rotStartAngle + (a1 - a0) * 180.0 / Math.PI + 360) % 360;
                rotatingAnn.Angle = newAngle;
                ((RotateTransform)rotatingAnn.VisualContainer.RenderTransform).Angle = newAngle;
            }
        }

        private void AnnotationCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = isResizingTpl = false;
            tplRect?.ReleaseMouseCapture();
            rotatingAnn = null; movingAnn = null;

            if (isStamping)
            {
                isStamping = false;
                annotationCanvas.ReleaseMouseCapture();
                Mouse.Capture(null);
                if (stampPreview != null)
                {
                    var dropPos = e.GetPosition(annotationCanvas);
                    annotationCanvas.Children.Remove(stampPreview);
                    stampPreview = null;
                    StampAnnotation(dropPos);
                }
                return;
            }
            annotationCanvas.ReleaseMouseCapture();
            Mouse.Capture(null);
        }

        // ════════════════════════════════════════════════════════════════
        // ANNOTATION
        // ════════════════════════════════════════════════════════════════
        private void StampAnnotation(Point center)
        {
            double x = center.X - templateWidth / 2.0;
            double y = center.Y - templateHeight / 2.0;
            CreateAnnotationVisual(x, y, templateWidth, templateHeight, 0, "", addToList: false, needsLabel: true);
        }

        private AnnotationData CreateAnnotationVisual(double x, double y, double w, double h,
                                                       double angle, string existingLabel,
                                                       bool addToList, bool needsLabel)
        {
            var container = new Canvas
            {
                Width = w,
                Height = h,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(angle),
                Cursor = Cursors.Hand,
                ClipToBounds = false,
            };

            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)),
            };
            Canvas.SetLeft(rect, 0); Canvas.SetTop(rect, 0);
            container.Children.Add(rect);

            var lbl = new TextBlock
            {
                Text = existingLabel,
                Foreground = Brushes.Lime,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                Padding = new Thickness(2, 1, 2, 1),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, 2);
            container.Children.Add(lbl);

            Canvas.SetLeft(container, x); Canvas.SetTop(container, y);
            Panel.SetZIndex(container, 100);
            annotationCanvas.Children.Add(container);

            var ann = new AnnotationData
            {
                Label = existingLabel,
                Bounds = new Rect(x, y, w, h),
                Angle = angle,
                VisualRect = rect,
                VisualContainer = container,
                LabelText = lbl,
            };

            if (addToList) currentImageAnnotations.Add(ann);
            WireContainer(container, ann);
            if (needsLabel) ShowInlineTextBox(ann);
            return ann;
        }

        private void WireContainer(Canvas container, AnnotationData ann)
        {
            container.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.OriginalSource is TextBox) return;
                var cp = ev.GetPosition(container);
                const double mg = 14;
                bool edge = cp.X < mg || cp.X > ann.Bounds.Width - mg
                         || cp.Y < mg || cp.Y > ann.Bounds.Height - mg;
                rotDragStart = ev.GetPosition(annotationCanvas);
                if (edge) { rotatingAnn = ann; rotStartAngle = ann.Angle; }
                else
                {
                    movingAnn = ann;
                    moveStartLeft = Canvas.GetLeft(container);
                    moveStartTop = Canvas.GetTop(container);
                }
                container.CaptureMouse(); ev.Handled = true;
            };
            container.MouseLeftButtonUp += (s, ev) =>
            {
                container.ReleaseMouseCapture();
                rotatingAnn = null; movingAnn = null;
                SaveCurrentImageAnnotations(); ev.Handled = true;
            };
            container.MouseMove += (s, ev) =>
            {
                if (rotatingAnn != null || movingAnn != null) return;
                var cp = ev.GetPosition(container);
                const double mg = 14;
                bool edge = cp.X < mg || cp.X > ann.Bounds.Width - mg
                         || cp.Y < mg || cp.Y > ann.Bounds.Height - mg;
                container.Cursor = edge ? Cursors.SizeAll : Cursors.Hand;
            };
            container.MouseRightButtonUp += (s, ev) =>
            {
                ShowAnnotationContextMenu(ann); ev.Handled = true;
            };
        }

        private void ShowInlineTextBox(AnnotationData ann)
        {
            if (ann.InlineTextBox != null)
            {
                ann.VisualContainer.Children.Remove(ann.InlineTextBox);
                ann.InlineTextBox = null;
            }

            double tbW = Math.Min(ann.Bounds.Width - 4, 28);
            double tbH = Math.Min(ann.Bounds.Height - 4, 22);

            var tb = new TextBox
            {
                Width = tbW,
                Height = tbH,
                MaxLength = 1,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.DarkGray,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                IsHitTestVisible = true,
            };

            Canvas.SetLeft(tb, 2);
            Canvas.SetTop(tb, 2);
            Panel.SetZIndex(tb, 200);
            ann.VisualContainer.Children.Add(tb);
            ann.InlineTextBox = tb;

            Dispatcher.InvokeAsync(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);

            void Confirm()
            {
                if (!ann.VisualContainer.Children.Contains(tb)) return;
                ann.VisualContainer.Children.Remove(tb);
                ann.InlineTextBox = null;
                string label = tb.Text.Trim().ToUpper();
                if (string.IsNullOrEmpty(label))
                {
                    annotationCanvas.Children.Remove(ann.VisualContainer);
                    currentImageAnnotations.Remove(ann);
                    return;
                }
                ann.LabelText.Text = ann.Label = label;
                if (!currentImageAnnotations.Contains(ann))
                    currentImageAnnotations.Add(ann);
                SaveCurrentImageAnnotations();
                UpdateAnnotationStatus();
            }

            tb.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter || ev.Key == Key.Return) { Confirm(); ev.Handled = true; }
                else if (ev.Key == Key.Escape)
                {
                    ann.VisualContainer.Children.Remove(tb);
                    ann.InlineTextBox = null;
                    annotationCanvas.Children.Remove(ann.VisualContainer);
                    currentImageAnnotations.Remove(ann);
                    ev.Handled = true;
                }
            };
            tb.LostFocus += (s, _) =>
            {
                if (ann.VisualContainer.Children.Contains(tb)) Confirm();
            };
        }

        // ════════════════════════════════════════════════════════════════
        // RIGHT-CLICK MENU
        // ════════════════════════════════════════════════════════════════
        private void ShowAnnotationContextMenu(AnnotationData ann)
        {
            var menu = new ContextMenu();

            var edit = new MenuItem { Header = "✏️  Edit Label" };
            edit.Click += (s, _) => ShowInlineTextBox(ann);

            var del = new MenuItem { Header = "🗑️  Delete this box" };
            del.Click += (s, _) =>
            {
                annotationCanvas.Children.Remove(ann.VisualContainer);
                currentImageAnnotations.Remove(ann);
                SaveCurrentImageAnnotations();
                UpdateMasterAnnotationFile();
                UpdateAnnotationStatus();
            };

            menu.Items.Add(edit);
            menu.Items.Add(del);
            menu.IsOpen = true;
        }

        private void AutoDetectCharacters_Click(object sender, RoutedEventArgs e) { }

        // ════════════════════════════════════════════════════════════════
        // SAVE / LOAD ANNOTATIONS
        // ════════════════════════════════════════════════════════════════
        private void SaveCurrentImageAnnotations()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            string imgPath = imageFiles[currentImageIndex];
            string file = GetAnnotationFilePath(imgPath);
            try
            {
                var src = imgDisplay.Source as BitmapSource;
                if (currentImageAnnotations.Count == 0 || src == null)
                {
                    if (File.Exists(file)) File.Delete(file);
                    return;
                }
                double sx = src.PixelWidth / annotationCanvas.ActualWidth;
                double sy = src.PixelHeight / annotationCanvas.ActualHeight;
                var list = currentImageAnnotations.Select(a =>
                {
                    double ax = Canvas.GetLeft(a.VisualContainer);
                    double ay = Canvas.GetTop(a.VisualContainer);
                    return new CharacterAnnotation
                    {
                        Label = a.Label,
                        Angle = a.Angle,
                        ImagePath = imgPath,
                        X = ax * sx,
                        Y = ay * sy,
                        Width = a.Bounds.Width * sx,
                        Height = a.Bounds.Height * sy,
                    };
                }).ToList();
                File.WriteAllText(file, JsonConvert.SerializeObject(list, Formatting.Indented));
                UpdateMasterAnnotationFile();
            }
            catch (Exception ex) { Debug.WriteLine($"Save: {ex.Message}"); }
        }

        private void LoadAnnotationsForCurrentImage()
        {
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();

            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count || imgDisplay.Source == null) return;
            string file = GetAnnotationFilePath(imageFiles[currentImageIndex]);
            if (!File.Exists(file)) return;

            try
            {
                var list = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(file));
                if (list == null) return;
                var src = imgDisplay.Source as BitmapSource; if (src == null) return;
                double sx = annotationCanvas.ActualWidth / src.PixelWidth;
                double sy = annotationCanvas.ActualHeight / src.PixelHeight;
                foreach (var a in list)
                    CreateAnnotationVisual(a.X * sx, a.Y * sy, a.Width * sx, a.Height * sy,
                                           a.Angle, a.Label, addToList: true, needsLabel: false);
            }
            catch (Exception ex) { Debug.WriteLine($"Load: {ex.Message}"); }

            EnsureTemplate();
        }

        private void UpdateMasterAnnotationFile()
        {
            try
            {
                var all = new List<CharacterAnnotation>();
                foreach (var p in imageFiles)
                {
                    string f = GetAnnotationFilePath(p);
                    if (!File.Exists(f)) continue;
                    var l = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(f));
                    if (l != null) all.AddRange(l);
                }
                string master = IOPath.Combine(annotationsFolder, "all_annotations.json");
                if (all.Count > 0) File.WriteAllText(master, JsonConvert.SerializeObject(all, Formatting.Indented));
                else if (File.Exists(master)) File.Delete(master);
                UpdateAnnotationStatus();
            }
            catch { }
        }

        private string GetAnnotationFilePath(string imgPath)
            => IOPath.Combine(annotationsFolder, IOPath.GetFileNameWithoutExtension(imgPath) + ".json");

        // ════════════════════════════════════════════════════════════════
        // IMAGE DISPLAY
        // ════════════════════════════════════════════════════════════════
        private void UpdateCanvasSize()
        {
            if (imgDisplay.Source != null && imgDisplay.ActualWidth > 0)
            {
                annotationCanvas.Width = imgDisplay.ActualWidth;
                annotationCanvas.Height = imgDisplay.ActualHeight;
            }
        }

        private void ShowImage(string path)
        {
            try
            {
                if (isTrainingMode && currentImageAnnotations.Count > 0) SaveCurrentImageAnnotations();

                foreach (var ann in currentImageAnnotations.ToList())
                {
                    if (ann.VisualContainer != null)
                        annotationCanvas.Children.Remove(ann.VisualContainer);
                }
                currentImageAnnotations.Clear();

                foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                {
                    if (!IsTemplateElement(el))
                        annotationCanvas.Children.Remove(el);
                }

                txtPlaceholder.Visibility = Visibility.Collapsed;

                if (imgDisplay.Source is BitmapImage old) { imgDisplay.Source = null; old.StreamSource?.Dispose(); GC.Collect(); }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit(); bmp.Freeze();
                imgDisplay.Source = bmp;
                imgDisplay.Visibility = Visibility.Visible;

                Dispatcher.InvokeAsync(() =>
                {
                    imgDisplay.UpdateLayout(); UpdateCanvasSize();
                    if (isTrainingMode)
                    {
                        annotationCanvas.Visibility = Visibility.Visible;

                        foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                        {
                            if (!IsTemplateElement(el))
                                annotationCanvas.Children.Remove(el);
                        }
                        currentImageAnnotations.Clear();

                        LoadAnnotationsForCurrentImage();
                        tplLeft = (annotationCanvas.ActualWidth - templateWidth) / 2;
                        tplTop = (annotationCanvas.ActualHeight - templateHeight) / 2;
                        RemoveTemplateVisuals();
                        DrawTemplate();
                    }
                    else
                    {
                        annotationCanvas.Visibility = Visibility.Collapsed;
                        foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                            annotationCanvas.Children.Remove(el);
                        tplRect = null; tplLabel = null;
                    }
                }, DispatcherPriority.Loaded);
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }
        private void UpdateImageList()
        {
            lstImages.Items.Clear();
            foreach (var path in imageFiles)
            {
                var item = new ListBoxItem { Content = IOPath.GetFileName(path), Tag = path };
                var menu = new ContextMenu();
                var removeItem = new MenuItem { Header = "❌ Remove from list" };
                string capturedPath = path;
                removeItem.Click += (s, e) =>
                {
                    imageFiles.Remove(capturedPath);
                    UpdateImageList();
                    if (imageFiles.Count > 0)
                    {
                        currentImageIndex = Math.Min(currentImageIndex, imageFiles.Count - 1);
                        ShowImage(imageFiles[currentImageIndex]);
                        lstImages.SelectedIndex = currentImageIndex;
                    }
                    else
                    {
                        imgDisplay.Source = null;
                        foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                            if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
                        currentImageAnnotations.Clear();
                        txtPlaceholder.Visibility = Visibility.Visible;
                    }
                    txtImageCount.Text = $"Images: {imageFiles.Count}";
                };
                menu.Items.Add(removeItem);
                item.ContextMenu = menu;
                lstImages.Items.Add(item);
            }
            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        // ════════════════════════════════════════════════════════════════
        // ANNOTATION STATUS + CLEAR
        // ════════════════════════════════════════════════════════════════
        private void UpdateAnnotationStatus()
        {
            string master = IOPath.Combine(annotationsFolder, "all_annotations.json");
            if (!File.Exists(master))
            {
                txtAnnotationStatus.Text = " No annotations yet";
                txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                btnStartTraining.IsEnabled = false;
                if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
                return;
            }
            try
            {
                var a = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(master));
                if (a != null && a.Count > 0)
                {
                    txtAnnotationStatus.Text = $" {a.Count} annotations\n{a.Select(x => x.ImagePath).Distinct().Count()} images | {a.Select(x => x.Label).Distinct().Count()} unique chars";
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
            catch { txtAnnotationStatus.Text = " Error reading annotations"; }
        }

        private bool CheckAnnotationsExist() =>
            File.Exists(IOPath.Combine(annotationsFolder, "all_annotations.json"));

        private void Annotate_Click(object sender, RoutedEventArgs e) { }
        private void CheckAnnotations_Click(object sender, RoutedEventArgs e) => UpdateAnnotationStatus();

        private void ClearCurrentImageAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            if (MessageBox.Show($"Clear annotations for {IOPath.GetFileName(imageFiles[currentImageIndex])}?",
                "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var ann in currentImageAnnotations.ToList())
                annotationCanvas.Children.Remove(ann.VisualContainer);
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();
            string f = GetAnnotationFilePath(imageFiles[currentImageIndex]);
            if (File.Exists(f)) File.Delete(f);
            UpdateMasterAnnotationFile(); UpdateAnnotationStatus();
        }

        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Delete ALL annotations from ALL images?\n\nThis will also clear the trained model.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            // Clear annotation files
            if (Directory.Exists(annotationsFolder))
                foreach (var f in Directory.GetFiles(annotationsFolder, "*.json")) File.Delete(f);
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();

            // ALSO clear the trained model so it doesn't keep applying old corrections
            ClearTrainedModel();

            UpdateAnnotationStatus();
            MessageBox.Show("Annotations and trained model cleared.", "Done");
        }

        private void SeparateAnnotations_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string master = IOPath.Combine(annotationsFolder, "all_annotations.json");
                if (!File.Exists(master)) return;
                var all = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(master));
                if (all == null) return;
                foreach (var grp in all.GroupBy(a => a.ImagePath))
                    File.WriteAllText(GetAnnotationFilePath(grp.Key),
                        JsonConvert.SerializeObject(grp.ToList(), Formatting.Indented));
                if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count)
                    LoadAnnotationsForCurrentImage();
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════
        // RESET - Clear trained model (inference goes back to pure EasyOCR)
        // ════════════════════════════════════════════════════════════════
        private void ResetModel_Click(object sender, RoutedEventArgs e)
        {
            if (!HasTrainedModel())
            {
                MessageBox.Show("No trained model to reset.", "Reset");
                return;
            }
            if (MessageBox.Show("Reset trained model?\n\nInference will go back to pure EasyOCR.\nAnnotations will be kept.",
                "Reset Model", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            ClearTrainedModel();
            txtStatus.Text = "EasyOCR";
            txtStatusBar.Text = $"User: {currentUser} | Mode: Inference (No Model) | Project: {currentProjectName}";
            MessageBox.Show("Model cleared. Inference will now use pure EasyOCR.", "Reset");
        }

        // ════════════════════════════════════════════════════════════════
        // TRAINING
        // ════════════════════════════════════════════════════════════════
        private async void StartTraining_Click(object sender, RoutedEventArgs e)
        {
            string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string trainScript = @"C:\Users\Pixtech Workstation\source\repos\PixtechApplication\PythonScripts\train_last_layer.py";
            string annoFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            string modelDir = GetTrainedModelDir();
            int epochs = 50, batch = 32;
            if (txtEpochs != null && int.TryParse(txtEpochs.Text, out int e2)) epochs = e2;
            if (txtBatchSize != null && int.TryParse(txtBatchSize.Text, out int b2)) batch = b2;
            if (!File.Exists(trainScript)) { MessageBox.Show("train_last_layer.py not found"); return; }
            if (!File.Exists(annoFile)) { MessageBox.Show("No annotations found!"); return; }

            // Clear old model before retraining
            ClearTrainedModel();

            btnStartTraining.IsEnabled = false;
            if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
            progressBarTraining.Value = 0; txtTrainingStatus.Text = "Starting...";
            var errs = new System.Text.StringBuilder();
            try
            {
                await Task.Run(() =>
                {
                    var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = python,
                            Arguments = $"\"{trainScript}\" \"{annoFile}\" \"{modelDir}\" --epochs {epochs} --batch {batch} --augments 80",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = baseDir,
                        }
                    };
                    proc.OutputDataReceived += (s, a) =>
                    {
                        if (string.IsNullOrEmpty(a.Data)) return;
                        Dispatcher.Invoke(() =>
                        {
                            txtTrainingStatus.Text = a.Data;
                            if (a.Data.Contains("Epoch"))
                            {
                                var parts = a.Data.Split('/');
                                if (parts.Length >= 2)
                                {
                                    int cur = int.TryParse(parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Last(), out int c) ? c : 0;
                                    int tot = int.TryParse(parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).First(), out int t) ? t : epochs;
                                    progressBarTraining.Value = cur * 100.0 / tot;
                                }
                            }
                            if (a.Data.StartsWith("TRAINING_COMPLETE:"))
                            {
                                double.TryParse(a.Data.Split(':')[1],
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double acc);
                                progressBarTraining.Value = 100;
                                txtTrainingStatus.Text = $"Done! Val: {acc:F1}%";
                                MessageBox.Show($"Training complete!\nVal accuracy: {acc:F1}%\n\nSwitch to Inference Mode to test.", "Done");
                            }
                        });
                    };
                    proc.ErrorDataReceived += (s, a) => { if (!string.IsNullOrEmpty(a.Data)) errs.AppendLine(a.Data); };
                    proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); proc.WaitForExit();
                    if (proc.ExitCode != 0)
                        Dispatcher.Invoke(() => { txtTrainingStatus.Text = "Failed!"; MessageBox.Show($"Error:\n{errs}", "Training Error"); });
                });
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            finally
            {
                btnStartTraining.IsEnabled = true;
                if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = true;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // INFERENCE
        // ════════════════════════════════════════════════════════════════
        private async void RunInference_Click(object sender, RoutedEventArgs e)
        {
            if (imageFiles.Count == 0) { MessageBox.Show("Load images first!"); return; }
            string imgPath = (lstImages.SelectedItem is ListBoxItem li && li.Tag is string p) ? p : imageFiles[currentImageIndex];
            string modelDir = GetTrainedModelDir();
            string mdArg = Directory.Exists(modelDir) && File.Exists(IOPath.Combine(modelDir, "model_config.json"))
                ? $"\"{modelDir}\""
                : "\"\"";
            btnRunInference.IsEnabled = false;
            progressBar.IsIndeterminate = true;
            txtResult.Text = "Running OCR...";
            txtStatus.Text = HasTrainedModel() ? "EasyOCR + Corrections" : "EasyOCR";
            try
            {
                string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string script = @"C:\Users\Pixtech Workstation\source\repos\PixtechApplication\PythonScripts\final_ocr.py";
                var result = await Task.Run(() =>
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = python,
                        Arguments = $"\"{script}\" \"{imgPath}\" {mdArg} output",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    });
                    proc.StandardOutput.ReadToEnd();
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) return new SiameseOCRResult { Success = false, Error = err };
                    string rf = IOPath.Combine(baseDir, "output", "result.json");
                    if (!File.Exists(rf)) return new SiameseOCRResult { Success = false, Error = "No result.json" };
                    var r = JsonConvert.DeserializeObject<SiameseOCRResult>(File.ReadAllText(rf));
                    r.Success = true; return r;
                });
                if (result.Success)
                {
                    txtResult.Text = result.Text;
                    txtConfidence.Text = $"Confidence: {result.Confidence:F1}%";

                    // Show method used
                    string method = HasTrainedModel() ? "EasyOCR + Corrections" : "EasyOCR";
                    if (result.CorrectionsApplied > 0)
                        method += $" ({result.CorrectionsApplied} fixes)";
                    txtStatus.Text = method;

                    if (result.Confidence >= 80) { txtDecision.Text = "HIGH CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Green); }
                    else if (result.Confidence >= 60) { txtDecision.Text = "MEDIUM CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Orange); }
                    else { txtDecision.Text = "LOW CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Red); }
                }
                else { txtResult.Text = $"Error: {result.Error}"; txtStatus.Text = "Failed!"; }
            }
            catch (Exception ex) { txtResult.Text = $"Exception: {ex.Message}"; }
            finally { btnRunInference.IsEnabled = true; progressBar.IsIndeterminate = false; progressBar.Value = 0; }
        }

        // ════════════════════════════════════════════════════════════════
        // MISC
        // ════════════════════════════════════════════════════════════════
        private void AuthTimer_Tick(object sender, EventArgs e)
        {
            authTimer.Stop();
            var w = new ReAuthWindow(currentUser);
            if (w.ShowDialog() == true && w.IsAuthenticated) authTimer.Start();
            else Application.Current.Shutdown();
        }

        private void LoadFiles_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
            if (d.ShowDialog() != true) return;
            foreach (var f in d.FileNames) if (!imageFiles.Contains(f)) imageFiles.Add(f);
            UpdateImageList();
            if (imageFiles.Count > 0) { currentImageIndex = 0; ShowImage(imageFiles[0]); lstImages.SelectedIndex = 0; }
            if (isTrainingMode) btnAnnotate.IsEnabled = true;
        }

        private void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Pick ANY image in the folder — ALL images in that folder will be loaded",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true) return;

            string folder = IOPath.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(folder)) return;

            string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif" };
            var files = Directory.GetFiles(folder)
                .Where(f => extensions.Contains(IOPath.GetExtension(f).ToLower()))
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
            {
                MessageBox.Show("No image files found in the folder.", "No Images");
                return;
            }

            imageFiles.Clear();
            imageFiles.AddRange(files);

            UpdateImageList();
            currentImageIndex = 0;
            ShowImage(imageFiles[0]);
            lstImages.SelectedIndex = 0;

            if (isTrainingMode) btnAnnotate.IsEnabled = true;

            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem li && li.Tag is string path)
            { currentImageIndex = imageFiles.IndexOf(path); ShowImage(path); }
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            string md = GetTrainedModelDir();
            if (!HasTrainedModel()) { MessageBox.Show("No trained model found!"); return; }
            var d = new SaveFileDialog { Filter = "Zip Archive|*.zip", FileName = $"{currentProjectName}_model.zip" };
            if (d.ShowDialog() == true)
            {
                try
                {
                    if (File.Exists(d.FileName)) File.Delete(d.FileName);
                    System.IO.Compression.ZipFile.CreateFromDirectory(md, d.FileName);
                    MessageBox.Show("Model saved!", "Success");
                }
                catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            }
        }

        private void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Zip Archive|*.zip|PyTorch Model|*.pth" };
            if (d.ShowDialog() == true)
            {
                try
                {
                    string md = GetTrainedModelDir();
                    if (d.FileName.EndsWith(".zip"))
                    {
                        // Clear and extract
                        if (Directory.Exists(md)) Directory.Delete(md, true);
                        System.IO.Compression.ZipFile.ExtractToDirectory(d.FileName, md);
                        MessageBox.Show("Model loaded!", "Success");
                    }
                    else
                    {
                        // Legacy single .pth file
                        Directory.CreateDirectory(md);
                        File.Copy(d.FileName, IOPath.Combine(md, IOPath.GetFileName(d.FileName)), true);
                        MessageBox.Show("Model file copied. Note: also needs model_config.json to work.", "Loaded");
                    }
                }
                catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            }
        }

        private void Accept_Click(object sender, RoutedEventArgs e) { }
        private void Reject_Click(object sender, RoutedEventArgs e) { }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (isTrainingMode && currentImageAnnotations.Count > 0) SaveCurrentImageAnnotations();
            authTimer?.Stop(); base.OnClosing(e);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // DATA MODELS
    // ════════════════════════════════════════════════════════════════════
    public class AnnotationData
    {
        public string Label { get; set; }
        public Rect Bounds { get; set; }
        public double Angle { get; set; }
        public Rectangle VisualRect { get; set; }
        public Canvas VisualContainer { get; set; }
        public TextBlock LabelText { get; set; }
        public Ellipse RotDot { get; set; }
        public TextBox InlineTextBox { get; set; }
    }

    public class CharacterAnnotation
    {
        public string Label { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Angle { get; set; }
        public string ImagePath { get; set; }
    }

    public class SiameseOCRResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public int CharCount { get; set; }
        public string Error { get; set; }
        public string Method { get; set; }
        [JsonProperty("original_text")]
        public string OriginalText { get; set; }
        [JsonProperty("corrections_applied")]
        public int CorrectionsApplied { get; set; }
    }
}