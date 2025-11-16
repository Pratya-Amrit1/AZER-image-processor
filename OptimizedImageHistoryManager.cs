using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace ImageProcessor
{
    public class OptimizedImageHistoryState
    {
        public byte[] CompressedImageData { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Blur { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        public OptimizedImageHistoryState(Bitmap image, string description, float brightness = 0, float contrast = 0, float saturation = 0, float blur = 0)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            CompressedImageData = CompressBitmap(image);
            Description = description ?? "Unknown operation";
            Timestamp = DateTime.Now;
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
            Blur = blur;
            OriginalWidth = image.Width;
            OriginalHeight = image.Height;
        }

        private byte[] CompressBitmap(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                // Save as JPEG with compression for smaller memory footprint
                bitmap.Save(ms, ImageFormat.Jpeg);
                byte[] imageBytes = ms.ToArray();

                // Further compress with GZip
                using (MemoryStream compressedMs = new MemoryStream())
                {
                    using (GZipStream gzip = new GZipStream(compressedMs, CompressionMode.Compress))
                    {
                        gzip.Write(imageBytes, 0, imageBytes.Length);
                    }
                    return compressedMs.ToArray();
                }
            }
        }

        public async Task<Bitmap> GetBitmapAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    return GetBitmap();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Async bitmap retrieval failed: {ex.Message}");
                    throw;
                }
            });
        }

        public Bitmap GetBitmap()
        {
            try
            {
                // Decompress
                using (MemoryStream compressedMs = new MemoryStream(CompressedImageData))
                using (GZipStream gzip = new GZipStream(compressedMs, CompressionMode.Decompress))
                using (MemoryStream decompressedMs = new MemoryStream())
                {
                    gzip.CopyTo(decompressedMs);
                    decompressedMs.Position = 0;

                    // Create bitmap with proper disposal handling
                    Bitmap bitmap = new Bitmap(decompressedMs);

                    // Verify bitmap is valid
                    if (bitmap.Width <= 0 || bitmap.Height <= 0)
                    {
                        bitmap.Dispose();
                        throw new InvalidOperationException("Invalid bitmap dimensions");
                    }

                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to decompress bitmap: {ex.Message}", ex);
            }
        }
    }

    public class OptimizedImageHistoryManager
    {
        private List<OptimizedImageHistoryState> history;
        private int currentIndex;
        private const int MaxHistorySize = 20; // Reduced for better performance
        private readonly object lockObject = new object();

        public event EventHandler HistoryChanged;

        public OptimizedImageHistoryManager()
        {
            history = new List<OptimizedImageHistoryState>();
            currentIndex = -1;
        }

        public bool CanUndo => currentIndex > 0;
        public bool CanRedo => currentIndex < history.Count - 1;

        public void AddState(Bitmap image, string description, float brightness = 0, float contrast = 0, float saturation = 0, float blur = 0)
        {
            if (image == null) return;

            Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        // Remove any states after current index
                        if (currentIndex < history.Count - 1)
                        {
                            history.RemoveRange(currentIndex + 1, history.Count - currentIndex - 1);
                        }

                        // Add new state
                        var state = new OptimizedImageHistoryState(image, description, brightness, contrast, saturation, blur);
                        history.Add(state);
                        currentIndex = history.Count - 1;

                        // Limit history size
                        if (history.Count > MaxHistorySize)
                        {
                            history.RemoveAt(0);
                            currentIndex--;
                        }
                    }

                    // Notify on UI thread
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        HistoryChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error adding history state: {ex.Message}");
                }
            });
        }

        public OptimizedImageHistoryState Undo()
        {
            lock (lockObject)
            {
                try
                {
                    if (CanUndo)
                    {
                        currentIndex--;
                        var state = history[currentIndex];

                        // Verify state is valid before returning
                        if (state?.CompressedImageData != null && state.CompressedImageData.Length > 0)
                        {
                            HistoryChanged?.Invoke(this, EventArgs.Empty);
                            return state;
                        }
                        else
                        {
                            // Invalid state, try to recover
                            currentIndex++;
                            System.Diagnostics.Debug.WriteLine("Invalid history state encountered during undo");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error during undo: {ex.Message}");
                }

                return null;
            }
        }

        public OptimizedImageHistoryState Redo()
        {
            lock (lockObject)
            {
                try
                {
                    if (CanRedo)
                    {
                        currentIndex++;
                        var state = history[currentIndex];

                        // Verify state is valid before returning
                        if (state?.CompressedImageData != null && state.CompressedImageData.Length > 0)
                        {
                            HistoryChanged?.Invoke(this, EventArgs.Empty);
                            return state;
                        }
                        else
                        {
                            // Invalid state, try to recover
                            currentIndex--;
                            System.Diagnostics.Debug.WriteLine("Invalid history state encountered during redo");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error during redo: {ex.Message}");
                }

                return null;
            }
        }

        public OptimizedImageHistoryState GetCurrentState()
        {
            lock (lockObject)
            {
                if (currentIndex >= 0 && currentIndex < history.Count)
                {
                    return history[currentIndex];
                }
                return null;
            }
        }

        public void Clear()
        {
            lock (lockObject)
            {
                history.Clear();
                currentIndex = -1;
            }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<string> GetHistoryDescriptions()
        {
            lock (lockObject)
            {
                var descriptions = new List<string>();
                for (int i = 0; i < history.Count; i++)
                {
                    string prefix = i == currentIndex ? "► " : "   ";
                    descriptions.Add($"{prefix}{history[i].Description} ({history[i].Timestamp:HH:mm:ss})");
                }
                return descriptions;
            }
        }

        public int CurrentIndex => currentIndex;
        public int HistoryCount => history.Count;
    }
}
