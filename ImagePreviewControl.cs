using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Create aliases to resolve ambiguity
using WpfImage = System.Windows.Controls.Image;
using DrawingImage = System.Drawing.Image;

namespace ImageProcessor
{
    public class ImagePreviewControl : UserControl
    {
        private ScrollViewer scrollViewer;
        private WpfImage imageControl;
        private ScaleTransform scaleTransform;
        private TranslateTransform translateTransform;
        private TransformGroup transformGroup;

        private double zoomFactor = 1.0;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 10.0;
        private const double ZoomStep = 0.1;

        private bool isPanning = false;
        private System.Windows.Point lastPanPoint;

        public ImagePreviewControl()
        {
            InitializeControl();
        }

        private void InitializeControl()
        {
            // Create the scroll viewer
            scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Colors.Transparent)
            };

            // Create the image control (WPF Image control)
            imageControl = new WpfImage
            {
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Set up transforms
            scaleTransform = new ScaleTransform();
            translateTransform = new TranslateTransform();
            transformGroup = new TransformGroup();
            transformGroup.Children.Add(scaleTransform);
            transformGroup.Children.Add(translateTransform);
            imageControl.RenderTransform = transformGroup;

            // Add image to scroll viewer
            scrollViewer.Content = imageControl;

            // Set as content
            Content = scrollViewer;

            // Wire up events
            scrollViewer.MouseWheel += OnMouseWheel;
            scrollViewer.MouseLeftButtonDown += OnMouseLeftButtonDown;
            scrollViewer.MouseLeftButtonUp += OnMouseLeftButtonUp;
            scrollViewer.MouseMove += OnMouseMove;
            scrollViewer.MouseLeave += OnMouseLeave;
        }

        public void SetImage(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                imageControl.Source = null;
                return;
            }

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

                    imageControl.Source = bitmapImage;

                    // Auto-fit the image when first loaded
                    FitToWindow();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting image: {ex.Message}");
            }
        }

        public void ZoomIn()
        {
            Zoom(zoomFactor + ZoomStep);
        }

        public void ZoomOut()
        {
            Zoom(zoomFactor - ZoomStep);
        }

        public void FitToWindow()
        {
            if (imageControl.Source == null) return;

            try
            {
                var imageSource = imageControl.Source as BitmapSource;
                if (imageSource == null) return;

                double imageWidth = imageSource.PixelWidth;
                double imageHeight = imageSource.PixelHeight;

                double containerWidth = scrollViewer.ActualWidth;
                double containerHeight = scrollViewer.ActualHeight;

                if (containerWidth <= 0 || containerHeight <= 0) return;

                // Calculate scale to fit
                double scaleX = containerWidth / imageWidth;
                double scaleY = containerHeight / imageHeight;
                double scale = Math.Min(scaleX, scaleY) * 0.9; // 90% to leave some margin

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

        public void ActualSize()
        {
            Zoom(1.0);
            CenterImage();
        }

        private void Zoom(double newZoom)
        {
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));

            if (Math.Abs(newZoom - zoomFactor) < 0.001) return;

            zoomFactor = newZoom;
            scaleTransform.ScaleX = zoomFactor;
            scaleTransform.ScaleY = zoomFactor;
        }

        private void CenterImage()
        {
            translateTransform.X = 0;
            translateTransform.Y = 0;
            scrollViewer.ScrollToHorizontalOffset(0);
            scrollViewer.ScrollToVerticalOffset(0);
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
                Zoom(zoomFactor + delta);
                e.Handled = true;
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (zoomFactor > 1.0)
            {
                isPanning = true;
                lastPanPoint = e.GetPosition(scrollViewer);
                scrollViewer.CaptureMouse();
                scrollViewer.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isPanning)
            {
                isPanning = false;
                scrollViewer.ReleaseMouseCapture();
                scrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(scrollViewer);
                var deltaX = currentPoint.X - lastPanPoint.X;
                var deltaY = currentPoint.Y - lastPanPoint.Y;

                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - deltaX);
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - deltaY);

                lastPanPoint = currentPoint;
                e.Handled = true;
            }
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (isPanning)
            {
                isPanning = false;
                scrollViewer.ReleaseMouseCapture();
                scrollViewer.Cursor = Cursors.Arrow;
            }
        }

        public double ZoomLevel => zoomFactor;
        public string ZoomPercentage => $"{zoomFactor * 100:F0}%";
    }
}
