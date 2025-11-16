using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Threading;

namespace ImageProcessor
{
    public partial class CameraWindow : Window
    {
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private Bitmap currentFrame;
        private readonly object frameLock = new object();

        public event EventHandler<Bitmap> ImageCaptured;

        public CameraWindow(FilterInfoCollection devices)
        {
            InitializeComponent();
            videoDevices = devices;
            PopulateCameraList();
        }

        private void PopulateCameraList()
        {
            CameraComboBox.Items.Clear();

            if (videoDevices != null)
            {
                foreach (FilterInfo device in videoDevices)
                {
                    CameraComboBox.Items.Add(device.Name);
                }

                if (CameraComboBox.Items.Count > 0)
                {
                    CameraComboBox.SelectedIndex = 0;
                }
            }
        }

        private void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (videoSource != null && videoSource.IsRunning)
            {
                StopCamera();
            }
        }

        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (CameraComboBox.SelectedIndex >= 0)
            {
                StartCamera();
            }
        }

        private void StartCamera()
        {
            try
            {
                if (videoDevices == null || CameraComboBox.SelectedIndex < 0 ||
                    CameraComboBox.SelectedIndex >= videoDevices.Count)
                {
                    MessageBox.Show("Please select a valid camera device.", "Camera Error",
                                  MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                videoSource = new VideoCaptureDevice(videoDevices[CameraComboBox.SelectedIndex].MonikerString);

                // Set video resolution if possible
                if (videoSource.VideoCapabilities.Length > 0)
                {
                    // Try to find a good resolution (640x480 or similar)
                    var capability = videoSource.VideoCapabilities[0];
                    foreach (var cap in videoSource.VideoCapabilities)
                    {
                        if (cap.FrameSize.Width == 640 && cap.FrameSize.Height == 480)
                        {
                            capability = cap;
                            break;
                        }
                    }
                    videoSource.VideoResolution = capability;
                }

                videoSource.NewFrame += VideoSource_NewFrame;
                videoSource.Start();

                StartCameraButton.IsEnabled = false;
                StopCameraButton.IsEnabled = true;
                CaptureButton.IsEnabled = true;

                System.Diagnostics.Debug.WriteLine("Camera started successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting camera: {ex.Message}\n\nPlease check:\n- Camera is not in use by another application\n- Camera drivers are installed\n- Camera permissions are granted",
                              "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);

                StartCameraButton.IsEnabled = true;
                StopCameraButton.IsEnabled = false;
                CaptureButton.IsEnabled = false;
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                // Create a copy of the frame to avoid disposal issues
                lock (frameLock)
                {
                    currentFrame?.Dispose();
                    currentFrame = new Bitmap(eventArgs.Frame);
                }

                // Update UI on main thread
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        lock (frameLock)
                        {
                            if (currentFrame != null)
                            {
                                DisplayFrame(currentFrame);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error displaying frame: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in VideoSource_NewFrame: {ex.Message}");
            }
        }

        private void DisplayFrame(Bitmap frame)
        {
            if (frame == null) return;

            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    // Create a copy to avoid threading issues
                    using (Bitmap frameCopy = new Bitmap(frame))
                    {
                        frameCopy.Save(memory, ImageFormat.Png);
                        memory.Position = 0;

                        BitmapImage bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = memory;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();

                        CameraPreview.Source = bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error displaying frame: {ex.Message}");
            }
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                lock (frameLock)
                {
                    if (currentFrame != null)
                    {
                        // Create a copy of the current frame for capture
                        Bitmap capturedImage = new Bitmap(currentFrame);

                        // Raise the event with the captured image
                        ImageCaptured?.Invoke(this, capturedImage);

                        System.Diagnostics.Debug.WriteLine($"Image captured: {capturedImage.Width}x{capturedImage.Height}");
                    }
                    else
                    {
                        MessageBox.Show("No frame available to capture!\n\nPlease ensure:\n- Camera is started\n- Camera is working properly",
                                      "Capture Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error capturing image: {ex.Message}", "Capture Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StopCamera()
        {
            try
            {
                if (videoSource != null && videoSource.IsRunning)
                {
                    videoSource.SignalToStop();

                    // Wait for the video source to stop (with timeout)
                    // Note: WaitForStop() doesn't return a boolean, so we use a different approach
                    var stopTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            videoSource.WaitForStop();
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    // Wait up to 3 seconds for the camera to stop
                    if (!stopTask.Wait(3000))
                    {
                        System.Diagnostics.Debug.WriteLine("Camera stop timeout, forcing stop");
                    }

                    videoSource.NewFrame -= VideoSource_NewFrame;
                    videoSource = null;
                }

                // Clean up current frame
                lock (frameLock)
                {
                    currentFrame?.Dispose();
                    currentFrame = null;
                }

                StartCameraButton.IsEnabled = true;
                StopCameraButton.IsEnabled = false;
                CaptureButton.IsEnabled = false;
                CameraPreview.Source = null;

                System.Diagnostics.Debug.WriteLine("Camera stopped successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping camera: {ex.Message}");

                // Force cleanup
                videoSource = null;
                StartCameraButton.IsEnabled = true;
                StopCameraButton.IsEnabled = false;
                CaptureButton.IsEnabled = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopCamera();
            base.OnClosed(e);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopCamera();
            base.OnClosing(e);
        }
    }
}
