using Basler.Pylon;
using Microsoft.Win32;
using Newtonsoft.Json;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using OpenCvSharp.Extensions;
using OpenCvSharp.XFeatures2D;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
// Aliases for WinForms/System.Drawing code
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBitmapData = System.Drawing.Imaging.BitmapData;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using IOPath = System.IO.Path;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfLine = System.Windows.Shapes.Line;
using WpfPath = System.Windows.Shapes.Path;
// Aliases for WPF code
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfWindow = System.Windows.Window;
namespace PixtechApplication
{
    public partial class MainWindow : WpfWindow
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

        // ── Active Learning Variables ──
        private SiameseOCRResult lastInferenceResult = null;
        private string lastInferenceImagePath = null;
        private Process ocrServerProcess;
        private readonly object ocrServerLock = new object();
        // ── Template Box ──
        // ── Template Box ──
        private const double DEFAULT_TEMPLATE_WIDTH = 60;
        private const double DEFAULT_TEMPLATE_HEIGHT = 80;

        private double templateWidth = DEFAULT_TEMPLATE_WIDTH;
        private double templateHeight = DEFAULT_TEMPLATE_HEIGHT;

        private double tplLeft = 10, tplTop = 10;

        private WpfRectangle tplRect;
        private TextBlock tplLabel;

        private bool isDraggingTpl = false;
        private bool isResizingTpl = false;
        private string resizeDir = "";

        private WpfPoint dragStart;
        private double dragStartW, dragStartH, dragStartL, dragStartT;


        // ── Stamping & Moving ──
        private bool isStamping = false;

        private WpfRectangle stampPreview = null;

        private AnnotationData rotatingAnn = null;
        private WpfPoint rotDragStart;

        private double rotStartAngle;

        private AnnotationData movingAnn = null;
        private double moveStartLeft, moveStartTop;


        // ── ROI Regions (Multiple) ──
        private List<RoiRegion> roiRegions = new List<RoiRegion>();
        private WpfPath roiOverlayPath;
        private RoiRegion activeRoi = null;

        private bool isDraggingRoi = false;
        private bool isResizingRoi = false;
        private bool isRotatingRoi = false;
        private string roiResizeDir = "";
        private WpfPoint roiDragStart;
        private double roiDragStartW, roiDragStartH, roiDragStartL, roiDragStartT;
        private double roiRotationStartAngle = 0;

        private bool isDrawingRoi = false;
        private WpfPoint roiDrawOrigin;
        private WpfRectangle roiDrawPreview = null;

        private const double EDGE_THRESH = 10;
        private const double ROI_GRAB_RADIUS = 40;

        // ── Z-index constants ──
        private const int Z_OVERLAY = 10;
        private const int Z_ROI_BORDER = 20;
        private const int Z_ROI_LABEL = 21;
        private const int Z_ROI_PREVIEW = 25;
        private const int Z_ANNOTATION = 200;
        private const int Z_STAMP_GHOST = 600;
        private const int Z_TEMPLATE = 900;
        private const int Z_TEMPLATE_LBL = 901;

        public MainWindow() : this("Guest") { }

        // Images & Count //
        Mat img = new Mat();
        Mat imgInput = new Mat();
        Mat saveImg = new Mat();
        Mat temp = new Mat();
        Mat temp1 = new Mat();
        Mat img1 = new Mat();
        Mat res = new Mat();
        public Mat templateImg = new Mat();
        List<string> variantList = new List<string>();


        // Int //
        int frameCount = 0;
        int click = 0;
        public int x = 0, y = 0, w = 0, h = 0;

        // Rectangles //
        Mat Image = new Mat();
        //List<Rect> rects = new List<Rect>();
        List<double> value = new List<double>();



        // Rect //
        //Rectangle rect;
        //Rect rrect;
        //List<Rect> Rectangle_Values = new List<Rect>();

        // Camera //
        Camera basler_camera;                                    // Basler Camera
        private List<string> detectedCameras; // List to store detected Logitech Camera Names
        private BitmapSource lastGrabbedFrame;
        public static class BitmapHelper
        {
            [DllImport("gdi32.dll")]
            public static extern bool DeleteObject(IntPtr hObject);

            public static BitmapSource ToBitmapSource(DrawingBitmap bitmap)
            {
                IntPtr hBitmap = bitmap.GetHbitmap();

                try
                {
                    return Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
        private void Camera_Check()
        {
            try
            {
                List<ICameraInfo> allCameras = CameraFinder.Enumerate(); //store the data of Basler Camera
                detectedCameras = new List<string>();

                bool basler_cam = false;
               
                if (allCameras.Count > 0)
                {
                    basler_cam = true;
                }
             
                if (basler_cam) 
                {
                    //toolStripCameraStatus.Text = "Basler_Logitech_Camera Found";
                    //toolStripCameraStatus.ForeColor = Color.Orange;
                    MessageBox.Show("camera_found");
                   
                }
                else
                {
                    MessageBox.Show("camera not found");
                }



            }
            catch (Exception Ex)
            {
                //toolStripCameraStatus.Text = "Camera Not Connected";
                //toolStripCameraStatus.ForeColor = Color.Red;
                //log.Error("Camera_Check" + Ex.Message.ToString());
                if (basler_camera != null)
                {
                    basler_camera.Close();
                    basler_camera.Dispose();
                }
            }
        }

        private void OpenBaslerCamera()
        {
            try
            {
                //For Camera Connection and Grabbing Image
                //basler_camera = new Camera("24457216");
                basler_camera = new Camera();
                if (basler_camera.IsOpen)
                {
                    //toolStripCameraStatus.Text = "Camera Connected";
                    //toolStripCameraStatus.ForeColor = Color.Green;
                    MessageBox.Show("Camera Already Opened");
                }
                else
                {
                    basler_camera.Open(); //camera open
                    if (basler_camera.IsOpen)
                    {
                        if (basler_camera.IsConnected) // condition camera is connected or notconnected
                        {
                            //toolStripCameraStatus.ForeColor = Color.Green; //label color
                            //toolStripCameraStatus.Text = "Camera Connected"; //label using camera is connected

                           // if (rb_softwaretrigger.Checked)
                            {
                                //basler_camera.Parameters[PLCamera.TriggerMode].TrySetValue(PLCamera.TriggerMode.On);
                                basler_camera.Parameters[PLCamera.TriggerSource].TrySetValue(PLCamera.TriggerSource.Software);
                                basler_camera.Parameters[PLCamera.AcquisitionMode].SetValue(PLCamera.AcquisitionMode.Continuous);
                                //basler_camera.Parameters[PLCamera.ExposureTimeAbs].TrySetValue(700);
                               // timer_plc.Start();
                            }
                            //if (rb_hardwaretrigger.Checked)
                            //{
                            //    basler_camera.Parameters[PLCamera.TriggerMode].TrySetValue(PLCamera.TriggerMode.On);
                            //    basler_camera.Parameters[PLCamera.TriggerSource].TrySetValue(PLCamera.TriggerSource.Line1);
                            //    //basler_camera.Parameters[PLCamera.TriggerDelayAbs].TrySetValue(Convert.ToInt32(175000));  // time delay
                            //    basler_camera.Parameters[PLCamera.ExposureTimeAbs].TrySetValue(700);
                            //    if (basler_camera.StreamGrabber.IsGrabbing == false)
                            //        basler_camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
                            //}

                            basler_camera.StreamGrabber.ImageGrabbed += OnImageGrabbed; //onimage grabbed
                            basler_camera.StreamGrabber.GrabStopped += OnGrabStopped;
                            basler_camera.ConnectionLost += OnConnectionLost;

                        }
                    }
                    else
                    {
                        //toolStripCameraStatus.ForeColor = Color.Red;
                        //toolStripCameraStatus.Text = "Camera Not Connected!";
                        MessageBox.Show("Camera Not Connected!");
                    }
                }
            }

            catch (Exception Ex)
            {
                //toolStripErrorMessage.Text = Ex.Message;
                //toolStripErrorMessage.ForeColor = Color.Red;
                //log.Error("OpenBaslerCamera" + Ex.Message.ToString());
                //  log.Error(Environment.NewLine + "open Basler Camera" + Environment.NewLine + Ex.Message.ToString() + Environment.NewLine);
                if (basler_camera != null) //condition is checking
                {
                    basler_camera.Close(); //close the camera
                }
                //MessageBox.Show("FAILED TO OPEN THE CAMERA. REPLUG");
            }

        }

        private void CloseBaslerCamera() //function
        {
            try
            {
               // timer_plc.Stop(); //timer using stop
                //To close camera connection
                if (basler_camera != null)
                {
                    if (basler_camera.IsOpen)  // condition is true
                    {
                        basler_camera.StreamGrabber.Stop(); //stop the streamgrabber
                        basler_camera.Close();           //close the camera
                        basler_camera.Dispose();


                        //toolStripCameraStatus.ForeColor = Color.Orange; //label forecolor
                        //toolStripCameraStatus.Text = "Camera Connection Closed"; //label "camera is connected is closed
                        //toolStripCameraStatus.Text = "Camera Status";
                        //toolStripCameraStatus.ForeColor = Color.Black;
                    }
                    else
                    {
                        //basler_camera.StreamGrabber.Stop();
                        basler_camera.Close();
                        basler_camera.Dispose();
                        //toolStripCameraStatus.Text = "Camera Status";
                        //toolStripCameraStatus.ForeColor = Color.Black;
                        MessageBox.Show("Camera Already Closed."); // message show
                    }
                }
            }
            catch (Exception Ex)
            {
                //toolStripErrorMessage.Text = Ex.Message;
                //toolStripErrorMessage.ForeColor = Color.Red;
                //log.Error("CloseBaslerCamera" + Ex.Message.ToString());
                // log.Error(Environment.NewLine + "Close Camera" + Environment.NewLine + Ex.Message.ToString() + Environment.NewLine);
                if (basler_camera != null)
                {
                    basler_camera.Close(); //camera close
                    basler_camera.Dispose();
                }
                MessageBox.Show(Ex.Message.ToString());
            }
        }
        private bool isLive = false;

        private void ImageContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (annotationCanvas.Visibility == Visibility.Visible)
                return;

            if (e.ClickCount != 2)
                return; // single click does nothing now — only double-click toggles live

            if (!isLive)
            {
                StartLiveView();
            }
            else
            {
                StopLiveView();
                if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count)
                    ShowImage(imageFiles[currentImageIndex]); // bring back whatever was selected
            }
        }


        private void StartLiveView()
        {
            try
            {
                if (basler_camera == null)
                    return;

                if (!basler_camera.IsOpen)
                    basler_camera.Open();

                basler_camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);

                isLive = true;
                txtPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start live view: {ex.Message}");
            }
        }

        private void StopLiveView()
        {
            if (basler_camera != null && basler_camera.StreamGrabber.IsGrabbing)
                basler_camera.StreamGrabber.Stop();

            isLive = false;
        }


        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopLiveView();
        }
        private void OnImageGrabbed(object sender, ImageGrabbedEventArgs e)
        {
            IGrabResult res = e.GrabResult;

            if (!res.GrabSucceeded)
                return;

            BitmapSource source;

            using (PixelDataConverter converter = new PixelDataConverter())
            {
                converter.OutputPixelFormat = PixelType.BGRA8packed;

                DrawingBitmap bitmap = new DrawingBitmap(
                    res.Width,
                    res.Height,
                    DrawingPixelFormat.Format32bppArgb);

                DrawingBitmapData bmpData = bitmap.LockBits(
                    new DrawingRectangle(0, 0, res.Width, res.Height),
                    DrawingImageLockMode.WriteOnly,
                    DrawingPixelFormat.Format32bppArgb);

                try
                {
                    converter.Convert(
                        (IntPtr)bmpData.Scan0,
                        bmpData.Stride * res.Height,
                        res);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }

                BitmapSource bitmapSource = BitmapHelper.ToBitmapSource(bitmap);
                bitmapSource.Freeze();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtPlaceholder.Visibility = Visibility.Collapsed;
                    lastGrabbedFrame = bitmapSource;
                    if (isLive)
                    {
                        imgDisplay.Source = bitmapSource;
                        return;
                    }

                    try
                    {
                        string capturesFolder = IOPath.Combine(currentProjectFolder, "Captures");
                        Directory.CreateDirectory(capturesFolder);
                        string filePath = IOPath.Combine(capturesFolder, $"grab_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                        using (var fs = new FileStream(filePath, FileMode.Create))
                        {
                            encoder.Save(fs);
                        }

                        imageFiles.Add(filePath);
                        UpdateImageList();
                        currentImageIndex = imageFiles.Count - 1;
                        ShowImage(filePath);
                        lstImages.SelectedIndex = currentImageIndex;
                        if (isTrainingMode) btnAnnotate.IsEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to save grabbed frame: {ex.Message}");
                    }
                }));

                currentCameraImage = img.Clone();
            }
        }
        
        private void OnGrabStopped(Object sender, GrabStopEventArgs e)
        {
            try
            {
                if (!Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        OnGrabStopped(sender, e);
                    }));
                    return;
                }
            }
            catch (Exception Ex)
            {
                //toolStripErrorMessage.Text = Ex.Message;
                //toolStripErrorMessage.ForeColor = Color.Red;
                //log.Error("OnGrabStopped" + Ex.Message.ToString());
                // log.Error(Environment.NewLine + "on grab stopped" + Environment.NewLine + Ex.Message.ToString() + Environment.NewLine);

            }
        }

        public void OnConnectionLost(Object sender, EventArgs e)
        {
            try
            {
                //toolStripCameraStatus.Text = "Camera is not connected";
                //toolStripCameraStatus.ForeColor = Color.Red;
                MessageBox.Show("Camera Connection Lost. Check camera connection and restart the application");
            }
            catch (Exception Ex)
            {
                //toolStripErrorMessage.Text = Ex.Message;
                //toolStripErrorMessage.ForeColor = Color.Red;
                //log.Error("OnConnectionLost" + Ex.Message.ToString());
                //  log.Error(Environment.NewLine + "on connection lost" + Environment.NewLine + Ex.Message.ToString() + Environment.NewLine);
            }

        }


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
            ScrubCrossContaminatedAnnotations();
        }

        private void ScrubCrossContaminatedAnnotations()
        {
            try
            {
                foreach (var jsonFile in Directory.GetFiles(annotationsFolder, "*.json"))
                {
                    if (IOPath.GetFileName(jsonFile) == "all_annotations.json") continue;
                    string expectedImgBase = IOPath.GetFileNameWithoutExtension(jsonFile);
                    var list = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(jsonFile));
                    if (list == null) continue;
                    var clean = list.Where(a =>
                        string.IsNullOrEmpty(a.ImagePath) ||
                        IOPath.GetFileNameWithoutExtension(a.ImagePath) == expectedImgBase
                    ).ToList();
                    if (clean.Count != list.Count)
                    {
                        if (clean.Count == 0) File.Delete(jsonFile);
                        else File.WriteAllText(jsonFile, JsonConvert.SerializeObject(clean, Formatting.Indented));
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"ScrubAnnotations: {ex.Message}"); }
        }

        private string GetTrainedModelDir() => IOPath.Combine(currentProjectFolder, "TrainedModel");

        private bool HasTrainedModel()
        {
            string md = GetTrainedModelDir();
            return Directory.Exists(md) && File.Exists(IOPath.Combine(md, "model_config.json"));
        }

        private void ClearTrainedModel()
        {
            string md = GetTrainedModelDir();
            if (Directory.Exists(md))
                try { Directory.Delete(md, true); }
                catch (Exception ex) { Debug.WriteLine($"ClearTrainedModel: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════
        // GEOMETRY & ROI LOGIC
        // ════════════════════════════════════════════════════════════════
        private WpfRect GetImageRect()
        {
            double w = double.IsNaN(annotationCanvas.Width) ? annotationCanvas.ActualWidth : annotationCanvas.Width;
            double h = double.IsNaN(annotationCanvas.Height) ? annotationCanvas.ActualHeight : annotationCanvas.Height;
            return new WpfRect(0, 0, w, h);
        }

        private (double l, double t) ClampToCanvas(double left, double top, double w, double h)
        {
            WpfRect img = GetImageRect();
            return (Math.Max(img.Left, Math.Min(left, img.Right - w)),
                    Math.Max(img.Top, Math.Min(top, img.Bottom - h)));
        }



        private bool IsRoiElement(object el)
        {
            if (el == roiOverlayPath) return true;
            foreach (var r in roiRegions)
                if (el == r.RectVisual || el == r.LabelVisual || el == r.RotHandleVisual || el == r.RotStemVisual) return true;
            return false;
        }

        private void UpdateOverlayGeometry()
        {
            double cW = Math.Max(10, annotationCanvas.ActualWidth);
            double cH = Math.Max(10, annotationCanvas.ActualHeight);
            Geometry combined = new RectangleGeometry(new WpfRect(0, 0, cW, cH));
            foreach (var r in roiRegions)
            {
                var hole = new RectangleGeometry(new WpfRect(r.Left, r.Top, r.Width, r.Height));
                hole.Transform = new RotateTransform(r.Angle, r.Left + r.Width / 2.0, r.Top + r.Height / 2.0);
                combined = new CombinedGeometry(GeometryCombineMode.Exclude, combined, hole);
            }
            if (roiOverlayPath != null) roiOverlayPath.Data = combined;
        }

        private void RemoveAllRoiVisuals()
        {
            annotationCanvas.Children.Clear();
            roiOverlayPath = null;
            foreach (var r in roiRegions)
            {
                r.RectVisual = null; r.LabelVisual = null; r.RotHandleVisual = null; r.RotStemVisual = null;
            }
        }

        private void RebuildRoiVisuals()
        {
            RemoveAllRoiVisuals();

            roiOverlayPath = new WpfPath
            {
                Fill = new SolidColorBrush(WpfColor.FromArgb(140, 0, 0, 0)),
                IsHitTestVisible = true
            };
            Panel.SetZIndex(roiOverlayPath, Z_OVERLAY);
            annotationCanvas.Children.Add(roiOverlayPath);

            foreach (var region in roiRegions)
            {
                CreateRoiRegionVisuals(region);
                UpdateRegionVisual(region);
            }
            UpdateOverlayGeometry();
        }

        private void CreateRoiRegionVisuals(RoiRegion region)
        {
            region.RectVisual = new WpfRectangle
            {
                Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 140, 0)),
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                Fill = WpfBrushes.Transparent,
                Tag = "ROI",
            };
            Panel.SetZIndex(region.RectVisual, Z_ROI_BORDER);
            annotationCanvas.Children.Add(region.RectVisual);
            region.RectVisual.MouseLeftButtonDown += (s, ev) => RoiRect_MouseDown(region, ev);
            region.RectVisual.MouseMove += (s, ev) => RoiRect_MouseMove_Cursor(region, ev);
            region.RectVisual.MouseRightButtonUp += (s, ev) => { ShowRoiContextMenu(region); ev.Handled = true; };

            region.LabelVisual = new TextBlock
            {
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 140, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(region.LabelVisual, Z_ROI_LABEL);
            annotationCanvas.Children.Add(region.LabelVisual);

            region.RotHandleVisual = new WpfEllipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(WpfColor.FromRgb(255, 140, 0)),
                Stroke = WpfBrushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
                Tag = "ROI_ROT"
            };
            Panel.SetZIndex(region.RotHandleVisual, Z_ROI_BORDER + 2);
            annotationCanvas.Children.Add(region.RotHandleVisual);

            region.RotStemVisual = new WpfLine
            {
                Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 140, 0)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Tag = "ROI_ROT"
            };
            Panel.SetZIndex(region.RotStemVisual, Z_ROI_BORDER + 1);
            annotationCanvas.Children.Add(region.RotStemVisual);

            region.RotHandleVisual.MouseLeftButtonDown += (s, ev) =>
            {
                isRotatingRoi = true; activeRoi = region;
                roiDragStart = ev.GetPosition(annotationCanvas);
                roiRotationStartAngle = region.Angle;
                region.RotHandleVisual.CaptureMouse(); ev.Handled = true;
            };
            region.RotHandleVisual.MouseMove += (s, ev) =>
            {
                if (!isRotatingRoi || activeRoi != region) return;
                var pos = ev.GetPosition(annotationCanvas);
                double rcx = region.Left + region.Width / 2.0;
                double rcy = region.Top + region.Height / 2.0;
                double a0 = Math.Atan2(roiDragStart.Y - rcy, roiDragStart.X - rcx);
                double a1 = Math.Atan2(pos.Y - rcy, pos.X - rcx);
                region.Angle = (roiRotationStartAngle + (a1 - a0) * 180.0 / Math.PI + 360) % 360;
                UpdateRegionVisual(region); ev.Handled = true;
            };
            region.RotHandleVisual.MouseLeftButtonUp += (s, ev) =>
            {
                isRotatingRoi = false; activeRoi = null;
                region.RotHandleVisual.ReleaseMouseCapture();
                SaveRoisForCurrentImage(); ev.Handled = true;
            };
        }

        private void UpdateRegionVisual(RoiRegion r)
        {
            if (r.RectVisual == null) return;
            double cx = r.Left + r.Width / 2.0;
            double cy = r.Top + r.Height / 2.0;

            Canvas.SetLeft(r.RectVisual, r.Left);
            Canvas.SetTop(r.RectVisual, r.Top);
            r.RectVisual.Width = r.Width;
            r.RectVisual.Height = r.Height;
            r.RectVisual.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            r.RectVisual.RenderTransform = new RotateTransform(r.Angle);
            Canvas.SetLeft(r.LabelVisual, r.Left + 4);
            Canvas.SetTop(r.LabelVisual, r.Top - 16);
            string angleStr = r.Angle != 0 ? $" ↻{(int)r.Angle}°" : "";
            r.LabelVisual.Text = $"ROI {roiRegions.IndexOf(r) + 1}  {(int)r.Width}×{(int)r.Height}{angleStr}";

            double rad = r.Angle * Math.PI / 180.0;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
            double handleX = cx - (r.Top - 28 - cy) * sinA;
            double handleY = cy + (r.Top - 28 - cy) * cosA;
            Canvas.SetLeft(r.RotHandleVisual, handleX - r.RotHandleVisual.Width / 2.0);
            Canvas.SetTop(r.RotHandleVisual, handleY - r.RotHandleVisual.Height / 2.0);

            double edgeX = cx - (r.Top - cy) * sinA;
            double edgeY = cy + (r.Top - cy) * cosA;
            r.RotStemVisual.X1 = handleX; r.RotStemVisual.Y1 = handleY;
            r.RotStemVisual.X2 = edgeX; r.RotStemVisual.Y2 = edgeY;

            UpdateOverlayGeometry();
        }

        private void StartDrawingNewRoi(WpfPoint pt)
        {
            if (roiRegions.Count > 0)
            {
                RemoveAllRoiVisuals();  // uses roiRegions to find visuals
                roiRegions.Clear();     // THEN clear
                DeleteRoiFile();
            }
            isDrawingRoi = true;
            roiDrawOrigin = pt;
            roiDrawPreview = new WpfRectangle
            {
                Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 140, 0)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Fill = new SolidColorBrush(WpfColor.FromArgb(18, 255, 140, 0)),
                IsHitTestVisible = false,
                Width = 1,
                Height = 1,
            };
            Canvas.SetLeft(roiDrawPreview, pt.X); Canvas.SetTop(roiDrawPreview, pt.Y);
            Panel.SetZIndex(roiDrawPreview, Z_ROI_PREVIEW);
            annotationCanvas.Children.Add(roiDrawPreview);
            annotationCanvas.CaptureMouse();
        }
        private void RoiRect_MouseDown(RoiRegion region, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(region.RectVisual);
            double W = region.RectVisual.ActualWidth, H = region.RectVisual.ActualHeight;
            bool r = p.X >= W - EDGE_THRESH, b = p.Y >= H - EDGE_THRESH;
            bool l = p.X <= EDGE_THRESH, t = p.Y <= EDGE_THRESH;

            if (!(r || b || l || t))
            {
                isStamping = true;
                double px = e.GetPosition(annotationCanvas).X - templateWidth / 2;
                double py = e.GetPosition(annotationCanvas).Y - templateHeight / 2;
                (px, py) = ClampToCanvas(px, py, templateWidth, templateHeight);

                stampPreview = new WpfRectangle
                {
                    Width = templateWidth,
                    Height = templateHeight,
                    Stroke = WpfBrushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WpfColor.FromArgb(40, 0, 255, 0)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(stampPreview, px); Canvas.SetTop(stampPreview, py);
                Panel.SetZIndex(stampPreview, Z_STAMP_GHOST);
                annotationCanvas.Children.Add(stampPreview);

                annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            activeRoi = region;
            roiDragStart = e.GetPosition(annotationCanvas);
            roiDragStartW = region.Width; roiDragStartH = region.Height;
            roiDragStartL = region.Left; roiDragStartT = region.Top;
            isResizingRoi = true;
            roiResizeDir = $"{(r ? "R" : "")}{(l ? "L" : "")}{(b ? "B" : "")}{(t ? "T" : "")}";

            annotationCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void RoiRect_MouseMove_Cursor(RoiRegion region, MouseEventArgs e)
        {
            if (isDraggingRoi || isResizingRoi) return;
            var p = e.GetPosition(region.RectVisual);
            double W = region.RectVisual.ActualWidth, H = region.RectVisual.ActualHeight;
            bool r = p.X >= W - EDGE_THRESH, b = p.Y >= H - EDGE_THRESH;
            bool l = p.X <= EDGE_THRESH, t = p.Y <= EDGE_THRESH;
            if ((r && b) || (l && t)) region.RectVisual.Cursor = Cursors.SizeNWSE;
            else if ((r && t) || (l && b)) region.RectVisual.Cursor = Cursors.SizeNESW;
            else if (r || l) region.RectVisual.Cursor = Cursors.SizeWE;
            else if (b || t) region.RectVisual.Cursor = Cursors.SizeNS;
            else region.RectVisual.Cursor = Cursors.Arrow;
        }

        private void ShowRoiContextMenu(RoiRegion region)
        {
            var menu = new ContextMenu();

            var rot90 = new MenuItem { Header = "↻  Rotate 90°" };
            rot90.Click += (s, _) =>
            {
                region.Angle = (region.Angle + 90) % 360;
                RebuildRoiVisuals();  // full rebuild instead of just update
                SaveRoisForCurrentImage();
            };
            menu.Items.Add(rot90);

            var rot0 = new MenuItem { Header = "⟲  Reset Rotation (0°)" };
            rot0.Click += (s, _) =>
            {
                region.Angle = 0;
                RebuildRoiVisuals();
                SaveRoisForCurrentImage();
            };
            menu.Items.Add(rot0);
            menu.Items.Add(new Separator());

            var del = new MenuItem { Header = " Delete ROI" };
            del.Click += (s, _) =>
            {
                roiRegions.Clear();

                // Remove EVERYTHING except template and annotations
                var toKeep = new List<UIElement>();
                foreach (UIElement el in annotationCanvas.Children)
                {
                    if (IsTemplateElement(el) || IsChildOfAnnotation(el))
                        toKeep.Add(el);
                    // Also keep annotation containers
                    if (currentImageAnnotations.Any(a => a.VisualContainer == el))
                        toKeep.Add(el);
                }
                annotationCanvas.Children.Clear();
                foreach (var el in toKeep) annotationCanvas.Children.Add(el);

                roiOverlayPath = null;
                DeleteRoiFile();
            };
            menu.Items.Add(del);

            menu.IsOpen = true;
        }

        private void SaveRoisForCurrentImage()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            if (roiRegions.Count == 0) { DeleteRoiFile(); return; }

            string imgPath = imageFiles[currentImageIndex];
            int pw, ph;
            try { (pw, ph) = GetNativePixelSize(imgPath); }
            catch { return; }
            if (pw == 0 || ph == 0) return;

            double cW = double.IsNaN(annotationCanvas.Width) ? annotationCanvas.ActualWidth : annotationCanvas.Width;
            double cH = double.IsNaN(annotationCanvas.Height) ? annotationCanvas.ActualHeight : annotationCanvas.Height;
            if (cW < 1 || cH < 1) return;

            var list = roiRegions.Select(r => new RoiData
            {
                Id = r.Id,
                X = r.Left / cW,
                Y = r.Top / cH,
                Width = r.Width / cW,
                Height = r.Height / cH,
                ImgW = pw,
                ImgH = ph,
                Angle = r.Angle,
            }).ToList();

            File.WriteAllText(GetRoiFilePath(imgPath), JsonConvert.SerializeObject(list, Formatting.Indented));
        }

        private void LoadRoisForCurrentImage()
        {
            roiRegions.Clear();
            RemoveAllRoiVisuals();
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            string f = GetRoiFilePath(imageFiles[currentImageIndex]);
            if (!File.Exists(f)) return;
            if (imgDisplay.Source == null) return;
            double cW = annotationCanvas.Width; double cH = annotationCanvas.Height;
            if (cW < 10 || cH < 10) return;

            try
            {
                string raw = File.ReadAllText(f);
                List<RoiData> list;
                try { list = JsonConvert.DeserializeObject<List<RoiData>>(raw); }
                catch
                {
                    var single = JsonConvert.DeserializeObject<RoiData>(raw); // old single-ROI files
                    list = single != null ? new List<RoiData> { single } : null;
                }
                if (list == null) return;

                foreach (var data in list)
                {
                    if (data == null || data.Width <= 0 || data.Height <= 0) continue;
                    var region = new RoiRegion
                    {
                        Id = string.IsNullOrEmpty(data.Id) ? Guid.NewGuid().ToString("N").Substring(0, 6) : data.Id,
                        Left = data.X * cW,
                        Top = data.Y * cH,
                        Width = data.Width * cW,
                        Height = data.Height * cH,
                        Angle = data.Angle,
                    };
                    if (region.Width < 5 || region.Height < 5) continue;
                    roiRegions.Add(region);
                }
                RebuildRoiVisuals();
            }
            catch (Exception ex) { Debug.WriteLine($"ROI load: {ex.Message}"); }
        }
        private readonly System.Text.StringBuilder ocrServerErrorLog = new System.Text.StringBuilder();

        private void EnsureOcrServerRunning()
        {
            if (ocrServerProcess != null && !ocrServerProcess.HasExited) return;

            ocrServerErrorLog.Clear();

            string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string serverScript = IOPath.Combine(baseDir, "PythonScripts", "ocr_server.py");

            ocrServerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = $"\"{serverScript}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = baseDir,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardInputEncoding = System.Text.Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            ocrServerProcess.ErrorDataReceived += (s, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data))
                {
                    Debug.WriteLine($"[OCR SERVER] {a.Data}");
                    lock (ocrServerErrorLog) { ocrServerErrorLog.AppendLine(a.Data); }
                }
            };
            ocrServerProcess.Start();
            ocrServerProcess.BeginErrorReadLine();
        }

        private async Task<SiameseOCRResult> RunOcrServerRequest(Mat currentCameraImage, string imgPath, string modelDir, string roiCsv)
        {
            EnsureOcrServerRunning();
            string requestJson = JsonConvert.SerializeObject(new { image_path = imgPath, model_dir = modelDir, roi = roiCsv });

            return await Task.Run(() =>
            {
                lock (ocrServerLock)
                {
                    try
                    {
                        ocrServerProcess.StandardInput.WriteLine(requestJson);
                        ocrServerProcess.StandardInput.Flush();
                        string responseLine = ocrServerProcess.StandardOutput.ReadLine();
                        if (responseLine == null)
                        {
                            string details;
                            lock (ocrServerErrorLog) { details = ocrServerErrorLog.ToString(); }
                            if (string.IsNullOrWhiteSpace(details)) details = "(no stderr captured — process likely died before printing, or exited code 0 with no output)";
                            return new SiameseOCRResult { Success = false, Error = $"OCR server closed unexpectedly.\n\n{details}" };
                        }
                        var result = JsonConvert.DeserializeObject<SiameseOCRResult>(responseLine);
                        result.Success = string.IsNullOrEmpty(result.Error);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        return new SiameseOCRResult { Success = false, Error = $"C# pipe exception: {ex.Message}" };
                    }
                }
            });
        }

        private void RestartOcrServer()
        {
            try { if (ocrServerProcess != null && !ocrServerProcess.HasExited) ocrServerProcess.Kill(); } catch { }
            ocrServerProcess = null;
        }










        // ════════════════════════════════════════════════════════════════
        // CANVAS EVENTS
        // ════════════════════════════════════════════════════════════════
        private void AnnotationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!isTrainingMode || e.ChangedButton != MouseButton.Left) return;

            var pt = e.GetPosition(annotationCanvas);
            var src = e.OriginalSource as UIElement;

            if (src != null && IsTemplateElement(src)) return;
            if (src != null && IsChildOfAnnotation(src)) return;
            if (!GetImageRect().Contains(pt)) return;

            // Always allow drawing — old ROI gets replaced in StartDrawingNewRoi
            if (roiRegions.Count == 0 || (roiOverlayPath != null && src == roiOverlayPath))
            {
                StartDrawingNewRoi(pt);
                e.Handled = true;
                return;
            }
        }

        private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isTrainingMode) return;
            var pos = e.GetPosition(annotationCanvas);

            if (isDraggingTpl) { tplLeft = dragStartL + (pos.X - dragStart.X); tplTop = dragStartT + (pos.Y - dragStart.Y); UpdateTemplate(); return; }
            if (isResizingTpl)
            {
                double dx = pos.X - dragStart.X, dy = pos.Y - dragStart.Y;
                if (resizeDir.Contains("R")) templateWidth = Math.Max(10, dragStartW + dx);
                if (resizeDir.Contains("L")) { templateWidth = Math.Max(10, dragStartW - dx); tplLeft = dragStartL + dx; }
                if (resizeDir.Contains("B")) templateHeight = Math.Max(10, dragStartH + dy);
                if (resizeDir.Contains("T")) { templateHeight = Math.Max(10, dragStartH - dy); tplTop = dragStartT + dy; }
                UpdateTemplate(); return;
            }
            if (isDraggingRoi && activeRoi != null)
            {
                activeRoi.Left = roiDragStartL + (pos.X - roiDragStart.X);
                activeRoi.Top = roiDragStartT + (pos.Y - roiDragStart.Y);
                if (activeRoi.Angle == 0)
                    (activeRoi.Left, activeRoi.Top) = ClampToCanvas(activeRoi.Left, activeRoi.Top, activeRoi.Width, activeRoi.Height);
                UpdateRegionVisual(activeRoi);
                return;
            }
            if (isResizingRoi && activeRoi != null)
            {
                double rad = activeRoi.Angle * Math.PI / 180.0;
                double cosA = Math.Cos(rad), sinA = Math.Sin(rad);

                double c0X = roiDragStartL + roiDragStartW / 2.0;
                double c0Y = roiDragStartT + roiDragStartH / 2.0;

                double dx = pos.X - c0X, dy = pos.Y - c0Y;
                double localX = dx * cosA + dy * sinA;
                double localY = -dx * sinA + dy * cosA;

                double l = -roiDragStartW / 2.0, r = roiDragStartW / 2.0;
                double t = -roiDragStartH / 2.0, b = roiDragStartH / 2.0;

                if (roiResizeDir.Contains("R")) r = Math.Max(l + 20, localX);
                if (roiResizeDir.Contains("L")) l = Math.Min(r - 20, localX);
                if (roiResizeDir.Contains("B")) b = Math.Max(t + 20, localY);
                if (roiResizeDir.Contains("T")) t = Math.Min(b - 20, localY);

                activeRoi.Width = r - l; activeRoi.Height = b - t;
                double localCenterX = (l + r) / 2.0, localCenterY = (t + b) / 2.0;
                double newCenterX = c0X + (localCenterX * cosA - localCenterY * sinA);
                double newCenterY = c0Y + (localCenterX * sinA + localCenterY * cosA);

                activeRoi.Left = newCenterX - activeRoi.Width / 2.0;
                activeRoi.Top = newCenterY - activeRoi.Height / 2.0;

                if (activeRoi.Angle == 0)
                    (activeRoi.Left, activeRoi.Top) = ClampToCanvas(activeRoi.Left, activeRoi.Top, activeRoi.Width, activeRoi.Height);

                UpdateRegionVisual(activeRoi);
                return;
            }

            if (isDrawingRoi && roiDrawPreview != null)
            {
                double x = Math.Min(pos.X, roiDrawOrigin.X), y = Math.Min(pos.Y, roiDrawOrigin.Y);
                double w = Math.Abs(pos.X - roiDrawOrigin.X), h = Math.Abs(pos.Y - roiDrawOrigin.Y);
                Canvas.SetLeft(roiDrawPreview, x); Canvas.SetTop(roiDrawPreview, y);
                roiDrawPreview.Width = Math.Max(1, w); roiDrawPreview.Height = Math.Max(1, h);
                return;
            }
            if (isStamping && stampPreview != null)
            {
                double px = pos.X - templateWidth / 2;
                double py = pos.Y - templateHeight / 2;
                (px, py) = ClampToCanvas(px, py, templateWidth, templateHeight);
                Canvas.SetLeft(stampPreview, px); Canvas.SetTop(stampPreview, py);
                return;
            }
            if (movingAnn != null)
            {
                double nl = moveStartLeft + (pos.X - rotDragStart.X);
                double nt = moveStartTop + (pos.Y - rotDragStart.Y);
                (nl, nt) = ClampToCanvas(nl, nt, movingAnn.Bounds.Width, movingAnn.Bounds.Height);
                Canvas.SetLeft(movingAnn.VisualContainer, nl); Canvas.SetTop(movingAnn.VisualContainer, nt);
                movingAnn.Bounds = new WpfRect(nl, nt, movingAnn.Bounds.Width, movingAnn.Bounds.Height);
                return;
            }
            if (rotatingAnn != null)
            {
                double cx = Canvas.GetLeft(rotatingAnn.VisualContainer) + rotatingAnn.Bounds.Width / 2;
                double cy = Canvas.GetTop(rotatingAnn.VisualContainer) + rotatingAnn.Bounds.Height / 2;
                double a0 = Math.Atan2(rotDragStart.Y - cy, rotDragStart.X - cx);
                double a1 = Math.Atan2(pos.Y - cy, pos.X - cx);
                double na = (rotStartAngle + (a1 - a0) * 180.0 / Math.PI + 360) % 360;
                rotatingAnn.Angle = na;
                ((RotateTransform)rotatingAnn.VisualContainer.RenderTransform).Angle = na;
            }
        }

        private void AnnotationCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = isResizingTpl = false;
            tplRect?.ReleaseMouseCapture();
            rotatingAnn = null; movingAnn = null;

            if (isDrawingRoi)
            {
                isDrawingRoi = false;
                annotationCanvas.ReleaseMouseCapture(); Mouse.Capture(null);
                if (roiDrawPreview != null)
                {
                    double w = roiDrawPreview.Width, h = roiDrawPreview.Height;
                    annotationCanvas.Children.Remove(roiDrawPreview); roiDrawPreview = null;
                    if (w > 15 && h > 15)
                    {
                        var pos = e.GetPosition(annotationCanvas);
                        WpfRect img = GetImageRect();
                        double newLeft = Math.Max(img.Left, Math.Min(pos.X, roiDrawOrigin.X));
                        double newTop = Math.Max(img.Top, Math.Min(pos.Y, roiDrawOrigin.Y));
                        double rawRight = Math.Min(img.Right, Math.Max(pos.X, roiDrawOrigin.X));
                        double rawBottom = Math.Min(img.Bottom, Math.Max(pos.Y, roiDrawOrigin.Y));
                        roiRegions.Clear();
                        RemoveAllRoiVisuals();
                        roiRegions.Add(new RoiRegion
                        {
                            Left = newLeft,
                            Top = newTop,
                            Width = rawRight - newLeft,
                            Height = rawBottom - newTop,
                            Angle = 0,
                        });
                        RebuildRoiVisuals();
                        SaveRoisForCurrentImage();
                        tplLeft = 10; tplTop = 10;
                        RemoveTemplateVisuals(); DrawTemplate();
                    }
                }
                return;
            }

            if (isDraggingRoi || isResizingRoi)
            {
                isDraggingRoi = isResizingRoi = false;
                activeRoi = null;
                annotationCanvas.ReleaseMouseCapture(); Mouse.Capture(null);
                SaveRoisForCurrentImage(); return;
            }

            if (isStamping)
            {
                isStamping = false;
                annotationCanvas.ReleaseMouseCapture(); Mouse.Capture(null);
                if (stampPreview != null)
                {
                    double dropX = Canvas.GetLeft(stampPreview) + templateWidth / 2;
                    double dropY = Canvas.GetTop(stampPreview) + templateHeight / 2;
                    annotationCanvas.Children.Remove(stampPreview); stampPreview = null;
                    StampAnnotation(new WpfPoint(dropX, dropY));
                }
                return;
            }

            annotationCanvas.ReleaseMouseCapture(); Mouse.Capture(null);
        }

        // ════════════════════════════════════════════════════════════════
        // TEMPLATE & ANNOTATION LOGIC
        // ════════════════════════════════════════════════════════════════
        private bool IsTemplateElement(object el) => el == tplRect || el == tplLabel;

        private void EnsureTemplate()
        {
            if (tplRect != null) return;
            tplLeft = 10; tplTop = 10; DrawTemplate();
        }

        private void RemoveTemplateVisuals()
        {
            if (tplRect != null) annotationCanvas.Children.Remove(tplRect);
            if (tplLabel != null) annotationCanvas.Children.Remove(tplLabel);
            tplRect = null; tplLabel = null;
        }

        private void DrawTemplate()
        {
            RemoveTemplateVisuals();
            tplRect = new WpfRectangle
            {
                Width = templateWidth,
                Height = templateHeight,
                Stroke = WpfBrushes.Cyan,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(WpfColor.FromArgb(30, 0, 255, 255)),
                Tag = "TPL",
            };
            PutOnCanvas(tplRect, tplLeft, tplTop, Z_TEMPLATE);
            tplRect.MouseLeftButtonDown += TplRect_MouseDown;
            tplRect.MouseMove += TplRect_MouseMove_Cursor;
            tplRect.MouseLeftButtonUp += TplAny_Up;

            tplLabel = new TextBlock
            {
                Text = $"SIZE: {(int)templateWidth}×{(int)templateHeight}",
                Foreground = WpfBrushes.Cyan,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
            };
            PutOnCanvas(tplLabel, tplLeft + 2, tplTop - 14, Z_TEMPLATE_LBL);
        }

        private void TplRect_MouseMove_Cursor(object sender, MouseEventArgs e)
        {
            if (isDraggingTpl || isResizingTpl) return;
            var p = e.GetPosition(tplRect);
            double W = tplRect.ActualWidth, H = tplRect.ActualHeight;
            bool onR = p.X >= W - EDGE_THRESH, onB = p.Y >= H - EDGE_THRESH;
            bool onL = p.X <= EDGE_THRESH, onT = p.Y <= EDGE_THRESH;
            if ((onR && onB) || (onL && onT)) tplRect.Cursor = Cursors.SizeNWSE;
            else if ((onR && onT) || (onL && onB)) tplRect.Cursor = Cursors.SizeNESW;
            else if (onR || onL) tplRect.Cursor = Cursors.SizeWE;
            else if (onB || onT) tplRect.Cursor = Cursors.SizeNS;
            else tplRect.Cursor = Cursors.SizeAll;
        }

        private void TplRect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(tplRect);
            double W = tplRect.ActualWidth, H = tplRect.ActualHeight;
            bool onR = p.X >= W - EDGE_THRESH, onB = p.Y >= H - EDGE_THRESH;
            bool onL = p.X <= EDGE_THRESH, onT = p.Y <= EDGE_THRESH;
            dragStart = e.GetPosition(annotationCanvas);
            dragStartW = templateWidth; dragStartH = templateHeight;
            dragStartL = tplLeft; dragStartT = tplTop;
            if (onR || onB || onL || onT)
            { isResizingTpl = true; resizeDir = $"{(onR ? "R" : "")}{(onL ? "L" : "")}{(onB ? "B" : "")}{(onT ? "T" : "")}"; }
            else isDraggingTpl = true;
            tplRect.CaptureMouse(); e.Handled = true;
        }

        private void TplAny_Up(object sender, MouseButtonEventArgs e)
        {
            isDraggingTpl = isResizingTpl = false;
            (sender as UIElement)?.ReleaseMouseCapture(); Mouse.Capture(null); e.Handled = true;
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
            Canvas.SetLeft(tplLabel, tplLeft + 2); Canvas.SetTop(tplLabel, tplTop - 14);
            tplLabel.Text = $"SIZE: {(int)templateWidth}×{(int)templateHeight}";
        }

        private void StampAnnotation(WpfPoint center)
        {
            double x = center.X - templateWidth / 2.0;
            double y = center.Y - templateHeight / 2.0;
            (x, y) = ClampToCanvas(x, y, templateWidth, templateHeight);
            CreateAnnotationVisual(x, y, templateWidth, templateHeight, 0, "", addToList: false, needsLabel: true);
        }

        private AnnotationData CreateAnnotationVisual(double x, double y, double w, double h, double angle, string existingLabel, bool addToList, bool needsLabel)
        {
            var container = new Canvas
            {
                Width = w,
                Height = h,
                RenderTransformOrigin = new WpfPoint(0.5, 0.5),
                RenderTransform = new RotateTransform(angle),
                Cursor = Cursors.Hand,
                ClipToBounds = false,
            };
            var rect = new WpfRectangle
            {
                Width = w,
                Height = h,
                Stroke = WpfBrushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(WpfColor.FromArgb(30, 0, 255, 0)),
            };
            Canvas.SetLeft(rect, 0); Canvas.SetTop(rect, 0); container.Children.Add(rect);

            var lbl = new TextBlock
            {
                Text = existingLabel,
                Foreground = WpfBrushes.Lime,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Background = new SolidColorBrush(WpfColor.FromArgb(200, 0, 0, 0)),
                Padding = new Thickness(2, 1, 2, 1),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, 2); container.Children.Add(lbl);

            Canvas.SetLeft(container, x); Canvas.SetTop(container, y);
            Panel.SetZIndex(container, Z_ANNOTATION); annotationCanvas.Children.Add(container);

            var ann = new AnnotationData { Label = existingLabel, Bounds = new WpfRect(x, y, w, h), Angle = angle, VisualRect = rect, VisualContainer = container, LabelText = lbl };
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
                bool edge = cp.X < mg || cp.X > ann.Bounds.Width - mg || cp.Y < mg || cp.Y > ann.Bounds.Height - mg;
                rotDragStart = ev.GetPosition(annotationCanvas);
                if (edge) { rotatingAnn = ann; rotStartAngle = ann.Angle; }
                else { movingAnn = ann; moveStartLeft = Canvas.GetLeft(container); moveStartTop = Canvas.GetTop(container); }
                container.CaptureMouse(); ev.Handled = true;
            };
            container.MouseLeftButtonUp += (s, ev) =>
            {
                container.ReleaseMouseCapture(); rotatingAnn = null; movingAnn = null;
                SaveCurrentImageAnnotations(); ev.Handled = true;
            };
            container.MouseMove += (s, ev) =>
            {
                if (rotatingAnn != null || movingAnn != null) return;
                var cp = ev.GetPosition(container);
                const double mg = 14;
                bool edge = cp.X < mg || cp.X > ann.Bounds.Width - mg || cp.Y < mg || cp.Y > ann.Bounds.Height - mg;
                container.Cursor = edge ? Cursors.SizeAll : Cursors.Hand;
            };
            container.MouseRightButtonUp += (s, ev) => { ShowAnnotationContextMenu(ann); ev.Handled = true; };
        }

        private void ShowInlineTextBox(AnnotationData ann)
        {
            if (ann.InlineTextBox != null) { ann.VisualContainer.Children.Remove(ann.InlineTextBox); ann.InlineTextBox = null; }
            double tbW = Math.Min(ann.Bounds.Width - 4, 28);
            double tbH = Math.Min(ann.Bounds.Height - 4, 22);
            var tb = new TextBox
            {
                Width = tbW,
                Height = tbH,
                MaxLength = 1,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.Black,
                Background = WpfBrushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = WpfBrushes.DarkGray,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                IsHitTestVisible = true,
            };
            Canvas.SetLeft(tb, 2); Canvas.SetTop(tb, 2); Panel.SetZIndex(tb, 200);
            ann.VisualContainer.Children.Add(tb); ann.InlineTextBox = tb;
            Dispatcher.InvokeAsync(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);

            void Confirm()
            {
                if (!ann.VisualContainer.Children.Contains(tb)) return;
                ann.VisualContainer.Children.Remove(tb); ann.InlineTextBox = null;
                string label = tb.Text.Trim().ToUpper();
                if (string.IsNullOrEmpty(label))
                {
                    if (string.IsNullOrEmpty(ann.Label)) { annotationCanvas.Children.Remove(ann.VisualContainer); currentImageAnnotations.Remove(ann); }
                    return;
                }
                ann.LabelText.Text = ann.Label = label;
                if (!currentImageAnnotations.Contains(ann)) currentImageAnnotations.Add(ann);
                SaveCurrentImageAnnotations(); UpdateAnnotationStatus();
            }

            tb.KeyDown += (s, ev) => { if (ev.Key == Key.Enter || ev.Key == Key.Return) { Confirm(); ev.Handled = true; } else if (ev.Key == Key.Escape) { ann.VisualContainer.Children.Remove(tb); ann.InlineTextBox = null; annotationCanvas.Children.Remove(ann.VisualContainer); currentImageAnnotations.Remove(ann); ev.Handled = true; } };
            tb.LostFocus += (s, _) => { if (ann.VisualContainer.Children.Contains(tb) && annotationCanvas.Children.Contains(ann.VisualContainer)) Confirm(); };
        }

        private void ShowAnnotationContextMenu(AnnotationData ann)
        {
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "✏️  Edit Label" };
            edit.Click += (s, _) => ShowInlineTextBox(ann);
            var del = new MenuItem { Header = "🗑️  Delete this box" };
            del.Click += (s, _) => { annotationCanvas.Children.Remove(ann.VisualContainer); currentImageAnnotations.Remove(ann); SaveCurrentImageAnnotations(); UpdateMasterAnnotationFile(); UpdateAnnotationStatus(); };
            menu.Items.Add(edit); menu.Items.Add(del); menu.IsOpen = true;
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

        // ════════════════════════════════════════════════════════════════
        // DATA PERSISTENCE
        // ════════════════════════════════════════════════════════════════



        private string GetRoiFilePath(string imgPath) => System.IO.Path.Combine(annotationsFolder, System.IO.Path.GetFileNameWithoutExtension(imgPath) + "_roi.json");
        private void DeleteRoiFile() { if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return; string f = GetRoiFilePath(imageFiles[currentImageIndex]); if (File.Exists(f)) File.Delete(f); }
        private static (int w, int h) GetNativePixelSize(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        private void SaveCurrentImageAnnotations()
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            string imgPath = imageFiles[currentImageIndex];
            string file = GetAnnotationFilePath(imgPath);
            try
            {
                WpfRect imgRect = GetImageRect();
                if (imgRect.Width < 10 || imgRect.Height < 10) { if (File.Exists(file)) File.Delete(file); return; }
                var valid = currentImageAnnotations.Where(a => !string.IsNullOrWhiteSpace(a.Label)).ToList();
                if (valid.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                var list = valid.Select(a => new CharacterAnnotation
                {
                    Label = a.Label,
                    Angle = a.Angle,
                    ImagePath = imgPath,
                    X = (Canvas.GetLeft(a.VisualContainer) - imgRect.Left) / imgRect.Width,
                    Y = (Canvas.GetTop(a.VisualContainer) - imgRect.Top) / imgRect.Height,
                    Width = a.Bounds.Width / imgRect.Width,
                    Height = a.Bounds.Height / imgRect.Height,
                }).ToList();
                File.WriteAllText(file, JsonConvert.SerializeObject(list, Formatting.Indented));
                UpdateMasterAnnotationFile();
            }
            catch (Exception ex) { Debug.WriteLine($"Save: {ex.Message}"); }
        }

        private void LoadAnnotationsForCurrentImage()
        {
            foreach (var ann in currentImageAnnotations.ToList()) { if (ann.VisualContainer != null) annotationCanvas.Children.Remove(ann.VisualContainer); }
            currentImageAnnotations.Clear();

            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count || imgDisplay.Source == null) return;
            string currentImgPath = imageFiles[currentImageIndex];
            string file = GetAnnotationFilePath(currentImgPath);
            if (!File.Exists(file)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(file));
                if (list == null) return;
                string currentFileName = IOPath.GetFileName(currentImgPath);
                list = list.Where(a => string.IsNullOrEmpty(a.ImagePath) || IOPath.GetFileName(a.ImagePath) == currentFileName).ToList();
                if (list.Count == 0) return;

                double cW = annotationCanvas.Width; double cH = annotationCanvas.Height;
                if (cW < 10 || cH < 10) return;

                foreach (var a in list) CreateAnnotationVisual(a.X * cW, a.Y * cH, a.Width * cW, a.Height * cH, a.Angle, a.Label, addToList: true, needsLabel: false);
            }
            catch (Exception ex) { Debug.WriteLine($"Load: {ex.Message}"); }
        }

        private void UpdateMasterAnnotationFile()
        {
            Task.Run(() =>
            {
                try
                {
                    var all = new List<CharacterAnnotation>();
                    foreach (var f in Directory.GetFiles(annotationsFolder, "*.json"))
                    {
                        if (IOPath.GetFileName(f) == "all_annotations.json" || IOPath.GetFileName(f).EndsWith("_roi.json")) continue;
                        try { var l = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(f)); if (l != null) all.AddRange(l); } catch { }
                    }
                    string master = IOPath.Combine(annotationsFolder, "all_annotations.json");
                    if (all.Count > 0) File.WriteAllText(master, JsonConvert.SerializeObject(all, Formatting.Indented));
                    else if (File.Exists(master)) File.Delete(master);

                    Dispatcher.Invoke(UpdateAnnotationStatus);
                }
                catch { }
            });
        }

        private string GetAnnotationFilePath(string imgPath) => IOPath.Combine(annotationsFolder, IOPath.GetFileNameWithoutExtension(imgPath) + ".json");

        private void UpdateAnnotationStatus()
        {
            string master = IOPath.Combine(annotationsFolder, "all_annotations.json");
            if (!File.Exists(master))
            {
                txtAnnotationStatus.Text = " No annotations yet"; txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Red);
                btnStartTraining.IsEnabled = false; if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false; return;
            }
            try
            {
                var a = JsonConvert.DeserializeObject<List<CharacterAnnotation>>(File.ReadAllText(master));
                if (a != null && a.Count > 0)
                {
                    txtAnnotationStatus.Text = $" {a.Count} annotations\n{a.Select(x => x.ImagePath).Distinct().Count()} images | {a.Select(x => x.Label).Distinct().Count()} unique chars";
                    txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Green);
                    btnStartTraining.IsEnabled = true; if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = true;
                }
                else
                {
                    txtAnnotationStatus.Text = " No annotations yet"; txtAnnotationStatus.Foreground = new SolidColorBrush(Colors.Orange);
                    btnStartTraining.IsEnabled = false; if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
                }
            }
            catch { txtAnnotationStatus.Text = " Error reading annotations"; }
        }

        private bool CheckAnnotationsExist() => File.Exists(IOPath.Combine(annotationsFolder, "all_annotations.json"));

        // ════════════════════════════════════════════════════════════════
        // IMAGE DISPLAY & MODES
        // ════════════════════════════════════════════════════════════════
        private void UpdateCanvasSize()
        {
            if (imgDisplay.Source == null || imgDisplay.ActualWidth < 10) return;
            var src = imgDisplay.Source as BitmapSource; if (src == null) return;
            double ia = (double)src.PixelWidth / src.PixelHeight; double ca = imgDisplay.ActualWidth / imgDisplay.ActualHeight;
            double rW, rH;
            if (ia > ca) { rW = imgDisplay.ActualWidth; rH = imgDisplay.ActualWidth / ia; } else { rH = imgDisplay.ActualHeight; rW = imgDisplay.ActualHeight * ia; }
            annotationCanvas.Width = rW; annotationCanvas.Height = rH;
        }

        private void ImgDisplay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (imgDisplay.Source == null) return;
            System.Diagnostics.Debug.WriteLine($"roiRegions.Count = {roiRegions.Count}");
            var src = imgDisplay.Source as BitmapSource; if (src == null) return;
            double cW = imgDisplay.ActualWidth; double cH = imgDisplay.ActualHeight;
            if (cW < 10 || cH < 10) return;
            double ia = (double)src.PixelWidth / src.PixelHeight; double ca = cW / cH;
            double rW, rH;
            if (ia > ca) { rW = cW; rH = cW / ia; } else { rH = cH; rW = cH * ia; }

            double oldW = annotationCanvas.Width; double oldH = annotationCanvas.Height;
            annotationCanvas.Width = rW; annotationCanvas.Height = rH;
            if (oldW > 10 && oldH > 10 && roiRegions.Count > 0)
            {
                double rsx = rW / oldW; double rsy = rH / oldH;
                foreach (var region in roiRegions)
                {
                    region.Left *= rsx;
                    region.Top *= rsy;
                    region.Width *= rsx;
                    region.Height *= rsy;
                    
                }
                RebuildRoiVisuals();
            }
            if (isTrainingMode)
            {
                annotationCanvas.Visibility = Visibility.Visible; DrawTemplate();
                if (oldW > 10 && oldH > 10 && currentImageAnnotations.Count > 0)
                {
                    double sx = rW / oldW; double sy = rH / oldH;
                    foreach (var ann in currentImageAnnotations)
                    {
                        if (ann.VisualContainer == null) continue;
                        double newX = Canvas.GetLeft(ann.VisualContainer) * sx; double newY = Canvas.GetTop(ann.VisualContainer) * sy;
                        double newW = ann.Bounds.Width * sx; double newH = ann.Bounds.Height * sy;
                        Canvas.SetLeft(ann.VisualContainer, newX); Canvas.SetTop(ann.VisualContainer, newY);
                        ann.VisualContainer.Width = newW; ann.VisualContainer.Height = newH;
                        if (ann.VisualRect != null) { ann.VisualRect.Width = newW; ann.VisualRect.Height = newH; }
                        ann.Bounds = new WpfRect(newX, newY, newW, newH);
                    }
                }

                Dispatcher.InvokeAsync(() =>
                {
                    LoadRoisForCurrentImage();
                    annotationCanvas.Visibility = roiRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }, DispatcherPriority.Loaded);
            }
            else
            {
                annotationCanvas.Visibility = roiRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void ShowImage(string path)
        {
            try
            {
                foreach (var ann in currentImageAnnotations.ToList()) { if (ann.VisualContainer != null) annotationCanvas.Children.Remove(ann.VisualContainer); }
                currentImageAnnotations.Clear();
                foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList()) { if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el); }
                txtPlaceholder.Visibility = Visibility.Collapsed;

                imgDisplay.Source = null;

                var bmp = await Task.Run(() =>
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    b.DecodePixelWidth = 1600; // display-only cap; full-res file on disk is untouched for OCR
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    return b;
                });

                // bail if the user already clicked to a different image while this was decoding
                if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count || imageFiles[currentImageIndex] != path) return;

                imgDisplay.Source = bmp; imgDisplay.Visibility = Visibility.Visible;

                await Dispatcher.InvokeAsync(() =>
                {
                    imgDisplay.UpdateLayout(); UpdateCanvasSize();
                    if (isTrainingMode)
                    {
                        annotationCanvas.Visibility = Visibility.Visible;
                        foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList()) if (!IsTemplateElement(el)) annotationCanvas.Children.Remove(el);
                        currentImageAnnotations.Clear(); LoadAnnotationsForCurrentImage(); LoadRoisForCurrentImage(); RemoveTemplateVisuals(); DrawTemplate();
                    }
                    else
                    {
                        annotationCanvas.Visibility = roiRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                        
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
                    imageFiles.Remove(capturedPath); UpdateImageList();
                    if (imageFiles.Count > 0)
                    {
                        currentImageIndex = Math.Min(currentImageIndex, imageFiles.Count - 1);
                        ShowImage(imageFiles[currentImageIndex]); lstImages.SelectedIndex = currentImageIndex;
                    }
                    else
                    {
                        imgDisplay.Source = null; foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList()) annotationCanvas.Children.Remove(el);
                        currentImageAnnotations.Clear(); txtPlaceholder.Visibility = Visibility.Visible;
                    }
                    txtImageCount.Text = $"Images: {imageFiles.Count}";
                };
                menu.Items.Add(removeItem); item.ContextMenu = menu; lstImages.Items.Add(item);
            }
            txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        // ════════════════════════════════════════════════════════════════
        // UI HANDLERS (Buttons, Modes, Loading, Training, Active Learning)
        // ════════════════════════════════════════════════════════════════
        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (rbTrainingMode == null || rbInferenceMode == null) return;
            isTrainingMode = rbTrainingMode.IsChecked == true;
            if (isTrainingMode)
            {
                txtMode.Text = "TRAINING MODE"; txtMode.Foreground = new SolidColorBrush(WpfColor.FromRgb(22, 163, 74));
                pnlInference.Visibility = Visibility.Collapsed; pnlTraining.Visibility = Visibility.Visible;
                btnAnnotate.Visibility = Visibility.Visible; btnStartTraining.Visibility = Visibility.Visible; btnCheckAnnotations.Visibility = Visibility.Visible;
                btnAnnotate.IsEnabled = imageFiles.Count > 0;
                if (imgDisplay.Source != null)
                {
                    annotationCanvas.Visibility = Visibility.Visible;
                    UpdateCanvasSize();
                    LoadAnnotationsForCurrentImage();
                    LoadRoisForCurrentImage();
                    EnableRoiEditing();
                    EnsureTemplate();
                }
                UpdateAnnotationStatus(); if (CheckAnnotationsExist()) btnStartTraining.IsEnabled = true;
                txtStatusBar.Text = $"User: {currentUser} | Mode: Training | Project: {currentProjectName}";
            }
            else
            {
                txtMode.Text = "INFERENCE MODE"; txtMode.Foreground = new SolidColorBrush(WpfColor.FromRgb(184, 134, 11));
                pnlInference.Visibility = Visibility.Visible; pnlTraining.Visibility = Visibility.Collapsed;
                RemoveTemplateVisuals();
                foreach (var ann in currentImageAnnotations.ToList()) if (ann.VisualContainer != null) annotationCanvas.Children.Remove(ann.VisualContainer);
                currentImageAnnotations.Clear();

                LoadRoisForCurrentImage();
                annotationCanvas.Visibility = roiRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                btnAnnotate.IsEnabled = btnStartTraining.IsEnabled = false;
                txtStatusBar.Text = HasTrainedModel() ? $"User: {currentUser} | Mode: Inference (Model Loaded) | Project: {currentProjectName}" : $"User: {currentUser} | Mode: Inference (No Model) | Project: {currentProjectName}";
            }
        }
        private void EnableRoiEditing()
        {
            if (roiOverlayPath != null) roiOverlayPath.IsHitTestVisible = true;
            foreach (var r in roiRegions)
            {
                if (r.RectVisual != null) r.RectVisual.IsHitTestVisible = true;
                if (r.RotHandleVisual != null) r.RotHandleVisual.IsHitTestVisible = true;
            }
            annotationCanvas.IsHitTestVisible = true;
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
            var dialog = new OpenFileDialog { Title = "Pick ANY image in the folder", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif", Multiselect = false };
            if (dialog.ShowDialog() != true) return;
            string folder = IOPath.GetDirectoryName(dialog.FileName); if (string.IsNullOrEmpty(folder)) return;
            string[] ext = { ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif" };
            var files = Directory.GetFiles(folder).Where(f => ext.Contains(IOPath.GetExtension(f).ToLower())).OrderBy(f => f).ToList();
            if (files.Count == 0) { MessageBox.Show("No image files found.", "No Images"); return; }
            imageFiles.Clear(); imageFiles.AddRange(files); UpdateImageList();
            currentImageIndex = 0; ShowImage(imageFiles[0]); lstImages.SelectedIndex = 0;
            if (isTrainingMode) btnAnnotate.IsEnabled = true; txtImageCount.Text = $"Images: {imageFiles.Count}";
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstImages.SelectedItem is ListBoxItem li && li.Tag is string path)
            {
                int newIndex = imageFiles.IndexOf(path); if (newIndex == currentImageIndex) return;
                if (isTrainingMode) { SaveCurrentImageAnnotations(); SaveRoisForCurrentImage(); }
                currentImageIndex = newIndex; ShowImage(path);
            }
        }

        private void Annotate_Click(object sender, RoutedEventArgs e) { }
        private void CheckAnnotations_Click(object sender, RoutedEventArgs e) => UpdateAnnotationStatus();

     
        private void ClearCurrentImageAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (currentImageIndex < 0 || currentImageIndex >= imageFiles.Count) return;
            if (MessageBox.Show($"Clear annotations for {IOPath.GetFileName(imageFiles[currentImageIndex])}?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var ann in currentImageAnnotations.ToList()) annotationCanvas.Children.Remove(ann.VisualContainer);
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList()) if (!IsTemplateElement(el) && !IsRoiElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear();
            string f = GetAnnotationFilePath(imageFiles[currentImageIndex]);
            if (File.Exists(f)) File.Delete(f);
            UpdateMasterAnnotationFile(); UpdateAnnotationStatus();
        }

        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Delete ALL annotations from ALL images?\n\nThis will also clear the trained model.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (Directory.Exists(annotationsFolder)) foreach (var f in Directory.GetFiles(annotationsFolder, "*.json")) File.Delete(f);
            foreach (var el in annotationCanvas.Children.OfType<UIElement>().ToList()) if (!IsTemplateElement(el) && !IsRoiElement(el)) annotationCanvas.Children.Remove(el);
            currentImageAnnotations.Clear(); ClearTrainedModel(); UpdateAnnotationStatus();
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
                foreach (var grp in all.GroupBy(a => a.ImagePath)) File.WriteAllText(GetAnnotationFilePath(grp.Key), JsonConvert.SerializeObject(grp.ToList(), Formatting.Indented));
                if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count) LoadAnnotationsForCurrentImage();
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            if (!HasTrainedModel()) { MessageBox.Show("No trained model found!"); return; }
            var d = new SaveFileDialog { Filter = "Zip Archive|*.zip", FileName = $"{currentProjectName}_model.zip" };
            if (d.ShowDialog() == true) try { if (File.Exists(d.FileName)) File.Delete(d.FileName); System.IO.Compression.ZipFile.CreateFromDirectory(GetTrainedModelDir(), d.FileName); MessageBox.Show("Model saved!", "Success"); } catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
        }

        private void LoadModel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Camera_Check();
                OpenBaslerCamera();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Camera failed: {ex.Message}");
            }
        }

        private void start(object sender, RoutedEventArgs e)
        {
            if (basler_camera == null || !basler_camera.IsOpen)
            {
                MessageBox.Show("Click 'Start' button first to connect the camera!");
                return;
            }

            try
            {
                if (!basler_camera.StreamGrabber.IsGrabbing)
                {
                    basler_camera.StreamGrabber.Start(1, GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
                }

                // Execute software trigger to actually capture
                basler_camera.ExecuteSoftwareTrigger();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Grab failed: {ex.Message}");
            }
        }
        private void Grab_Click(object sender, RoutedEventArgs e)
        {
            if (basler_camera == null || !basler_camera.IsOpen)
            {
                MessageBox.Show("Camera not connected! Click Start first.");
                return;
            }

            try
            {
                if (!basler_camera.StreamGrabber.IsGrabbing)
                {
                    basler_camera.StreamGrabber.Start(1, GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
                }

                basler_camera.ExecuteSoftwareTrigger();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Grab failed: {ex.Message}");
            }
        }
        private async void StartTraining_Click(object sender, RoutedEventArgs e)
        {
            string python = @"C:\Users\Pixtech Workstation\AppData\Local\Programs\Python\Python311\python.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string trainScript = IOPath.Combine(baseDir, "PythonScripts", "train_templates.py");
            string annoFile = IOPath.Combine(annotationsFolder, "all_annotations.json");
            string modelDir = GetTrainedModelDir();
            int epochs = 50, batch = 32;
            if (txtEpochs != null && int.TryParse(txtEpochs.Text, out int e2)) epochs = e2;
            if (txtBatchSize != null && int.TryParse(txtBatchSize.Text, out int b2)) batch = b2;
            if (!File.Exists(trainScript)) { MessageBox.Show("train_last_layer.py not found"); return; }
            if (!File.Exists(annoFile)) { MessageBox.Show("No annotations found!"); return; }
            ClearTrainedModel(); Directory.CreateDirectory(modelDir);
            btnStartTraining.IsEnabled = false; if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = false;
            progressBarTraining.Value = 0; txtTrainingStatus.Text = "Starting...";
            var errs = new System.Text.StringBuilder();

            string pretrainedPath = IOPath.Combine(modelsFolder, "pretrained_base.pth");
            string pretrainedArg = File.Exists(pretrainedPath) ? $"--pretrained \"{pretrainedPath}\"" : "";

            try
            {
                await Task.Run(() =>
                {
                    var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo { FileName = python, Arguments = $"\"{trainScript}\" \"{annoFile}\" \"{modelDir}\" --epochs {epochs} --batch {batch} --augments 80 {pretrainedArg}", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = baseDir, }
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
                                double.TryParse(a.Data.Split(':')[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double acc);
                                progressBarTraining.Value = 100;
                                txtTrainingStatus.Text = $"Done! Val: {acc:F1}%";
                                RestartOcrServer();
                                MessageBox.Show($"Training complete!\nVal accuracy: {acc:F1}%\n\nSwitch to Inference Mode to test.", "Done");
                            }
                        });
                    };
                    proc.ErrorDataReceived += (s, a) => { if (!string.IsNullOrEmpty(a.Data)) errs.AppendLine(a.Data); };
                    proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); proc.WaitForExit();
                    if (proc.ExitCode != 0) Dispatcher.Invoke(() => { txtTrainingStatus.Text = "Failed!"; MessageBox.Show($"Error:\n{errs}", "Training Error"); });
                });
            }
            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            finally { btnStartTraining.IsEnabled = true; if (btnStartTrainingBig != null) btnStartTrainingBig.IsEnabled = true; }
        }
        private Mat currentCameraImage = null;
        private string currentImagePath = null;

        private async void RunInference_Click(object sender, RoutedEventArgs e)
        {
            Mat inputImage;

            string imgPath;
            if (currentImageIndex >= 0 && currentImageIndex < imageFiles.Count)
            {
                imgPath = imageFiles[currentImageIndex];
            }
            else
            {
                txtResult.Text = "Error: No image loaded";
                txtStatus.Text = "Failed!";
                return;
            }

            string modelDir = GetTrainedModelDir();
            string modelDirArg =
                    (Directory.Exists(modelDir) &&
                     File.Exists(IOPath.Combine(modelDir, "model_config.json")))
                     ? modelDir
                     : null;

            string roiCsv = null;


            // Only load file ROI for normal images
            if (imgPath != "CAMERA")
            {
                string roiFile = GetRoiFilePath(imgPath);

                if (File.Exists(roiFile))
                {
                    try
                    {
                        var rd = JsonConvert.DeserializeObject<List<RoiData>>(
                         File.ReadAllText(roiFile))?.FirstOrDefault();
                        
                        if (rd != null)
                        {
                            var (nw, nh) = GetNativePixelSize(imgPath);

                            int rx = (int)(rd.X * nw);
                            int ry = (int)(rd.Y * nh);
                            int rw = (int)(rd.Width * nw);
                            int rh = (int)(rd.Height * nh);

                            if (rw > 10 && rh > 10)
                            {
                                roiCsv = $"{rx},{ry},{rw},{rh},{(int)rd.Angle}";
                            }
                        }

                    }
                    catch
                    {
                    }
                }
            }
            btnRunInference.IsEnabled = false; progressBar.IsIndeterminate = true; txtResult.Text = "Running OCR..."; txtStatus.Text = HasTrainedModel() ? "EasyOCR + Corrections" : "EasyOCR";
            try
            {
                var result = await RunOcrServerRequest( currentCameraImage, imgPath, modelDirArg, roiCsv);

                if (result.Success)
                {
                    lastInferenceResult = result;
                    lastInferenceImagePath = imgPath;
                    txtResult.Text = result.Text; txtConfidence.Text = $"Confidence: {result.Confidence:F1}%";
                    string method = HasTrainedModel() ? "EasyOCR + Corrections" : "EasyOCR";
                    if (!string.IsNullOrEmpty(roiCsv)) method += " (ROI)";
                    if (result.CorrectionsApplied > 0) method += $" ({result.CorrectionsApplied} fixes)";
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

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (lastInferenceResult == null || string.IsNullOrEmpty(lastInferenceImagePath))
            {
                MessageBox.Show("No active inference result to accept.");
                return;
            }

            string operatorText = txtResult.Text.Trim().ToUpper();
            string systemText = lastInferenceResult.OriginalText?.Trim().ToUpper();

            if (operatorText != systemText)
            {
                try
                {
                    string dataLakeDir = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "data_lake", "corrections");
                    Directory.CreateDirectory(dataLakeDir);

                    var correctionRecord = new
                    {
                        ImageFile = lastInferenceImagePath,
                        SystemGuess = systemText,
                        OperatorCorrection = operatorText,
                        Timestamp = DateTime.Now.ToString("o")
                    };

                    string filename = $"correction_{Guid.NewGuid().ToString("N").Substring(0, 8)}.json";
                    string filepath = IOPath.Combine(dataLakeDir, filename);

                    File.WriteAllText(filepath, JsonConvert.SerializeObject(correctionRecord, Formatting.Indented));
                    MessageBox.Show("Correction logged for next training cycle!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to log correction: {ex.Message}");
                }
            }

            lastInferenceResult = null;
            txtResult.Text = "—";
            txtStatus.Text = "Ready";
            txtConfidence.Text = "Confidence: —";
            txtDecision.Text = "—";
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            lastInferenceResult = null;
            txtResult.Text = "—";
            txtStatus.Text = "Ready";
            txtConfidence.Text = "Confidence: —";
            txtDecision.Text = "—";
        }

        private void AuthTimer_Tick(object sender, EventArgs e)
        {
            authTimer.Stop();
            // var w = new ReAuthWindow(currentUser);
            // if (w.ShowDialog() == true && w.IsAuthenticated) authTimer.Start();
            // else Application.Current.Shutdown();
        }

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
        public WpfRect Bounds { get; set; }
        public double Angle { get; set; }
        public WpfRectangle VisualRect { get; set; }
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
        [JsonProperty("original_text")] public string OriginalText { get; set; }
        [JsonProperty("corrections_applied")] public int CorrectionsApplied { get; set; }
    }
    public class RoiData
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int ImgW { get; set; }
        public int ImgH { get; set; }
        public double Angle { get; set; }
    }

    public class RoiRegion
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 6);
        public double Left, Top, Width, Height, Angle;

        [JsonIgnore] public WpfRectangle RectVisual;
        [JsonIgnore] public TextBlock LabelVisual;
        [JsonIgnore] public WpfEllipse RotHandleVisual;
        [JsonIgnore] public WpfLine RotStemVisual;
    }
}