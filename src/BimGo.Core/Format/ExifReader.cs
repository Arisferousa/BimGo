using System.Globalization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Site photos round: what a photo's EXIF block says about where and how it was taken (each value null when the photo
    /// doesn't record it).
    /// </summary>
    public sealed class ExifInfo
    {
        /// <summary>EXIF orientation 1–8 (1 = upright; 6 = turn 90° clockwise to view, as phones held upright write).</summary>
        public int Orientation { get; set; } = 1;

        /// <summary>WGS84 latitude (degrees, north positive).</summary>
        public double? Latitude { get; set; }

        /// <summary>WGS84 longitude (degrees, east positive).</summary>
        public double? Longitude { get; set; }

        /// <summary>Altitude above sea level (m).</summary>
        public double? Altitude { get; set; }

        /// <summary>The way the camera faced (compass degrees, 0 = north, clockwise).</summary>
        public double? Heading { get; set; }

        /// <summary>True when <see cref="Heading"/> is magnetic (else true north).</summary>
        public bool HeadingMagnetic { get; set; }

        /// <summary>The 35 mm equivalent focal length (mm).</summary>
        public double? FocalLength35 { get; set; }

        /// <summary>When it was taken (the camera's clock, no time zone): "yyyy-MM-dd HH:mm".</summary>
        public string Taken { get; set; }

        /// <summary>True when the photo is on its side (orientations 5–8): width and height swap once it is turned upright.</summary>
        public bool SwapsSides => Orientation >= 5 && Orientation <= 8;

        /// <summary>True when the photo records a usable position.</summary>
        public bool HasLocation => Latitude.HasValue && Longitude.HasValue;
    }

    /// <summary>
    /// Site photos round: reads the EXIF block of a JPEG (APP1 "Exif", TIFF structure, IFD0 → Exif and GPS sub-IFDs):
    /// orientation, GPS position, altitude and image direction, the 35 mm focal length and the time taken. Own code, no
    /// package; bounded reads only; never throws (anything unreadable is left null).
    /// </summary>
    public static class ExifReader
    {
        // Tags
        private const ushort TAG_ORIENTATION = 0x0112, TAG_EXIF_IFD = 0x8769, TAG_GPS_IFD = 0x8825;
        private const ushort TAG_DATE_ORIGINAL = 0x9003, TAG_FOCAL_35 = 0xA405;
        private const ushort GPS_LAT_REF = 1, GPS_LAT = 2, GPS_LON_REF = 3, GPS_LON = 4, GPS_ALT_REF = 5, GPS_ALT = 6, GPS_DIR_REF = 16, GPS_DIR = 17;

        /// <summary>How far into the file the APP1 block is looked for (it comes first in practice).</summary>
        private const int SCAN_LIMIT = 1 << 20;

        /// <summary>Reads a JPEG's EXIF (an empty result when it has none or isn't a JPEG).</summary>
        public static ExifInfo Read(byte[] jpeg)
        {
            var info = new ExifInfo();
            try
            {
                if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) { return info; }
                int p = 2, limit = Math.Min(jpeg.Length, SCAN_LIMIT);
                while (p + 4 <= limit)
                {
                    if (jpeg[p] != 0xFF) { break; }
                    byte marker = jpeg[p + 1];
                    if (marker == 0xFF) { p++; continue; } // fill byte
                    if (marker == 0xD9 || marker == 0xDA) { break; } // end of image / start of scan: no more metadata
                    int length = (jpeg[p + 2] << 8) | jpeg[p + 3];
                    if (length < 2 || p + 2 + length > jpeg.Length) { break; }
                    int body = p + 4;
                    if (marker == 0xE1 && length >= 8 && jpeg[body] == (byte)'E' && jpeg[body + 1] == (byte)'x' && jpeg[body + 2] == (byte)'i'
                        && jpeg[body + 3] == (byte)'f' && jpeg[body + 4] == 0 && jpeg[body + 5] == 0)
                    {
                        ReadTiff(new Span(jpeg, body + 6, length - 8), info);
                        return info;
                    }
                    p += 2 + length;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"EXIF not read: {ex.Message}");
            }
            return info;
        }

        #region TIFF

        /// <summary>A bounded window on the TIFF block with its byte order.</summary>
        private sealed class Span
        {
            private readonly byte[] _data;
            private readonly int _start, _length;

            public Span(byte[] data, int start, int length)
            {
                _data = data;
                _start = start;
                _length = Math.Max(0, Math.Min(length, data.Length - start));
            }

            public bool Motorola { get; set; }

            public int Length => _length;

            public bool Has(int offset, int count) => offset >= 0 && count >= 0 && offset <= _length - count;

            public byte U8(int offset) => Has(offset, 1) ? _data[_start + offset] : (byte)0;

            public ushort U16(int offset)
            {
                if (!Has(offset, 2)) { return 0; }
                int i = _start + offset;
                return Motorola ? (ushort)((_data[i] << 8) | _data[i + 1]) : (ushort)(_data[i] | (_data[i + 1] << 8));
            }

            public uint U32(int offset)
            {
                if (!Has(offset, 4)) { return 0; }
                int i = _start + offset;
                return Motorola
                    ? (uint)((_data[i] << 24) | (_data[i + 1] << 16) | (_data[i + 2] << 8) | _data[i + 3])
                    : (uint)(_data[i] | (_data[i + 1] << 8) | (_data[i + 2] << 16) | (_data[i + 3] << 24));
            }

            public string Ascii(int offset, int count)
            {
                if (!Has(offset, count)) { return null; }
                int end = offset;
                while (end < offset + count && _data[_start + end] != 0) { end++; }
                return System.Text.Encoding.ASCII.GetString(_data, _start + offset, end - offset);
            }
        }

        /// <summary>One IFD entry: its type, count and where its value is (inline or at an offset).</summary>
        private readonly record struct Entry(ushort Type, uint Count, int ValueAt);

        private static void ReadTiff(Span tiff, ExifInfo info)
        {
            if (!tiff.Has(0, 8)) { return; }
            if (tiff.U8(0) == (byte)'M' && tiff.U8(1) == (byte)'M') { tiff.Motorola = true; }
            else if (!(tiff.U8(0) == (byte)'I' && tiff.U8(1) == (byte)'I')) { return; }
            if (tiff.U16(2) != 42) { return; }

            int ifd0 = (int)tiff.U32(4);
            var entries = new Dictionary<ushort, Entry>();
            ReadIfd(tiff, ifd0, entries);
            if (entries.TryGetValue(TAG_ORIENTATION, out Entry orientation))
            {
                int value = tiff.U16(orientation.ValueAt);
                if (value >= 1 && value <= 8) { info.Orientation = value; }
            }

            if (entries.TryGetValue(TAG_EXIF_IFD, out Entry exifPointer))
            {
                var exif = new Dictionary<ushort, Entry>();
                ReadIfd(tiff, (int)tiff.U32(exifPointer.ValueAt), exif);
                if (exif.TryGetValue(TAG_DATE_ORIGINAL, out Entry date)) { info.Taken = ParseDate(tiff.Ascii(date.ValueAt, (int)Math.Min(date.Count, 32))); }
                if (exif.TryGetValue(TAG_FOCAL_35, out Entry focal))
                {
                    int mm = tiff.U16(focal.ValueAt);
                    if (mm >= 8 && mm <= 2000) { info.FocalLength35 = mm; }
                }
            }

            if (entries.TryGetValue(TAG_GPS_IFD, out Entry gpsPointer))
            {
                var gps = new Dictionary<ushort, Entry>();
                ReadIfd(tiff, (int)tiff.U32(gpsPointer.ValueAt), gps);
                ReadGps(tiff, gps, info);
            }
        }

        /// <summary>Reads an IFD's entries (tag → entry; values of 4 bytes or less sit inline).</summary>
        private static void ReadIfd(Span tiff, int offset, Dictionary<ushort, Entry> entries)
        {
            if (!tiff.Has(offset, 2)) { return; }
            int count = tiff.U16(offset);
            if (count > 512) { return; } // not an IFD
            for (int i = 0; i < count; i++)
            {
                int at = offset + 2 + i * 12;
                if (!tiff.Has(at, 12)) { return; }
                ushort tag = tiff.U16(at), type = tiff.U16(at + 2);
                uint n = tiff.U32(at + 4);
                long size = (long)TypeSize(type) * n;
                int valueAt = size <= 4 ? at + 8 : (int)tiff.U32(at + 8);
                if (size > 4 && !tiff.Has(valueAt, (int)Math.Min(size, int.MaxValue))) { continue; }
                entries.TryAdd(tag, new Entry(type, n, valueAt));
            }
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1, // byte, ASCII, signed byte, undefined
            3 or 8 => 2,           // short, signed short
            4 or 9 or 11 => 4,     // long, signed long, float
            5 or 10 or 12 => 8,    // rational, signed rational, double
            _ => 1
        };

        private static double? Rational(Span tiff, Entry entry, int index)
        {
            if (entry.Type != 5 && entry.Type != 10) { return null; }
            if (index >= entry.Count) { return null; }
            int at = entry.ValueAt + index * 8;
            if (!tiff.Has(at, 8)) { return null; }
            double numerator = entry.Type == 10 ? (int)tiff.U32(at) : tiff.U32(at);
            double denominator = entry.Type == 10 ? (int)tiff.U32(at + 4) : tiff.U32(at + 4);
            if (denominator == 0) { return null; }
            double value = numerator / denominator;
            return double.IsFinite(value) ? value : null;
        }

        private static double? Degrees(Span tiff, Dictionary<ushort, Entry> gps, ushort tag)
        {
            if (!gps.TryGetValue(tag, out Entry entry) || entry.Count < 1) { return null; }
            double? d = Rational(tiff, entry, 0);
            if (d == null) { return null; }
            double m = entry.Count > 1 ? Rational(tiff, entry, 1) ?? 0 : 0;
            double s = entry.Count > 2 ? Rational(tiff, entry, 2) ?? 0 : 0;
            return d.Value + m / 60.0 + s / 3600.0;
        }

        private static char Ref(Span tiff, Dictionary<ushort, Entry> gps, ushort tag) =>
            gps.TryGetValue(tag, out Entry entry) ? char.ToUpperInvariant((char)tiff.U8(entry.ValueAt)) : '\0';

        private static void ReadGps(Span tiff, Dictionary<ushort, Entry> gps, ExifInfo info)
        {
            double? lat = Degrees(tiff, gps, GPS_LAT), lon = Degrees(tiff, gps, GPS_LON);
            if (lat.HasValue && lon.HasValue)
            {
                double la = Ref(tiff, gps, GPS_LAT_REF) == 'S' ? -lat.Value : lat.Value;
                double lo = Ref(tiff, gps, GPS_LON_REF) == 'W' ? -lon.Value : lon.Value;
                // 0, 0 is what some cameras write with no fix
                if (Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && (Math.Abs(la) > 1e-9 || Math.Abs(lo) > 1e-9))
                {
                    info.Latitude = la;
                    info.Longitude = lo;
                }
            }
            if (gps.TryGetValue(GPS_ALT, out Entry altitude) && Rational(tiff, altitude, 0) is double alt)
            {
                bool below = gps.TryGetValue(GPS_ALT_REF, out Entry altRef) && tiff.U8(altRef.ValueAt) == 1;
                if (alt < 20000) { info.Altitude = below ? -alt : alt; }
            }
            if (gps.TryGetValue(GPS_DIR, out Entry direction) && Rational(tiff, direction, 0) is double dir && dir >= 0 && dir <= 360)
            {
                info.Heading = dir % 360.0;
                info.HeadingMagnetic = Ref(tiff, gps, GPS_DIR_REF) == 'M';
            }
        }

        /// <summary>"2026:10:01 14:05:33" → "2026-10-01 14:05" (null when unreadable).</summary>
        private static string ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            return DateTime.TryParseExact(text.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime taken)
                ? taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : null;
        }

        #endregion
    }
}
