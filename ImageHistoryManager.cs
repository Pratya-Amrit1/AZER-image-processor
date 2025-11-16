using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ImageProcessor
{
    public class ImageHistoryState
    {
        public byte[] ImageData { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Blur { get; set; }

        public ImageHistoryState(Bitmap image, string description, float brightness = 0, float contrast = 0, float saturation = 0, float blur = 0)
        {
            ImageData = BitmapToByteArray(image);
            Description = description;
            Timestamp = DateTime.Now;
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
            Blur = blur;
        }

        private byte[] BitmapToByteArray(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        public Bitmap GetBitmap()
        {
            using (MemoryStream stream = new MemoryStream(ImageData))
            {
                return new Bitmap(stream);
            }
        }
    }

    public class ImageHistoryManager
    {
        private List<ImageHistoryState> history;
        private int currentIndex;
        private const int MaxHistorySize = 50;

        public event EventHandler HistoryChanged;

        public ImageHistoryManager()
        {
            history = new List<ImageHistoryState>();
            currentIndex = -1;
        }

        public bool CanUndo => currentIndex > 0;
        public bool CanRedo => currentIndex < history.Count - 1;

        public void AddState(Bitmap image, string description, float brightness = 0, float contrast = 0, float saturation = 0, float blur = 0)
        {
            // Remove any states after current index (when adding new state after undo)
            if (currentIndex < history.Count - 1)
            {
                history.RemoveRange(currentIndex + 1, history.Count - currentIndex - 1);
            }

            // Add new state
            var state = new ImageHistoryState(image, description, brightness, contrast, saturation, blur);
            history.Add(state);
            currentIndex = history.Count - 1;

            // Limit history size
            if (history.Count > MaxHistorySize)
            {
                history.RemoveAt(0);
                currentIndex--;
            }

            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public ImageHistoryState Undo()
        {
            if (CanUndo)
            {
                currentIndex--;
                HistoryChanged?.Invoke(this, EventArgs.Empty);
                return history[currentIndex];
            }
            return null;
        }

        public ImageHistoryState Redo()
        {
            if (CanRedo)
            {
                currentIndex++;
                HistoryChanged?.Invoke(this, EventArgs.Empty);
                return history[currentIndex];
            }
            return null;
        }

        public ImageHistoryState GetCurrentState()
        {
            if (currentIndex >= 0 && currentIndex < history.Count)
            {
                return history[currentIndex];
            }
            return null;
        }

        public void Clear()
        {
            history.Clear();
            currentIndex = -1;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<string> GetHistoryDescriptions()
        {
            var descriptions = new List<string>();
            for (int i = 0; i < history.Count; i++)
            {
                string prefix = i == currentIndex ? "► " : "   ";
                descriptions.Add($"{prefix}{history[i].Description} ({history[i].Timestamp:HH:mm:ss})");
            }
            return descriptions;
        }

        public int CurrentIndex => currentIndex;
        public int HistoryCount => history.Count;
    }
}

