using System.Collections.Concurrent;
using System.Security.Cryptography;
using BimGo.Format;
using Microsoft.Win32;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Materials round: images for material matches. Encoding (longest side capped; PNG when the image has
    /// transparency, so a cutout can use it, else JPEG), entry names from the content (the same image is stored once),
    /// and the Autodesk Material Library's images on this PC (installed with Revit and Revit LT) with small thumbnails
    /// made on a worker thread. Never throws.
    /// </summary>
    internal static class MaterialImages
    {
        private const long JPEG_QUALITY = 85L;

        /// <summary>Image files the pickers take.</summary>
        public const string FILTER = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|All files (*.*)|*.*";

        private static readonly string[] EXTENSIONS = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        /// <summary>
        /// Re-encodes an image for a match: the longest side at most <paramref name="cap"/>, PNG when it has
        /// transparency (or <paramref name="png"/>), else JPEG.
        /// </summary>
        /// <returns>The entry name ("textures/&lt;hash&gt;.jpg|png") and bytes, or null with a reason.</returns>
        public static (string Entry, byte[] Bytes)? Encode(byte[] source, int cap, bool png, out string error)
        {
            error = null;
            try
            {
                using var input = new MemoryStream(source, writable: false);
                using var image = SD.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
                double scale = Math.Min(1.0, (double)cap / Math.Max(image.Width, image.Height));
                int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
                using var resized = new SD.Bitmap(w, h, SDI.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(resized))
                {
                    g.Clear(SD.Color.Transparent);
                    g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                    using var wrap = new SDI.ImageAttributes();
                    wrap.SetWrapMode(SD.Drawing2D.WrapMode.TileFlipXY);
                    g.DrawImage(image, new SD.Rectangle(0, 0, w, h), 0, 0, image.Width, image.Height, SD.GraphicsUnit.Pixel, wrap);
                }
                bool keepPng = png || HasTransparency(resized);
                using var output = new MemoryStream();
                if (keepPng) { resized.Save(output, SDI.ImageFormat.Png); }
                else
                {
                    using var opaque = new SD.Bitmap(w, h, SDI.PixelFormat.Format24bppRgb);
                    using (var g = SD.Graphics.FromImage(opaque))
                    {
                        g.Clear(SD.Color.White);
                        g.DrawImageUnscaled(resized, 0, 0);
                    }
                    SDI.ImageCodecInfo jpeg = SDI.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == SDI.ImageFormat.Jpeg.Guid);
                    using var parameters = new SDI.EncoderParameters(1);
                    parameters.Param[0] = new SDI.EncoderParameter(SDI.Encoder.Quality, JPEG_QUALITY);
                    if (jpeg != null) { opaque.Save(output, jpeg, parameters); }
                    else { opaque.Save(output, SDI.ImageFormat.Jpeg); }
                }
                byte[] bytes = output.ToArray();
                return (EntryName(bytes, keepPng), bytes);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Material image could not be encoded: {ex.Message}");
                return null;
            }
        }

        /// <summary>Reads and encodes an image file (see <see cref="Encode(byte[], int, bool, out string)"/>).</summary>
        public static (string Entry, byte[] Bytes)? EncodeFile(string path, int cap, bool png, out string error)
        {
            try
            {
                return Encode(File.ReadAllBytes(path), cap, png, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>An entry name from an image's content (the same image always gets the same name).</summary>
        public static string EntryName(byte[] bytes, bool png)
        {
            byte[] hash = SHA1.HashData(bytes);
            return BimGoFormat.TEXTURE_FOLDER + "m" + Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + (png ? ".png" : ".jpg");
        }

        /// <summary>True when any pixel is less than fully opaque (every 3rd row and column: enough to find a cutout).</summary>
        private static bool HasTransparency(SD.Bitmap bitmap)
        {
            var rect = new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            SDI.BitmapData data = bitmap.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y += 3)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int i = 3; i < row.Length; i += 12)
                    {
                        if (row[i] < 250) { return true; }
                    }
                }
                return false;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        #region The Autodesk Material Library

        /// <summary>
        /// The Autodesk Material Library's texture folders on this PC (Common Files, and the folders Autodesk's registry
        /// keys name), or none.
        /// </summary>
        public static List<string> AutodeskRoots()
        {
            var roots = new List<string>();
            void Add(string folder)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) { return; }
                    folder = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')).TrimEnd('\\', '/');
                    if (folder.Length > 2 && Directory.Exists(folder) && !roots.Any(r => folder.StartsWith(r, StringComparison.OrdinalIgnoreCase))) { roots.Add(folder); }
                }
                catch (Exception)
                {
                    // Not a usable folder
                }
            }
            foreach (string variable in new[] { "CommonProgramFiles(x86)", "CommonProgramFiles", "CommonProgramW6432" })
            {
                string common = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(common)) { Add(Path.Combine(common, "Autodesk Shared", "Materials", "Textures")); }
            }
            if (OperatingSystem.IsWindows())
            {
                foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        try
                        {
                            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                            foreach (string name in new[] { @"SOFTWARE\Autodesk\ADSKTextureLibrary", @"SOFTWARE\Autodesk\ADSKTextureLibraryNew" })
                            {
                                using RegistryKey key = baseKey.OpenSubKey(name);
                                if (key == null) { continue; }
                                foreach (string sub in key.GetSubKeyNames().Prepend(string.Empty))
                                {
                                    using RegistryKey k = sub.Length == 0 ? key.OpenSubKey(string.Empty) : key.OpenSubKey(sub);
                                    if (k == null) { continue; }
                                    foreach (string valueName in k.GetValueNames())
                                    {
                                        if (k.GetValue(valueName) is string text) { foreach (string part in text.Split(';')) { Add(part); } }
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // A registry view that can't be read: the others still count
                        }
                    }
                }
            }
            return roots;
        }

        /// <summary>Every image under the roots (at most <paramref name="max"/>), sorted by file name. Slow: a worker thread.</summary>
        public static List<string> ListImages(IEnumerable<string> roots, int max = 20000)
        {
            var files = new List<string>();
            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        if (!EXTENSIONS.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) { continue; }
                        files.Add(file);
                        if (files.Count >= max) { break; }
                    }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Autodesk material library {root} could not be listed: {ex.Message}");
                }
            }
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            return files;
        }

        /// <summary>
        /// The bump image the Autodesk library keeps beside a colour image ("….bump.jpg", "…_bump.jpg", "… bump.jpg"),
        /// or null.
        /// </summary>
        public static string AutodeskBumpFor(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path);
                foreach (string suffix in new[] { ".bump", "_bump", " bump", ".BUMP", "-bump" })
                {
                    foreach (string extension in EXTENSIONS)
                    {
                        string candidate = Path.Combine(folder, stem + suffix + extension);
                        if (File.Exists(candidate)) { return candidate; }
                    }
                }
            }
            catch (Exception)
            {
                // No bump then
            }
            return null;
        }

        #endregion

        #region Thumbnails (worker)

        /// <summary>
        /// Makes small JPEG thumbnails of image files on a worker thread: <see cref="Request"/> queues a file once;
        /// <see cref="TryTake"/> hands finished ones to the GL thread.
        /// </summary>
        public sealed class Thumbnailer : IDisposable
        {
            private readonly ConcurrentQueue<string> _requests = new();
            private readonly ConcurrentQueue<(string Path, byte[] Jpeg)> _done = new();
            private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
            private readonly CancellationTokenSource _cancel = new();
            private readonly SemaphoreSlim _signal = new(0);
            private readonly int _size;

            public Thumbnailer(int size)
            {
                _size = size;
                Task.Run(Work);
            }

            /// <summary>Queues a file (once; GL thread).</summary>
            public void Request(string path)
            {
                if (path == null || !_asked.Add(path)) { return; }
                _requests.Enqueue(path);
                _signal.Release();
            }

            /// <summary>A finished thumbnail (JPEG bytes; null when the file couldn't be read).</summary>
            public bool TryTake(out string path, out byte[] jpeg)
            {
                bool got = _done.TryDequeue(out var item);
                path = item.Path;
                jpeg = item.Jpeg;
                return got;
            }

            private async Task Work()
            {
                try
                {
                    while (!_cancel.IsCancellationRequested)
                    {
                        await _signal.WaitAsync(_cancel.Token).ConfigureAwait(false);
                        if (!_requests.TryDequeue(out string path)) { continue; }
                        _done.Enqueue((path, Encode(path)));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Closed
                }
            }

            private byte[] Encode(string path)
            {
                try
                {
                    return EncodeFile(path, _size, png: false, out _)?.Bytes;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                _cancel.Cancel();
                _signal.Release();
            }
        }

        #endregion
    }
}
