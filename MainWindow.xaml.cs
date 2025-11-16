using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AForge.Video;
using AForge.Video.DirectShow;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Controls;
using System.Threading;

// Create aliases to resolve ambiguity
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using WpfImage = System.Windows.Controls.Image;

namespace ImageProcessor
{
    public partial class MainWindow : Window
    {
        private Bitmap originalImage;
        private Bitmap currentImage;
        private Bitmap previewImage; // Smaller version for real-time preview
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private CameraWindow cameraWindow;
        private OptimizedImageHistoryManager historyManager;
        private bool isApplyingHistoryState = false;
        private bool isProcessing = false;
        private readonly object processingLock = new object();

        // Zoom and pan functionality
        private double zoomFactor = 1.0;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 10.0;
        private const double ZoomStep = 0.1;
        private bool isPanning = false;
        private System.Windows.Point lastPanPoint;

        // Performance settings - OPTIMIZED FOR BETTER PREVIEW
        private const int MAX_PREVIEW_SIZE = 1200; // Increased from 800 for better preview quality
        private DateTime lastSliderUpdate = DateTime.MinValue;
        private const int SLIDER_DEBOUNCE_MS = 100; // Reduced for more responsive UI

        private System.Windows.Threading.DispatcherTimer historyTimer;
        private CancellationTokenSource processingCancellation;

        public MainWindow()
        {
            InitializeComponent();
            InitializeCamera();
            historyManager = new OptimizedImageHistoryManager();
            historyManager.HistoryChanged += HistoryManager_HistoryChanged;
            InitializeHistoryTimer();
        }

        private void InitializeCamera()
        {
            try
            {
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing camera: {ex.Message}", "Camera Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg, *.jpeg, *.png, *.bmp, *.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files (*.*)|*.*",
                Title = "Select an image file"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    LoadImageButton.IsEnabled = false;
                    LoadImageButton.Content = "Loading...";

                    await Task.Run(() =>
                    {
                        originalImage?.Dispose();
                        currentImage?.Dispose();
                        previewImage?.Dispose();

                        originalImage = new Bitmap(openFileDialog.FileName);
                        currentImage = new Bitmap(originalImage);

                        // Create preview image for better quality display
                        if (originalImage.Width > MAX_PREVIEW_SIZE || originalImage.Height > MAX_PREVIEW_SIZE)
                        {
                            previewImage = FastImageProcessor.ResizeImage(originalImage, MAX_PREVIEW_SIZE, MAX_PREVIEW_SIZE);
                        }
                        else
                        {
                            // Use original image if it's already small enough
                            previewImage = new Bitmap(originalImage);
                        }
                    });

                    DisplayImage(currentImage);
                    UpdateImageInfo();
                    ResetSliders();

                    // Clear history and add initial state
                    historyManager.Clear();
                    await Task.Delay(50); // Reduced delay
                    historyManager.AddState(currentImage, "Original image");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading image: {ex.Message}", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    LoadImageButton.IsEnabled = true;
                    LoadImageButton.Content = "📁 Load Image";
                }
            }
        }

        private void CaptureImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (videoDevices == null || videoDevices.Count == 0)
            {
                MessageBox.Show("No camera devices found!", "Camera Error",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (cameraWindow == null)
            {
                cameraWindow = new CameraWindow(videoDevices);
                cameraWindow.ImageCaptured += CameraWindow_ImageCaptured;
            }

            cameraWindow.Show();
        }

        private async void CameraWindow_ImageCaptured(object sender, Bitmap capturedImage)
        {
            try
            {
                await Task.Run(() =>
                {
                    originalImage?.Dispose();
                    currentImage?.Dispose();
                    previewImage?.Dispose();

                    // Create copies to avoid disposal issues
                    originalImage = new Bitmap(capturedImage);
                    currentImage = new Bitmap(capturedImage);

                    // Create preview image for better quality
                    if (originalImage.Width > MAX_PREVIEW_SIZE || originalImage.Height > MAX_PREVIEW_SIZE)
                    {
                        previewImage = FastImageProcessor.ResizeImage(originalImage, MAX_PREVIEW_SIZE, MAX_PREVIEW_SIZE);
                    }
                    else
                    {
                        previewImage = new Bitmap(originalImage);
                    }
                });

                // Update UI on main thread
                await Dispatcher.InvokeAsync(() =>
                {
                    DisplayImage(currentImage);
                    UpdateImageInfo();
                    ResetSliders();

                    // Clear history and add initial state
                    historyManager.Clear();
                    historyManager.AddState(currentImage, "Captured image");

                    // Close camera window after successful capture
                    cameraWindow?.Hide();
                });

                MessageBox.Show("Image captured and ready for editing!", "Success",
                              MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing captured image: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentImage == null)
            {
                MessageBox.Show("No image to save!", "Warning",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "PNG files (*.png)|*.png|JPEG files (*.jpg)|*.jpg|BMP files (*.bmp)|*.bmp",
                Title = "Save processed image",
                DefaultExt = "png"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    SaveImageButton.IsEnabled = false;
                    SaveImageButton.Content = "Saving...";

                    await Task.Run(() =>
                    {
                        ImageFormat format = ImageFormat.Png;
                        string extension = Path.GetExtension(saveFileDialog.FileName).ToLower();

                        switch (extension)
                        {
                            case ".jpg":
                            case ".jpeg":
                                format = ImageFormat.Jpeg;
                                break;
                            case ".bmp":
                                format = ImageFormat.Bmp;
                                break;
                        }

                        currentImage.Save(saveFileDialog.FileName, format);
                    });

                    MessageBox.Show("Image saved successfully!", "Success",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving image: {ex.Message}", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SaveImageButton.IsEnabled = true;
                    SaveImageButton.Content = "💾 Save Image";
                }
            }
        }

        private async void AdjustmentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (originalImage == null || isApplyingHistoryState) return;

            // Cancel any ongoing processing
            processingCancellation?.Cancel();
            processingCancellation = new CancellationTokenSource();

            // Apply adjustments with minimal delay for responsiveness
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, processingCancellation.Token); // Minimal delay

                    if (!processingCancellation.Token.IsCancellationRequested)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            await ApplyAllAdjustmentsAsync(processingCancellation.Token);
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                }
            }, processingCancellation.Token);

            // Restart the history timer for adding to history
            historyTimer.Stop();
            historyTimer.Start();
        }

        private async Task ApplyAllAdjustmentsAsync(CancellationToken cancellationToken = default)
        {
            if (originalImage == null || isApplyingHistoryState) return;

            // Prevent multiple simultaneous processing
            if (isProcessing) return;

            lock (processingLock)
            {
                if (isProcessing) return;
                isProcessing = true;
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();

                // Get current slider values
                float brightness = (float)BrightnessSlider.Value;
                float contrast = (float)ContrastSlider.Value;
                float saturation = (float)SaturationSlider.Value;
                float blur = (float)BlurSlider.Value;

                System.Diagnostics.Debug.WriteLine($"Starting adjustments: B={brightness}, C={contrast}, S={saturation}, Blur={blur}");

                Bitmap processedImage = await Task.Run(() =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return null;

                        // Start with a copy of the original image
                        Bitmap tempImage = new Bitmap(originalImage);

                        // Apply brightness, contrast, and saturation adjustments in one pass
                        if (Math.Abs(brightness) > 0.1 || Math.Abs(contrast) > 0.1 || Math.Abs(saturation) > 0.1)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                tempImage.Dispose();
                                return null;
                            }

                            var adjusted = FastImageProcessor.AdjustBrightnessContrastSaturationFast(tempImage, brightness, contrast, saturation);

                            if (adjusted != null)
                            {
                                tempImage.Dispose();
                                tempImage = adjusted;
                            }
                        }

                        // Apply blur if needed
                        if (blur > 0.1)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                tempImage.Dispose();
                                return null;
                            }

                            var blurred = FastImageProcessor.ApplyGaussianBlurFast(tempImage, (int)Math.Round(blur));
                            if (blurred != null)
                            {
                                tempImage.Dispose();
                                tempImage = blurred;
                            }
                        }

                        return tempImage;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in processing: {ex.Message}");
                        return new Bitmap(originalImage);
                    }
                }, cancellationToken);

                if (processedImage != null && !cancellationToken.IsCancellationRequested)
                {
                    // Safely update current image
                    var oldImage = currentImage;
                    currentImage = processedImage;
                    oldImage?.Dispose();

                    DisplayImage(currentImage);
                    UpdateImageInfo();

                    stopwatch.Stop();
                    System.Diagnostics.Debug.WriteLine($"Processing completed in {stopwatch.ElapsedMilliseconds}ms");
                }
                else if (processedImage != null)
                {
                    // Dispose if cancelled
                    processedImage.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("Processing was cancelled");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ApplyAllAdjustmentsAsync: {ex.Message}");
                MessageBox.Show($"Error applying adjustments: {ex.Message}", "Processing Error",
                       MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                lock (processingLock)
                {
                    isProcessing = false;
                }
            }
        }

        private async void GrayscaleButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentImage == null || isProcessing) return;

            try
            {
                GrayscaleButton.IsEnabled = false;
                GrayscaleButton.Content = "Processing...";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => FastImageProcessor.ConvertToGrayscaleFast(currentImage));
                stopwatch.Stop();

                System.Diagnostics.Debug.WriteLine($"Grayscale completed in {stopwatch.ElapsedMilliseconds}ms");

                if (result != null)
                {
                    currentImage.Dispose();
                    currentImage = result;
                    DisplayImage(currentImage);
                    historyManager.AddState(currentImage, "Grayscale filter");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying grayscale: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GrayscaleButton.IsEnabled = true;
                GrayscaleButton.Content = "🎨 Grayscale";
            }
        }

        private async void SepiaButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentImage == null || isProcessing) return;

            try
            {
                SepiaButton.IsEnabled = false;
                SepiaButton.Content = "Processing...";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => FastImageProcessor.ApplySepiaFast(currentImage));
                stopwatch.Stop();

                System.Diagnostics.Debug.WriteLine($"Sepia completed in {stopwatch.ElapsedMilliseconds}ms");

                if (result != null)
                {
                    currentImage.Dispose();
                    currentImage = result;
                    DisplayImage(currentImage);
                    historyManager.AddState(currentImage, "Sepia filter");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying sepia: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SepiaButton.IsEnabled = true;
                SepiaButton.Content = "🟤 Sepia";
            }
        }

        private async void InvertButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentImage == null || isProcessing) return;

            try
            {
                InvertButton.IsEnabled = false;
                InvertButton.Content = "Processing...";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => FastImageProcessor.InvertColorsFast(currentImage));
                stopwatch.Stop();

                System.Diagnostics.Debug.WriteLine($"Invert completed in {stopwatch.ElapsedMilliseconds}ms");

                if (result != null)
                {
                    currentImage.Dispose();
                    currentImage = result;
                    DisplayImage(currentImage);
                    historyManager.AddState(currentImage, "Invert colors");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error inverting colors: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InvertButton.IsEnabled = true;
                InvertButton.Content = "🔄 Invert Colors";
            }
        }

        private async void EdgeDetectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentImage == null || isProcessing) return;

            try
            {
                EdgeDetectionButton.IsEnabled = false;
                EdgeDetectionButton.Content = "Processing...";

                var stopwatch = Stopwatch.StartNew();
                var result = await Task.Run(() => FastImageProcessor.ApplyEdgeDetectionFast(currentImage));
                stopwatch.Stop();

                System.Diagnostics.Debug.WriteLine($"Edge detection completed in {stopwatch.ElapsedMilliseconds}ms");

                if (result != null)
                {
                    currentImage.Dispose();
                    currentImage = result;
                    DisplayImage(currentImage);
                    historyManager.AddState(currentImage, "Edge detection");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying edge detection: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EdgeDetectionButton.IsEnabled = true;
                EdgeDetectionButton.Content = "📐 Edge Detection";
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (originalImage == null) return;

            try
            {
                // Cancel any ongoing processing
                processingCancellation?.Cancel();

                // Stop any pending history timer
                historyTimer.Stop();

                // Create a fresh copy of the original image
                currentImage?.Dispose();
                currentImage = new Bitmap(originalImage);

                DisplayImage(currentImage);
                ResetSliders();

                // Add to history
                historyManager.AddState(currentImage, "Reset to original", 0, 0, 0, 0);

                System.Diagnostics.Debug.WriteLine("Image reset successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error resetting image: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Reset error: {ex.Message}");
            }
        }

        private void ResetSliders()
        {
            isApplyingHistoryState = true;
            try
            {
                BrightnessSlider.Value = 0;
                ContrastSlider.Value = 0;
                SaturationSlider.Value = 0;
                BlurSlider.Value = 0;
            }
            finally
            {
                isApplyingHistoryState = false;
            }
        }

        // Zoom and Pan functionality
        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            Zoom(zoomFactor + ZoomStep);
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            Zoom(zoomFactor - ZoomStep);
        }

        private void FitToWindowButton_Click(object sender, RoutedEventArgs e)
        {
            FitToWindow();
        }

        private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
        {
            ActualSize();
        }

        private void Zoom(double newZoom)
        {
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));

            if (Math.Abs(newZoom - zoomFactor) < 0.001) return;

            zoomFactor = newZoom;
            ImageScaleTransform.ScaleX = zoomFactor;
            ImageScaleTransform.ScaleY = zoomFactor;

            UpdateZoomDisplay();
        }

        private void FitToWindow()
        {
            if (PreviewImage.Source == null) return;

            try
            {
                var imageSource = PreviewImage.Source as BitmapSource;
                if (imageSource == null) return;

                double imageWidth = imageSource.PixelWidth;
                double imageHeight = imageSource.PixelHeight;

                double containerWidth = ImageScrollViewer.ActualWidth;
                double containerHeight = ImageScrollViewer.ActualHeight;

                if (containerWidth <= 0 || containerHeight <= 0) return;

                // Calculate scale to fit with better margins
                double scaleX = containerWidth / imageWidth;
                double scaleY = containerHeight / imageHeight;
                double scale = Math.Min(scaleX, scaleY) * 0.95; // Increased from 0.9 to 0.95 for larger display

                // Ensure scale is within bounds
                scale = Math.Max(MinZoom, Math.Min(MaxZoom, scale));

                Zoom(scale);
                CenterImage();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fitting to window: {ex.Message}");
            }
        }

        private void ActualSize()
        {
            Zoom(1.0);
            CenterImage();
        }

        private void CenterImage()
        {
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
            ImageScrollViewer.ScrollToHorizontalOffset(0);
            ImageScrollViewer.ScrollToVerticalOffset(0);
        }

        private void UpdateZoomDisplay()
        {
            try
            {
                ZoomLevelText.Text = $"{zoomFactor * 100:F0}%";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating zoom display: {ex.Message}");
            }
        }

        // Mouse event handlers for zoom and pan
        private void ImageScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
                Zoom(zoomFactor + delta);
                e.Handled = true;
            }
        }

        private void ImageScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (zoomFactor > 1.0)
            {
                isPanning = true;
                lastPanPoint = e.GetPosition(ImageScrollViewer);
                ImageScrollViewer.CaptureMouse();
                ImageScrollViewer.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        private void ImageScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isPanning)
            {
                isPanning = false;
                ImageScrollViewer.ReleaseMouseCapture();
                ImageScrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        private void ImageScrollViewer_MouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(ImageScrollViewer);
                var deltaX = currentPoint.X - lastPanPoint.X;
                var deltaY = currentPoint.Y - lastPanPoint.Y;

                ImageScrollViewer.ScrollToHorizontalOffset(ImageScrollViewer.HorizontalOffset - deltaX);
                ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.VerticalOffset - deltaY);

                lastPanPoint = currentPoint;
                e.Handled = true;
            }
        }

        private void ImageScrollViewer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isPanning)
            {
                isPanning = false;
                ImageScrollViewer.ReleaseMouseCapture();
                ImageScrollViewer.Cursor = Cursors.Arrow;
            }
        }

        private void DisplayImage(Bitmap bitmap)
        {
            if (bitmap == null) return;

            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    memory.Position = 0;

                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = memory;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    PreviewImage.Source = bitmapImage;

                    // Auto-fit when first loading an image
                    if (zoomFactor == 1.0)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            FitToWindow();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error displaying image: {ex.Message}");
            }
        }

        private void UpdateImageInfo()
        {
            if (currentImage == null) return;

            try
            {
                ImageSizeText.Text = $"Size: {currentImage.Width} x {currentImage.Height}";
                ImageFormatText.Text = $"Format: {currentImage.RawFormat}";

                using (MemoryStream ms = new MemoryStream())
                {
                    currentImage.Save(ms, ImageFormat.Png);
                    FileSizeText.Text = $"File Size: {ms.Length / 1024} KB";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating image info: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup resources
            processingCancellation?.Cancel();
            originalImage?.Dispose();
            currentImage?.Dispose();
            previewImage?.Dispose();
            cameraWindow?.Close();
            base.OnClosed(e);
        }

        private void HistoryManager_HistoryChanged(object sender, EventArgs e)
        {
            try
            {
                UndoButton.IsEnabled = historyManager.CanUndo;
                RedoButton.IsEnabled = historyManager.CanRedo;
                UpdateHistoryDisplay();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in HistoryManager_HistoryChanged: {ex.Message}");
            }
        }

        private async void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!historyManager.CanUndo || isProcessing || isApplyingHistoryState) return;

            try
            {
                // Cancel any ongoing processing
                processingCancellation?.Cancel();

                UndoButton.IsEnabled = false;
                UndoButton.Content = "Undoing...";

                var state = historyManager.Undo();
                if (state != null)
                {
                    await ApplyHistoryStateAsync(state);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during undo: {ex.Message}", "Undo Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UndoButton.IsEnabled = historyManager.CanUndo;
                UndoButton.Content = "↶ Undo";
            }
        }

        private async void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!historyManager.CanRedo || isProcessing || isApplyingHistoryState) return;

            try
            {
                // Cancel any ongoing processing
                processingCancellation?.Cancel();

                RedoButton.IsEnabled = false;
                RedoButton.Content = "Redoing...";

                var state = historyManager.Redo();
                if (state != null)
                {
                    await ApplyHistoryStateAsync(state);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during redo: {ex.Message}", "Redo Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RedoButton.IsEnabled = historyManager.CanRedo;
                RedoButton.Content = "↷ Redo";
            }
        }

        private async Task ApplyHistoryStateAsync(OptimizedImageHistoryState state)
        {
            if (state == null) return;

            isApplyingHistoryState = true;

            try
            {
                // Get bitmap from history state
                Bitmap restoredBitmap = await Task.Run(() => state.GetBitmap());

                if (restoredBitmap == null)
                {
                    throw new InvalidOperationException("Failed to restore image from history state");
                }

                // Update the original image to match the restored state
                var oldOriginal = originalImage;
                originalImage = new Bitmap(restoredBitmap);
                oldOriginal?.Dispose();

                // Dispose current image safely
                var oldImage = currentImage;
                currentImage = new Bitmap(restoredBitmap);
                oldImage?.Dispose();

                // Update preview image if needed
                if (previewImage != null)
                {
                    var oldPreview = previewImage;
                    if (currentImage.Width > MAX_PREVIEW_SIZE || currentImage.Height > MAX_PREVIEW_SIZE)
                    {
                        previewImage = await Task.Run(() =>
                            FastImageProcessor.ResizeImage(currentImage, MAX_PREVIEW_SIZE, MAX_PREVIEW_SIZE));
                    }
                    else
                    {
                        previewImage = new Bitmap(currentImage);
                    }
                    oldPreview?.Dispose();
                }

                // Update UI
                DisplayImage(currentImage);

                // Update sliders to match the state
                BrightnessSlider.Value = state.Brightness;
                ContrastSlider.Value = state.Contrast;
                SaturationSlider.Value = state.Saturation;
                BlurSlider.Value = state.Blur;

                UpdateImageInfo();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying history state: {ex.Message}");
                MessageBox.Show($"Error restoring image state: {ex.Message}", "History Error",
                      MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                isApplyingHistoryState = false;
            }
        }

        private string GetCurrentAdjustmentDescription()
        {
            var adjustments = new List<string>();

            if (Math.Abs(BrightnessSlider.Value) > 0.1)
                adjustments.Add($"Brightness: {BrightnessSlider.Value:F0}");
            if (Math.Abs(ContrastSlider.Value) > 0.1)
                adjustments.Add($"Contrast: {ContrastSlider.Value:F0}");
            if (Math.Abs(SaturationSlider.Value) > 0.1)
                adjustments.Add($"Saturation: {SaturationSlider.Value:F0}");
            if (Math.Abs(BlurSlider.Value) > 0.1)
                adjustments.Add($"Blur: {BlurSlider.Value:F0}");

            return adjustments.Count > 0 ? string.Join(", ", adjustments) : "Adjustment";
        }

        private void UpdateHistoryDisplay()
        {
            try
            {
                HistoryListBox.Items.Clear();
                var descriptions = historyManager.GetHistoryDescriptions();

                foreach (var description in descriptions)
                {
                    HistoryListBox.Items.Add(description);
                }

                if (historyManager.CurrentIndex >= 0 && historyManager.CurrentIndex < HistoryListBox.Items.Count)
                {
                    HistoryListBox.ScrollIntoView(HistoryListBox.Items[historyManager.CurrentIndex]);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating history display: {ex.Message}");
            }
        }

        private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        // Ctrl+Shift+Z for Redo
                        if (historyManager.CanRedo && !isProcessing && !isApplyingHistoryState)
                        {
                            RedoButton_Click(sender, null);
                        }
                    }
                    else
                    {
                        // Ctrl+Z for Undo
                        if (historyManager.CanUndo && !isProcessing && !isApplyingHistoryState)
                        {
                            UndoButton_Click(sender, null);
                        }
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Ctrl+Y for Redo
                    if (historyManager.CanRedo && !isProcessing && !isApplyingHistoryState)
                    {
                        RedoButton_Click(sender, null);
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling keyboard shortcut: {ex.Message}");
            }
        }

        private async void HistoryListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryListBox.SelectedIndex >= 0 && !isApplyingHistoryState && !isProcessing)
            {
                int targetIndex = HistoryListBox.SelectedIndex;
                int attempts = 0;
                const int maxAttempts = 20;

                try
                {
                    while (historyManager.CurrentIndex != targetIndex && attempts < maxAttempts)
                    {
                        attempts++;

                        if (historyManager.CurrentIndex < targetIndex)
                        {
                            var state = historyManager.Redo();
                            if (state != null)
                            {
                                await ApplyHistoryStateAsync(state);
                            }
                            else break;
                        }
                        else
                        {
                            var state = historyManager.Undo();
                            if (state != null)
                            {
                                await ApplyHistoryStateAsync(state);
                            }
                            else break;
                        }
                    }

                    if (attempts >= maxAttempts)
                    {
                        System.Diagnostics.Debug.WriteLine("History navigation exceeded maximum attempts");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error navigating history: {ex.Message}", "History Error",
                          MessageBoxButton.OK, MessageBoxImage.Warning);

                    // Reset selection to current state
                    HistoryListBox.SelectedIndex = historyManager.CurrentIndex;
                }
            }
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AboutWindow aboutWindow = new AboutWindow();
                aboutWindow.Owner = this;
                aboutWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening About dialog: {ex.Message}", "Error",
                      MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Handle window size changes
            SizeChanged += (s, args) =>
            {
                if (currentImage != null && args.NewSize.Width > 0 && args.NewSize.Height > 0)
                {
                    // Small delay to ensure layout is complete
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FitToWindow();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            };
        }

        private void InitializeHistoryTimer()
        {
            historyTimer = new System.Windows.Threading.DispatcherTimer();
            historyTimer.Interval = TimeSpan.FromMilliseconds(500); // Reduced for responsiveness
            historyTimer.Tick += HistoryTimer_Tick;
        }

        private void HistoryTimer_Tick(object sender, EventArgs e)
        {
            historyTimer.Stop();

            if (currentImage != null && !isApplyingHistoryState)
            {
                try
                {
                    string description = GetCurrentAdjustmentDescription();
                    // Create a copy of the current processed image for history
                    Bitmap historyImage = new Bitmap(currentImage);
                    historyManager.AddState(historyImage, description,
                        (float)BrightnessSlider.Value, (float)ContrastSlider.Value,
                        (float)SaturationSlider.Value, (float)BlurSlider.Value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error adding to history: {ex.Message}");
                }
            }
        }
    }
}
