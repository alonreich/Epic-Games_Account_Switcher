using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>
    /// Lets the user place an uploaded photo inside the round avatar frame (drag to move, wheel/slider/keys to zoom),
    /// with live previews, and produces a square 512x512 JPEG. The photo always covers the whole circle.
    /// </summary>
    public partial class ImageCropWindow : Window
    {
        private const double Viewport = 320;
        private const int OutputSize = 512;
        private const int MaxSourceSide = 2400;
        private const long MaxFileBytes = 40L * 1024 * 1024;
        private const int MinSourceSide = 64;

        private readonly BitmapSource _source;
        private readonly double _coverScale;
        private double _scale, _offsetX, _offsetY;
        private Point _dragStart;
        private double _dragOffsetX, _dragOffsetY;
        private bool _dragging, _syncingSlider;

        public byte[] ResultJpeg { get; private set; }
        public string PhotoName => string.IsNullOrWhiteSpace(NameBox.Text) ? L.T("Photo") : NameBox.Text.Trim();

        private ImageCropWindow(BitmapSource source, string suggestedName)
        {
            InitializeComponent();
            DialogWindowHelper.FitToScreen(this);
            _source = source;
            CropImage.Source = source;
            NameBox.Text = suggestedName;
            _coverScale = Viewport / Math.Min(source.PixelWidth, source.PixelHeight);
            _scale = _coverScale;
            _offsetX = (Viewport - source.PixelWidth * _scale) / 2;
            _offsetY = (Viewport - source.PixelHeight * _scale) / 2;
            ApplyLayout();
            Loaded += (s, e) => CropFrame.Focus();
        }

        /// <summary>Asks for an image file, lets the user crop it and saves it to the avatar collection. Returns the saved path or null.</summary>
        public static string PickCropAndSave(Window owner)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Choose a photo"),
                Filter = L.T("Images") + " (*.jpg, *.png, *.bmp, *.gif, *.tif, *.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(owner) != true) return null;

            BitmapSource source;
            try { source = LoadPhoto(dialog.FileName); }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SKINS] Could not open photo " + dialog.FileName, ex);
                new CustomMessageBox(L.T("Photo Not Supported"), ex is InvalidDataException ? ex.Message : L.T("This file could not be opened as a picture. Try a JPG or PNG photo."), MessageBoxVisualStyle.Error) { Owner = owner }.ShowDialog();
                return null;
            }

            var name = SkinLibrary.DisplayName(dialog.FileName);
            var crop = new ImageCropWindow(source, name) { Owner = owner };
            if (crop.ShowDialog() != true || crop.ResultJpeg == null) return null;
            try
            {
                var saved = SkinLibrary.SaveCustom(crop.ResultJpeg, crop.PhotoName);
                Globals.WriteToLog("[SKINS] Photo added: " + Path.GetFileName(saved));
                return saved;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SKINS] Could not save photo.", ex);
                new CustomMessageBox(L.T("Photo Not Saved"), L.T("The photo could not be saved: {0}", ex.Message), MessageBoxVisualStyle.Error) { Owner = owner }.ShowDialog();
                return null;
            }
        }

        /// <summary>Loads a photo upright (camera rotation applied) and no larger than needed.</summary>
        private static BitmapSource LoadPhoto(string path)
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException(L.T("This photo is larger than 40 MB. Please pick a smaller one."));
            BitmapFrame frame;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];

            BitmapSource result = frame;
            if (Math.Min(frame.PixelWidth, frame.PixelHeight) < MinSourceSide)
                throw new InvalidDataException(L.T("This photo is too small. Please pick one at least {0} pixels wide and high.", MinSourceSide));

            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            var transform = new TransformGroup();
            if (longest > MaxSourceSide) transform.Children.Add(new ScaleTransform((double)MaxSourceSide / longest, (double)MaxSourceSide / longest));
            switch (ReadOrientation(frame))
            {
                case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: transform.Children.Add(new RotateTransform(90)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7: transform.Children.Add(new RotateTransform(270)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
            }
            if (transform.Children.Count > 0) result = new TransformedBitmap(frame, transform);
            result.Freeze();
            return result;
        }

        private static int ReadOrientation(BitmapFrame frame)
        {
            try
            {
                var value = (frame.Metadata as BitmapMetadata)?.GetQuery("System.Photo.Orientation");
                return value == null ? 1 : Convert.ToInt32(value);
            }
            catch { return 1; }
        }

        private void ApplyLayout()
        {
            var width = _source.PixelWidth * _scale;
            var height = _source.PixelHeight * _scale;
            _offsetX = Math.Min(0, Math.Max(Viewport - width, _offsetX));
            _offsetY = Math.Min(0, Math.Max(Viewport - height, _offsetY));
            CropImage.Width = width;
            CropImage.Height = height;
            Canvas.SetLeft(CropImage, _offsetX);
            Canvas.SetTop(CropImage, _offsetY);
        }

        /// <summary>Zooms keeping the photo point under <paramref name="anchor"/> (viewport coordinates) in place.</summary>
        private void ZoomTo(double zoom, Point anchor)
        {
            zoom = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, zoom));
            var newScale = _coverScale * zoom;
            var sourceX = (anchor.X - _offsetX) / _scale;
            var sourceY = (anchor.Y - _offsetY) / _scale;
            _scale = newScale;
            _offsetX = anchor.X - sourceX * _scale;
            _offsetY = anchor.Y - sourceY * _scale;
            ApplyLayout();
            _syncingSlider = true;
            ZoomSlider.Value = zoom;
            _syncingSlider = false;
        }

        private static Point Center => new Point(Viewport / 2, Viewport / 2);

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingSlider || _source == null) return;
            ZoomTo(e.NewValue, Center);
        }

        private void Crop_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomTo(ZoomSlider.Value * Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(CropCanvas));
            e.Handled = true;
        }

        private void Crop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CropFrame.Focus();
            _dragging = CropFrame.CaptureMouse();
            _dragStart = e.GetPosition(CropFrame);
            _dragOffsetX = _offsetX;
            _dragOffsetY = _offsetY;
            e.Handled = true; // the crop area moves the photo, not the window
        }

        private void Crop_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(CropFrame);
            _offsetX = _dragOffsetX + (p.X - _dragStart.X);
            _offsetY = _dragOffsetY + (p.Y - _dragStart.Y);
            ApplyLayout();
        }

        private void Crop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) CropFrame.ReleaseMouseCapture();
            _dragging = false;
        }

        private void Crop_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!CropFrame.IsKeyboardFocused) return;
            const double step = 10;
            switch (e.Key)
            {
                case Key.Left: _offsetX += step; break;
                case Key.Right: _offsetX -= step; break;
                case Key.Up: _offsetY += step; break;
                case Key.Down: _offsetY -= step; break;
                case Key.Add: case Key.OemPlus: ZoomTo(ZoomSlider.Value + 0.2, Center); e.Handled = true; return;
                case Key.Subtract: case Key.OemMinus: ZoomTo(ZoomSlider.Value - 0.2, Center); e.Handled = true; return;
                default: return;
            }
            ApplyLayout();
            e.Handled = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ResultJpeg = RenderCrop();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SKINS] Cropping failed.", ex);
                new CustomMessageBox(L.T("Photo Not Saved"), L.T("The photo could not be cropped: {0}", ex.Message), MessageBoxVisualStyle.Error) { Owner = this }.ShowDialog();
            }
        }

        /// <summary>Encodes the square behind the circle as a 512x512 JPEG.</summary>
        private byte[] RenderCrop()
        {
            // Square that sits behind the circle, in source pixels.
            var size = Viewport / _scale;
            var x = Math.Max(0, -_offsetX / _scale);
            var y = Math.Max(0, -_offsetY / _scale);
            var rect = new Int32Rect((int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Min(Math.Round(size), _source.PixelWidth - Math.Round(x)),
                (int)Math.Min(Math.Round(size), _source.PixelHeight - Math.Round(y)));
            var cropped = new CroppedBitmap(_source, rect);

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, OutputSize, OutputSize)); // transparent PNG areas become black, not random
                dc.DrawImage(cropped, new Rect(0, 0, OutputSize, OutputSize));
            }
            var bitmap = new RenderTargetBitmap(OutputSize, OutputSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Dialog_DragMove(object sender, MouseButtonEventArgs e) => DialogWindowHelper.DragMove(this, e);
    }
}
