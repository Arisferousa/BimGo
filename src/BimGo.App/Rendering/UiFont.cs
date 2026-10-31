using System.Runtime.InteropServices;
using BimGo.Native;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using SDT = System.Drawing.Text;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// One glyph's atlas rectangle and metrics (pixels).
    /// </summary>
    internal struct Glyph
    {
        public float U0, V0, U1, V1;
        public float Width, Height;
        public float Advance;
        public float OffsetX, OffsetY;
        public bool Valid;
    }

    /// <summary>
    /// A rasterised font inside the shared atlas.
    /// </summary>
    internal sealed class UiFont
    {
        private readonly Glyph[] _latin = new Glyph[256];
        private readonly char[] _extraChars;
        private readonly Glyph[] _extraGlyphs;

        /// <summary>Line height in pixels.</summary>
        public float LineHeight { get; }

        internal UiFont(float lineHeight, Glyph[] latin, char[] extraChars, Glyph[] extraGlyphs)
        {
            LineHeight = lineHeight;
            _latin = latin;
            _extraChars = extraChars;
            _extraGlyphs = extraGlyphs;
        }

        /// <summary>
        /// Gets a glyph ('?' for anything not in the atlas).
        /// </summary>
        public ref readonly Glyph Get(char c)
        {
            if (c < 256)
            {
                if (_latin[c].Valid) { return ref _latin[c]; }
            }
            else
            {
                for (int i = 0; i < _extraChars.Length; i++)
                {
                    if (_extraChars[i] == c) { return ref _extraGlyphs[i]; }
                }
            }
            return ref _latin['?'];
        }
    }

    /// <summary>
    /// Builds the UI font atlas with GDI+ once at startup (white glyphs on transparent, plus a white block for solid fills).
    /// </summary>
    internal sealed unsafe class FontAtlas : IDisposable
    {
        /// <summary>Extra non-Latin-1 characters used by the HUD.</summary>
        private static readonly char[] EXTRA = { 'Δ', '−', '—', '–', '…', '•', '↑', '↓', '←', '→', '“', '”', '’', '≈', '≤', '≥', '‹', '›', '↔' };

        /// <summary>The GL texture.</summary>
        public uint Texture { get; private set; }

        /// <summary>Atlas size in pixels.</summary>
        public int Size { get; private set; }

        /// <summary>UI round: the corner disc and ring (DISC_SIZE px each, at the top of the atlas, after the white block).</summary>
        private const int DISC_X = 6, RING_X = DISC_X + DISC_SIZE + 2, DISC_SIZE = 32;

        /// <summary>The ring's line width (px, at DISC_SIZE): about one pixel at the corner radii the UI uses.</summary>
        private const float RING_WIDTH = 2f;

        /// <summary>UI round: U of the disc's and the ring's left edge (V is 0 for both).</summary>
        public float DiscU { get; private set; }

        /// <summary>UI round: U of the ring's left edge.</summary>
        public float RingU { get; private set; }

        /// <summary>UI round: the disc's (and the ring's) size in texture coordinates.</summary>
        public float CornerSize { get; private set; }

        /// <summary>UV of the solid white block.</summary>
        public float WhiteU { get; private set; }

        /// <summary>UV of the solid white block.</summary>
        public float WhiteV { get; private set; }

        /// <summary>Small caps labels (11 px).</summary>
        public UiFont Small { get; private set; }

        /// <summary>Body text (14 px).</summary>
        public UiFont Body { get; private set; }

        /// <summary>Emphasised body (16 px semibold).</summary>
        public UiFont Bold { get; private set; }

        /// <summary>Large title (40 px bold).</summary>
        public UiFont Title { get; private set; }

        /// <summary>Monospace readouts (13 px).</summary>
        public UiFont Mono { get; private set; }

        /// <summary>Large monospace readouts (26 px).</summary>
        public UiFont MonoLarge { get; private set; }

        /// <summary>
        /// Rasterises all fonts and uploads the atlas.
        /// </summary>
        /// <param name="scale">UI scale (DPI / 96).</param>
        public void Build(float scale)
        {
            // Smallest atlas that holds every font with its padding: a dry run of the packer measures each size
            Size = scale <= 1.01f ? 1024 : scale <= 2.01f ? 2048 : 4096;
            using (var probe = new SD.Bitmap(1, 1, SDI.PixelFormat.Format32bppArgb))
            using (SD.Graphics measure = SD.Graphics.FromImage(probe))
            {
                measure.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                while (Size < 8192 && !RasteriseAll(measure, scale, false)) { Size *= 2; }
            }

            using var bitmap = new SD.Bitmap(Size, Size, SDI.PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
            {
                g.Clear(SD.Color.Transparent);
                g.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(SD.Brushes.White, 0, 0, 4, 4);
                WhiteU = 2f / Size;
                WhiteV = 2f / Size;

                // UI round: an anti-aliased disc and ring, whose quarters are rounded corners (filled and outlined)
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                g.FillEllipse(SD.Brushes.White, DISC_X, 0, DISC_SIZE, DISC_SIZE);
                using (var pen = new SD.Pen(SD.Color.White, RING_WIDTH))
                {
                    float inset = RING_WIDTH * 0.5f;
                    g.DrawEllipse(pen, RING_X + inset, inset, DISC_SIZE - RING_WIDTH, DISC_SIZE - RING_WIDTH);
                }
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
                DiscU = DISC_X / (float)Size;
                RingU = RING_X / (float)Size;
                CornerSize = DISC_SIZE / (float)Size;
                RasteriseAll(g, scale, true);
            }

            Upload(bitmap);
        }

        /// <summary>
        /// Packs (and, with <paramref name="draw"/>, draws) every font.
        /// </summary>
        /// <returns>True if every glyph fitted in the atlas.</returns>
        private bool RasteriseAll(SD.Graphics g, float scale, bool draw)
        {
            // UI round: the fonts and text size chosen (Esc → INTERFACE); Segoe UI / Courier New stand in when a face is missing
            (string textName, string numbersName, float textSize) = UiFonts.Current;
            UiFonts.Faces text = UiFonts.Resolve(textName, numbers: false), numbers = UiFonts.Resolve(numbersName, numbers: true);
            float t = scale * Math.Clamp(textSize, UiFonts.MIN_TEXT_SIZE, UiFonts.MAX_TEXT_SIZE);
            var segoe = ("Segoe UI", SD.FontStyle.Regular);
            var segoeSemi = ("Segoe UI Semibold", SD.FontStyle.Regular);
            var courier = ("Courier New", SD.FontStyle.Regular);

            var packer = new Packer { X = RING_X + DISC_SIZE + 2, Y = 0, RowHeight = DISC_SIZE + 1, Fits = true }; // after the white block, disc and ring
            Small = Rasterise(g, ref packer, 11f * t, draw, text.SemiBold, segoeSemi);
            Body = Rasterise(g, ref packer, 14f * t, draw, text.Regular, segoe);
            Bold = Rasterise(g, ref packer, 16f * t, draw, text.SemiBold, segoeSemi);
            Title = Rasterise(g, ref packer, 40f * t, draw, text.Bold, ("Segoe UI", SD.FontStyle.Bold));
            Mono = Rasterise(g, ref packer, 13f * t, draw, numbers.Regular, courier);
            MonoLarge = Rasterise(g, ref packer, 26f * t, draw, numbers.Bold, ("Courier New", SD.FontStyle.Bold));
            return packer.Fits;
        }

        /// <summary>
        /// Shelf packer state.
        /// </summary>
        private struct Packer
        {
            public int X, Y, RowHeight;
            public bool Fits;
        }

        /// <summary>
        /// Rasterises one font into the atlas.
        /// </summary>
        /// <remarks>
        /// Each glyph gets a padded cell and is clipped to it. Accents on capitals (É, Å) and tails reach past the
        /// font's line box; unclipped they spilled into the row above and showed as small white marks under other text.
        /// </remarks>
        private UiFont Rasterise(SD.Graphics g, ref Packer packer, float pixelSize, bool draw, params (string Family, SD.FontStyle Style)[] faces)
        {
            using SD.Font font = UiFonts.Create(pixelSize, faces);
            using var format = (SD.StringFormat)SD.StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= SD.StringFormatFlags.MeasureTrailingSpaces | SD.StringFormatFlags.NoClip;

            float lineHeight = MathF.Ceiling(font.GetHeight(g));
            int padTop = (int)MathF.Ceiling(pixelSize * 0.25f) + 1;
            int padBottom = (int)MathF.Ceiling(pixelSize * 0.15f) + 1;
            int padSide = (int)MathF.Ceiling(pixelSize * 0.1f) + 2;
            int cellHeight = (int)lineHeight + padTop + padBottom;
            var latin = new Glyph[256];
            var extra = new Glyph[EXTRA.Length];

            Glyph Add(char c, ref Packer p)
            {
                string text = c.ToString();
                float advance = g.MeasureString(text, font, SD.PointF.Empty, format).Width;
                int cellWidth = (int)MathF.Ceiling(advance) + 2 * padSide;

                if (p.X + cellWidth >= Size) { p.X = 0; p.Y += p.RowHeight + 1; p.RowHeight = 0; }
                if (p.Y + cellHeight >= Size) { p.Fits = false; return default; }

                if (draw)
                {
                    g.SetClip(new SD.Rectangle(p.X, p.Y, cellWidth, cellHeight));
                    g.DrawString(text, font, SD.Brushes.White, p.X + padSide, p.Y + padTop, format);
                    g.ResetClip();
                }
                var glyph = new Glyph
                {
                    U0 = (float)p.X / Size,
                    V0 = (float)p.Y / Size,
                    U1 = (float)(p.X + cellWidth) / Size,
                    V1 = (float)(p.Y + cellHeight) / Size,
                    Width = cellWidth,
                    Height = cellHeight,
                    Advance = advance,
                    OffsetX = -padSide,
                    OffsetY = -padTop,
                    Valid = true
                };
                p.X += cellWidth + 1;
                p.RowHeight = Math.Max(p.RowHeight, cellHeight);
                return glyph;
            }

            for (int c = 32; c < 256; c++)
            {
                if (c >= 127 && c < 160) { continue; }
                latin[c] = Add((char)c, ref packer);
            }
            for (int i = 0; i < EXTRA.Length; i++)
            {
                extra[i] = Add(EXTRA[i], ref packer);
            }

            // Next font starts on a fresh row
            packer.X = 0;
            packer.Y += packer.RowHeight + 2;
            packer.RowHeight = 0;

            return new UiFont(lineHeight, latin, EXTRA, extra);
        }

        /// <summary>
        /// Converts to white-with-alpha RGBA and uploads.
        /// </summary>
        private void Upload(SD.Bitmap bitmap)
        {
            var rect = new SD.Rectangle(0, 0, Size, Size);
            SDI.BitmapData data = bitmap.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[Size * Size * 4];
            try
            {
                for (int y = 0; y < Size; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * Size * 4, Size * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            // BGRA in memory: force white RGB, keep alpha
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
            }

            Texture = Gl.GenTexture();
            Gl.BindTexture(Gl.TEXTURE_2D, Texture);
            Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
            fixed (byte* p = pixels)
            {
                Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, Size, Size, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
            }
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
        }

        /// <summary>
        /// Deletes the texture.
        /// </summary>
        public void Dispose()
        {
            Gl.DeleteTexture(Texture);
            Texture = 0;
        }
    }
}
