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
        private string currentUser;
        private DispatcherTimer authTimer;
        private const int AUTH_TIMEOUT_MINUTES = 20;
        private bool isTrainingMode = false;

        private string currentProjectName = "DefaultProject";
        private string annotationsFolder;
        private string modelsFolder;
        private string currentProjectFolder;
        private const string PROJECTS_BASE_DIR = "Projects";
        private const int LABEL_OFFSET = 2;

        private List<AnnotationData> currentImageAnnotations = new List<AnnotationData>();

        // ── Template: resize-only cyan box ──────────────────────────────
        private double templateWidth = 50;
        private double templateHeight = 50;
        private double tplLeft = 10, tplTop = 40;
        private Rectangle tplRect;
        private Rectangle tplHandleBR, tplHandleR, tplHandleB;
        private TextBlock tplLabel;

        // Template drag state
        private bool isDraggingTpl = false;
        private bool isResizingTpl = false;
        private string resizeDir = "";
        private Point dragStart;
        private double dragStartW, dragStartH, dragStartL, dragStartT;

        // ── Stamp drag state ─────────────────────────────────────────────
        private bool isStamping = false;
        private Rectangle stampPreview = null;

        // ── Per-annotation rotation state ───────────────────────────────
        private AnnotationData rotatingAnn = null;
        private Point rotDragStart;
        private double rotStartAngle;

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
        // MODE SWITCH
        // ════════════════════════════════════════════════════════════════

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
                    EnsureTemplate();
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
                tplRect = null; tplHandleBR = tplHandleR = tplHandleB = null; tplLabel = null;
                btnAnnotate.IsEnabled = btnStartTraining.IsEnabled = false;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Inference | Project: {currentProjectName}";
            }
        }

        // ════════════════════════════════════════════════════════════════
        // TEMPLATE — cyan box, resize only, NO rotation
        // ════════════════════════════════════════════════════════════════

        private bool IsTemplateElement(object el) =>
            el == tplRect || el == tplHandleBR || el == tplHandleR || el == tplHandleB || el == tplLabel;

        private void EnsureTemplate()
        {
            if (tplRect != null) return;
            DrawTemplate();
        }

        private void RemoveTemplateVisuals()
        {
            foreach (var el in new UIElement[] { tplRect, tplHandleBR, tplHandleR, tplHandleB, tplLabel })
                if (el != null) annotationCanvas.Children.Remove(el);
            tplRect = null; tplHandleBR = tplHandleR = tplHandleB = null; tplLabel = null;
        }

        private void DrawTemplate()
        {
            RemoveTemplateVisuals();
            double W = templateWidth, H = templateHeight, L = tplLeft, T = tplTop;
            const double HS = 9;

            tplRect = new Rectangle
            {
                Width = W,
                Height = H,
                Stroke = Brushes.Cyan,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(15, 0, 255, 255)),
                Cursor = Cursors.SizeAll,
                Tag = "TPL",
            };
            PutOnCanvas(tplRect, L, T, 900);
            tplRect.MouseLeftButtonDown += TplBody_Down;
            tplRect.MouseLeftButtonUp += TplAny_Up;

            tplLabel = new TextBlock
            {
                Text = $"{(int)W}×{(int)H}",
                Foreground = Brushes.Cyan,
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
            };
            PutOnCanvas(tplLabel, L + 2, T + 2, 901);

            tplHandleBR = MakeTplHandle(Cursors.SizeNWSE, "BR");
            tplHandleR = MakeTplHandle(Cursors.SizeWE, "R");
            tplHandleB = MakeTplHandle(Cursors.SizeNS, "B");
            PutOnCanvas(tplHandleBR, L + W - HS, T + H - HS, 902);
            PutOnCanvas(tplHandleR, L + W - HS, T + H / 2 - HS / 2, 902);
            PutOnCanvas(tplHandleB, L + W / 2 - HS / 2, T + H - HS, 902);
        }

        private Rectangle MakeTplHandle(Cursor cur, string tag)
        {
            var r = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = Brushes.Cyan, StrokeThickness = 1.5, Cursor = cur, Tag = tag };
            r.MouseLeftButtonDown += TplHandle_Down;
            r.MouseLeftButtonUp += TplAny_Up;
            return r;
        }

        private void PutOnCanvas(UIElement el, double l, double t, int z)
        {
            Canvas.SetLeft(el, l); Canvas.SetTop(el, t); Panel.SetZIndex(el, z);
            if (!annotationCanvas.Children.Contains(el)) annotationCanvas.Children.Add(el);
        }

        private void UpdateTemplate()
        {
            if (tplRect == null) return;
            double W = templateWidth, H = templateHeight, L = tplLeft, T = tplTop;
            const double HS = 9;
            Canvas.SetLeft(tplRect, L); Canvas.SetTop(tplRect, T);
            tplRect.Width = W; tplRect.Height = H;
            Canvas.SetLeft(tplLabel, L + 2); Canvas.SetTop(tplLabel, T + 2);
            tplLabel.Text = $"{(int)W}×{(int)H}";
            Canvas.SetLeft(tplHandleBR, L + W - HS); Canvas.SetTop(tplHandleBR, T + H - HS);
            Canvas.SetLeft(tplHandleR, L + W - HS); Canvas.SetTop(tplHandleR, T + H / 2 - HS / 2);
            Canvas.SetLeft(tplHandleB, L + W / 2 - HS / 2); Canvas.SetTop(tplHandleB, T + H - HS);
        }

        private void TplBody_Down(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = true;
            dragStart = e.GetPosition(annotationCanvas);
            dragStartL = tplLeft; dragStartT = tplTop;
            tplRect.CaptureMouse(); e.Handled = true;
        }

        private void TplHandle_Down(object sender, MouseButtonEventArgs e)
        {
            isResizingTpl = true;
            resizeDir = (sender as FrameworkElement)?.Tag?.ToString() ?? "BR";
            dragStart = e.GetPosition(annotationCanvas);
            dragStartW = templateWidth; dragStartH = templateHeight;
            (sender as UIElement)?.CaptureMouse(); e.Handled = true;
        }

        private void TplAny_Up(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = isResizingTpl = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            Mouse.Capture(null); e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════════
        // CANVAS MOUSE
        // ════════════════════════════════════════════════════════════════

        private void AnnotationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!isTrainingMode || e.ChangedButton != MouseButton.Left) return;

            // Block if clicking template or existing annotation visuals
            if (e.OriginalSource is UIElement src && IsTemplateElement(src)) return;
            if (e.OriginalSource is Rectangle existR && currentImageAnnotations.Any(a => a.VisualRect == existR)) return;
            if (e.OriginalSource is Ellipse existE && currentImageAnnotations.Any(a => a.RotDot == existE)) return;

            // Begin drag-stamp: draw a preview rect that follows the mouse
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

        private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isTrainingMode) return;
            var pos = e.GetPosition(annotationCanvas);

            // Template move
            if (isDraggingTpl)
            {
                tplLeft = dragStartL + (pos.X - dragStart.X);
                tplTop = dragStartT + (pos.Y - dragStart.Y);
                UpdateTemplate(); return;
            }

            // Template resize
            if (isResizingTpl)
            {
                double dx = pos.X - dragStart.X, dy = pos.Y - dragStart.Y;
                if (resizeDir == "BR" || resizeDir == "R") templateWidth = Math.Max(15, dragStartW + dx);
                if (resizeDir == "BR" || resizeDir == "B") templateHeight = Math.Max(15, dragStartH + dy);
                UpdateTemplate(); return;
            }

            // Stamp preview follows mouse
            if (isStamping && stampPreview != null)
            {
                Canvas.SetLeft(stampPreview, pos.X - templateWidth / 2);
                Canvas.SetTop(stampPreview, pos.Y - templateHeight / 2);
                return;
            }

            // Per-annotation rotation
            if (rotatingAnn != null)
            {
                double cx = Canvas.GetLeft(rotatingAnn.VisualRect) + rotatingAnn.VisualRect.Width / 2;
                double cy = Canvas.GetTop(rotatingAnn.VisualRect) + rotatingAnn.VisualRect.Height / 2;
                double a0 = Math.Atan2(rotDragStart.Y - cy, rotDragStart.X - cx);
                double a1 = Math.Atan2(pos.Y - cy, pos.X - cx);
                double newAngle = (rotStartAngle + (a1 - a0) * 180.0 / Math.PI + 360) % 360;
                rotatingAnn.Angle = newAngle;
                ((RotateTransform)rotatingAnn.VisualRect.RenderTransform).Angle = newAngle;
                // Keep rotation dot above rect center
                if (rotatingAnn.RotDot != null)
                {
                    Canvas.SetLeft(rotatingAnn.RotDot, cx - 6);
                    Canvas.SetTop(rotatingAnn.RotDot, Canvas.GetTop(rotatingAnn.VisualRect) - 18);
                }
            }
        }

        private void AnnotationCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // Finish template ops
            isDraggingTpl = isResizingTpl = false;
            tplRect?.ReleaseMouseCapture();

            // Finish rotation
            if (rotatingAnn != null)
            {
                rotatingAnn.RotDot?.ReleaseMouseCapture();
                rotatingAnn = null;
                SaveCurrentImageAnnotations();
            }

            // Finish stamp — drop rect where mouse is, show label box
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
        // STAMP — green rect + rotation dot + inline label textbox
        // ════════════════════════════════════════════════════════════════

        private void StampAnnotation(Point center)
        {
            double x = center.X - templateWidth / 2.0;
            double y = center.Y - templateHeight / 2.0;

            // Green rect
            var rect = new Rectangle
            {
                Width = templateWidth,
                Height = templateHeight,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
            };
            rect.MouseRightButtonUp += AnnotationRect_RightClick;
            PutOnCanvas(rect, x, y, 100);

            // Label (starts empty)
            var lbl = new TextBlock
            {
                Text = "",
                Foreground = Brushes.Lime,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(2),
                IsHitTestVisible = false,
            };
            PutOnCanvas(lbl, x + LABEL_OFFSET, y + LABEL_OFFSET, 101);

            // Green rotation dot above this rect
            var rotDot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.Lime,
                Stroke = Brushes.DarkGreen,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
            };
            PutOnCanvas(rotDot, x + templateWidth / 2 - 6, y - 18, 102);

            // Build annotation object
            var ann = new AnnotationData
            {
                Label = "",
                Bounds = new Rect(x, y, templateWidth, templateHeight),
                Angle = 0,
                VisualRect = rect,
                LabelText = lbl,
                RotDot = rotDot,
            };

            // Wire rotation dot
            rotDot.MouseLeftButtonDown += (s, ev) =>
            {
                rotatingAnn = ann;
                rotDragStart = ev.GetPosition(annotationCanvas);
                rotStartAngle = ann.Angle;
                rotDot.CaptureMouse(); ev.Handled = true;
            };
            rotDot.MouseLeftButtonUp += (s, ev) =>
            {
                rotDot.ReleaseMouseCapture();
                rotatingAnn = null;
                SaveCurrentImageAnnotations(); ev.Handled = true;
            };

            // Inline textbox inside top-left of rect
            var tb = new TextBox
            {
                Width = Math.Min(templateWidth - 4, 30),
                Height = Math.Min(templateHeight - 4, 22),
                MaxLength = 1,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                BorderThickness = new Thickness(0),
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
            };
            PutOnCanvas(tb, x + 2, y + 2, 200);
            Dispatcher.InvokeAsync(() => tb.Focus(), System.Windows.Threading.DispatcherPriority.Input);

            void Confirm()
            {
                string label = tb.Text.Trim().ToUpper();
                annotationCanvas.Children.Remove(tb);
                if (string.IsNullOrEmpty(label))
                {
                    annotationCanvas.Children.Remove(rect);
                    annotationCanvas.Children.Remove(lbl);
                    annotationCanvas.Children.Remove(rotDot);
                    return;
                }
                lbl.Text = ann.Label = label;
                currentImageAnnotations.Add(ann);
                SaveCurrentImageAnnotations();
                UpdateAnnotationStatus();
            }

            tb.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter || ev.Key == Key.Return) { Confirm(); ev.Handled = true; }
                else if (ev.Key == Key.Escape)
                {
                    annotationCanvas.Children.Remove(tb);
                    annotationCanvas.Children.Remove(rect);
                    annotationCanvas.Children.Remove(lbl);
                    annotationCanvas.Children.Remove(rotDot);
                    ev.Handled = true;
                }
            };
            tb.LostFocus += (s, _) =>
            {
                if (annotationCanvas.Children.Contains(tb)) Confirm();
            };
        }

        // ════════════════════════════════════════════════════════════════
        // RIGHT-CLICK on placed annotation
        // ════════════════════════════════════════════════════════════════

        private void AnnotationRect_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Rectangle r) return;
            var ann = currentImageAnnotations.FirstOrDefault(a => a.VisualRect == r);
            if (ann == null) return;

            var menu = new ContextMenu();

            var edit = new MenuItem { Header = "✏️ Edit Label" };
            edit.Click += (s, _) =>
            {
                double rx = Canvas.GetLeft(ann.VisualRect), ry = Canvas.GetTop(ann.VisualRect);
                var tb = new TextBox
                {
                    Width = Math.Min(ann.VisualRect.Width - 4, 30),
                    Height = Math.Min(ann.VisualRect.Height - 4, 22),
                    MaxLength = 1,
                    Text = ann.Label,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    Background = Brushes.White,
                    BorderThickness = new Thickness(0),
                    TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                };
                PutOnCanvas(tb, rx + 2, ry + 2, 200);
                Dispatcher.InvokeAsync(() => { tb.Focus(); tb.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);

                void Save()
                {
                    if (!annotationCanvas.Children.Contains(tb)) return;
                    annotationCanvas.Children.Remove(tb);
                    string newLabel = tb.Text.Trim().ToUpper();
                    if (!string.IsNullOrEmpty(newLabel)) { ann.Label = newLabel; ann.LabelText.Text = newLabel; }
                    SaveCurrentImageAnnotations(); UpdateMasterAnnotationFile();
                }
                tb.KeyDown += (ts, te) => { if (te.Key == Key.Enter || te.Key == Key.Escape) { Save(); te.Handled = true; } };
                tb.LostFocus += (ts, _) => Save();
            };

            var del = new MenuItem { Header = "🗑️ Delete Annotation" };
            del.Click += (s, _) =>
            {
                annotationCanvas.Children.Remove(ann.VisualRect);
                annotationCanvas.Children.Remove(ann.LabelText);
                if (ann.RotDot != null) annotationCanvas.Children.Remove(ann.RotDot);
                currentImageAnnotations.Remove(ann);
                SaveCurrentImageAnnotations(); UpdateMasterAnnotationFile(); UpdateAnnotationStatus();
            };

            menu.Items.Add(edit);
            menu.Items.Add(del);
            r.ContextMenu = menu;
            menu.IsOpen = true;
        }

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
                if (currentImageAnnotations.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                var src = imgDisplay.Source as BitmapSource; if (src == null) return;
                double sx = src.PixelWidth / annotationCanvas.ActualWidth;
                double sy = src.PixelHeight / annotationCanvas.ActualHeight;
                var list = currentImageAnnotations.Select(a => new CharacterAnnotation
                {
                    Label = a.Label,
                    Angle = a.Angle,
                    ImagePath = imgPath,
                    X = a.Bounds.X * sx,
                    Y = a.Bounds.Y * sy,
                    Width = a.Bounds.Width * sx,
                    Height = a.Bounds.Height * sy,
                }).ToList();
                File.WriteAllText(file, JsonConvert.SerializeObject(list, Formatting.Indented));
                UpdateMasterAnnotationFile();
            }
            catch (Exception ex) { Debug.WriteLine($"Save: {ex.Message}"); }
        }

        private void LoadAnnotationsForCurrentImage()
        {
            // Remove everything except template visuals
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
                {
                    double x = a.X * sx, y = a.Y * sy, w = a.Width * sx, h = a.Height * sy;

                    var rect = new Rectangle
                    {
                        Width = w,
                        Height = h,
                        Stroke = Brushes.Lime,
                        StrokeThickness = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 0)),
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(a.Angle),
                    };
                    rect.MouseRightButtonUp += AnnotationRect_RightClick;
                    PutOnCanvas(rect, x, y, 100);

                    var lbl = new TextBlock
                    {
                        Text = a.Label,
                        Foreground = Brushes.Lime,
                        FontWeight = FontWeights.Bold,
                        FontSize = 11,
                        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                        Padding = new Thickness(2),
                        IsHitTestVisible = false,
                    };
                    PutOnCanvas(lbl, x + LABEL_OFFSET, y + LABEL_OFFSET, 101);

                    var rotDot = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = Brushes.Lime,
                        Stroke = Brushes.DarkGreen,
                        StrokeThickness = 1.5,
                        Cursor = Cursors.Hand,
                    };
                    PutOnCanvas(rotDot, x + w / 2 - 6, y - 18, 102);

                    var ann = new AnnotationData
                    {
                        Label = a.Label,
                        Bounds = new Rect(x, y, w, h),
                        Angle = a.Angle,
                        VisualRect = rect,
                        LabelText = lbl,
                        RotDot = rotDot,
                    };
                    currentImageAnnotations.Add(ann);

                    rotDot.MouseLeftButtonDown += (s, ev) =>
                    {
                        rotatingAnn = ann;
                        rotDragStart = ev.GetPosition(annotationCanvas);
                        rotStartAngle = ann.Angle;
                        rotDot.CaptureMouse(); ev.Handled = true;
                    };
                    rotDot.MouseLeftButtonUp += (s, ev) =>
                    {
                        rotDot.ReleaseMouseCapture(); rotatingAnn = null;
                        SaveCurrentImageAnnotations(); ev.Handled = true;
                    };
                }
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
        // IMAGE DISPLAY + LIST (with right-click remove from UI)
        // ════════════════════════════════════════════════════════════════

        private void UpdateCanvasSize()
        {
            if (imgDisplay.Source != null && imgDisplay.ActualWidth > 0)
            { annotationCanvas.Width = imgDisplay.ActualWidth; annotationCanvas.Height = imgDisplay.ActualHeight; }
        }

        private void ShowImage(string path)
        {
            try
            {
                if (isTrainingMode && currentImageAnnotations.Count > 0) SaveCurrentImageAnnotations();
                foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                    if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
                currentImageAnnotations.Clear();
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
                    if (isTrainingMode) { annotationCanvas.Visibility = Visibility.Visible; LoadAnnotationsForCurrentImage(); EnsureTemplate(); }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }

        private void UpdateImageList()
        {
            lstImages.Items.Clear();
            foreach (var path in imageFiles)
            {
                var item = new ListBoxItem { Content = IOPath.GetFileName(path), Tag = path };
                // Right-click to remove from UI (does NOT delete the file)
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
        // ANNOTATION STATUS + CLEAR BUTTONS
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
                    txtAnnotationStatus.Text = $" {a.Count} annotations\n{a.Select(x => x.ImagePath).Distinct().Count()} images | {a.Select(x => x.Label).Distinct().Count()} unique characters";
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

        private bool CheckAnnotationsExist() => File.Exists(IOPath.Combine(annotationsFolder, "all_annotations.json"));

        private void Annotate_Click(object sender, RoutedEventArgs e) { }
        private void CheckAnnotations_Click(object sender, RoutedEventArgs e) => UpdateAnnotationStatus();

        private void ClearCurrentImageAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            if (MessageBox.Show($"Clear annotations for {IOPath.GetFileName(imageFiles[currentImageIndex])}?",
                "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();
            string f = GetAnnotationFilePath(imageFiles[currentImageIndex]);
            if (File.Exists(f)) File.Delete(f);
            UpdateMasterAnnotationFile(); UpdateAnnotationStatus();
        }

        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Delete ALL annotations from ALL images?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (Directory.Exists(annotationsFolder))
                foreach (var f in Directory.GetFiles(annotationsFolder, "*.json")) File.Delete(f);
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList())
                if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();
            UpdateAnnotationStatus();
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
                    File.WriteAllText(GetAnnotationFilePath(grp.Key), JsonConvert.SerializeObject(grp.ToList(), Formatting.Indented));
                if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count) LoadAnnotationsForCurrentImage();
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════
        // TRAINING
        // ════════════════════════════════════════════════════════════════

        private async void StartTraining_Click(object sender, RoutedEventArgs e)
        {
            string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string trainScript = IOPath.Combine(baseDir, "train_last_layer.py");
            string annoFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            string modelDir = IOPath.Combine(currentProjectFolder, "TrainedModel");
            int epochs = 50, batch = 32;
            if (txtEpochs != null && int.TryParse(txtEpochs.Text, out int e2)) epochs = e2;
            if (txtBatchSize != null && int.TryParse(txtBatchSize.Text, out int b2)) batch = b2;
            if (!File.Exists(trainScript)) { MessageBox.Show($"train_last_layer.py not found"); return; }
            if (!File.Exists(annoFile)) { MessageBox.Show("No annotations found!"); return; }
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
                            if (a.Data.StartsWith("Epoch"))
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
                                double.TryParse(a.Data.Split(':')[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double acc);
                                progressBarTraining.Value = 100; txtTrainingStatus.Text = $"Done! Val: {acc:F1}%";
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
            string modelDir = IOPath.Combine(currentProjectFolder, "TrainedModel");
            string mdArg = Directory.Exists(modelDir) ? $"\"{modelDir}\"" : "\"\"";
            btnRunInference.IsEnabled = false; progressBar.IsIndeterminate = true;
            txtResult.Text = "Running OCR..."; txtStatus.Text = "Loading EasyOCR...";
            try
            {
                string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string script = IOPath.Combine(baseDir, "final_ocr.py");
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
                    proc.StandardOutput.ReadToEnd(); string err = proc.StandardError.ReadToEnd(); proc.WaitForExit();
                    if (proc.ExitCode != 0) return new SiameseOCRResult { Success = false, Error = err };
                    string rf = IOPath.Combine(baseDir, "output", "result.json");
                    if (!File.Exists(rf)) return new SiameseOCRResult { Success = false, Error = "No result.json" };
                    var r = JsonConvert.DeserializeObject<SiameseOCRResult>(File.ReadAllText(rf));
                    r.Success = true; return r;
                });
                if (result.Success)
                {
                    txtResult.Text = result.Text; txtConfidence.Text = $"Confidence: {result.Confidence:F1}%";
                    if (result.Confidence >= 80) { txtDecision.Text = "HIGH CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Green); }
                    else if (result.Confidence >= 60) { txtDecision.Text = "MEDIUM CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Orange); }
                    else { txtDecision.Text = "LOW CONFIDENCE"; txtDecision.Foreground = new SolidColorBrush(Colors.Red); }
                    txtStatus.Text = result.Method ?? "EasyOCR";
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

        private void LoadFolder_Click(object sender, RoutedEventArgs e) => LoadFiles_Click(sender, e);

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem li && li.Tag is string path)
            { currentImageIndex = imageFiles.IndexOf(path); ShowImage(path); }
        }

        private void ToolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try { if (cmbToolType?.SelectedItem is ComboBoxItem ci) currentTool = ci.Content.ToString(); } catch { }
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            string mp = IOPath.Combine(modelsFolder, "siamese_model.pth");
            if (!File.Exists(mp)) { MessageBox.Show("No model found!"); return; }
            var d = new SaveFileDialog { Filter = "PyTorch Model|*.pth", FileName = $"{currentProjectName}.pth" };
            if (d.ShowDialog() == true) File.Copy(mp, d.FileName, true);
        }

        private void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "PyTorch Model|*.pth" };
            if (d.ShowDialog() == true) { Directory.CreateDirectory(modelsFolder); File.Copy(d.FileName, IOPath.Combine(modelsFolder, "siamese_model.pth"), true); }
        }

        private void Accept_Click(object sender, RoutedEventArgs e) { }
        private void Reject_Click(object sender, RoutedEventArgs e) { }
        private void AutoDetectCharacters_Click(object sender, RoutedEventArgs e) { }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (isTrainingMode && currentImageAnnotations.Count > 0) SaveCurrentImageAnnotations();
            authTimer?.Stop(); base.OnClosing(e);
        }
    }

    public class AnnotationData
    {
        public string Label { get; set; }
        public Rect Bounds { get; set; }
        public double Angle { get; set; }
        public Rectangle VisualRect { get; set; }
        public TextBlock LabelText { get; set; }
        public Ellipse RotDot { get; set; }  // per-annotation green rotation handle
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
    }
}