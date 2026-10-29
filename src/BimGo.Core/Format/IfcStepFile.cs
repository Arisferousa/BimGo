using System.Globalization;
using System.IO.Compression;
using System.Text;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// A reference to another entity instance (<c>#123</c>) in a STEP file.
    /// </summary>
    /// <param name="Id">The instance number.</param>
    internal readonly record struct StepRef(int Id);

    /// <summary>
    /// An enumeration value (<c>.ELEMENT.</c>, <c>.T.</c>) in a STEP file, without the dots.
    /// </summary>
    /// <param name="Name">The upper-case name.</param>
    internal sealed record StepEnum(string Name);

    /// <summary>
    /// A typed value such as <c>IFCLABEL('Wall')</c> or <c>IFCLENGTHMEASURE(200.)</c>.
    /// </summary>
    /// <param name="Type">The upper-case type name.</param>
    /// <param name="Value">The wrapped value.</param>
    internal sealed record StepTyped(string Type, object Value);

    /// <summary>
    /// The derived-value marker (<c>*</c>) in a STEP file.
    /// </summary>
    internal sealed class StepDerived
    {
        /// <summary>The single instance.</summary>
        public static readonly StepDerived Instance = new();

        private StepDerived() { }
    }

    /// <summary>
    /// A read-only ISO 10303-21 (STEP physical file) reader, as IFC files are written. One pass indexes every entity
    /// instance (number, type, where its arguments are); arguments are parsed on demand and never cached, so a large file
    /// costs little more than its own bytes. Values are: null (<c>$</c>), <see cref="StepDerived"/> (<c>*</c>),
    /// <see cref="long"/>, <see cref="double"/>, <see cref="string"/> (decoded), <see cref="StepEnum"/>,
    /// <see cref="StepRef"/>, <see cref="StepTyped"/> and <c>object[]</c> (lists).
    /// </summary>
    internal sealed class StepFile
    {
        #region Fields

        private readonly byte[] _data;
        private readonly Dictionary<int, int> _slotById = new();
        private readonly List<string> _types = new();
        private readonly List<int> _argStart = new();
        private readonly Dictionary<string, List<int>> _idsByType = new(StringComparer.Ordinal);

        #endregion

        #region Header

        /// <summary>The schema named in FILE_SCHEMA, upper case (e.g. "IFC2X3", "IFC4"), or empty.</summary>
        public string Schema { get; private set; } = string.Empty;

        /// <summary>FILE_NAME's originating system (e.g. "Autodesk Revit 2025 (ENU)"), or empty.</summary>
        public string OriginatingSystem { get; private set; } = string.Empty;

        /// <summary>FILE_NAME's preprocessor version (e.g. the exporter's name and version), or empty.</summary>
        public string PreprocessorVersion { get; private set; } = string.Empty;

        /// <summary>FILE_DESCRIPTION's description strings joined (e.g. "ViewDefinition [CoordinationView_V2.0]"), or empty.</summary>
        public string Description { get; private set; } = string.Empty;

        /// <summary>Number of entity instances.</summary>
        public int Count => _types.Count;

        #endregion

        private StepFile(byte[] data)
        {
            _data = data;
        }

        #region Reading

        /// <summary>
        /// Reads and indexes a file. A <c>.ifczip</c> is opened and its first <c>.ifc</c> entry read.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="progress">Optional progress / cancellation (the caller starts the stage).</param>
        /// <exception cref="InvalidDataException">The file is not a STEP file.</exception>
        public static StepFile Read(string path, Utilities.OperationProgress progress = null)
        {
            byte[] data;
            if (string.Equals(System.IO.Path.GetExtension(path), ".ifczip", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The .ifczip holds no .ifc file.");
                if (entry.Length > int.MaxValue - 64) { throw new InvalidDataException("The IFC file is too large (over 2 GB)."); }
                data = new byte[entry.Length];
                using Stream stream = entry.Open();
                stream.ReadExactly(data);
            }
            else
            {
                data = File.ReadAllBytes(path);
            }
            return Parse(data, progress);
        }

        /// <summary>
        /// Indexes a file already in memory.
        /// </summary>
        /// <exception cref="InvalidDataException">The data is not a STEP file.</exception>
        public static StepFile Parse(byte[] data, Utilities.OperationProgress progress = null)
        {
            var file = new StepFile(data ?? Array.Empty<byte>());
            file.Index(progress);
            return file;
        }

        /// <summary>
        /// The one pass over the file: header records parsed, entity instances indexed.
        /// </summary>
        private void Index(Utilities.OperationProgress progress)
        {
            byte[] d = _data;
            int n = d.Length;
            int i = 0;

            SkipSpace(ref i);
            if (!StartsWith(i, "ISO-10303-21")) { throw new InvalidDataException("This is not an IFC (STEP) file."); }

            int nextReport = 1 << 20;
            while (i < n)
            {
                SkipSpace(ref i);
                if (i >= n) { break; }

                if (i >= nextReport)
                {
                    progress?.Step(i, n);
                    progress?.ThrowIfCancelled();
                    nextReport = i + (1 << 20);
                }

                if (d[i] == (byte)'#')
                {
                    // #123 = TYPE(args);
                    i++;
                    int id = ReadInt(ref i);
                    SkipSpace(ref i);
                    if (i < n && d[i] == (byte)'=') { i++; }
                    SkipSpace(ref i);
                    if (i < n && d[i] == (byte)'(')
                    {
                        // A complex instance (several types): not used by IFC; skipped
                        SkipStatement(ref i);
                        continue;
                    }
                    string type = ReadKeyword(ref i);
                    SkipSpace(ref i);
                    int argStart = i;
                    SkipStatement(ref i);
                    if (type.Length == 0 || argStart >= n || d[argStart] != (byte)'(') { continue; }

                    int slot = _types.Count;
                    _types.Add(type);
                    _argStart.Add(argStart);
                    _slotById[id] = slot;
                    if (!_idsByType.TryGetValue(type, out List<int> ids))
                    {
                        ids = new List<int>();
                        _idsByType[type] = ids;
                    }
                    ids.Add(id);
                    continue;
                }

                // A keyword statement: HEADER; FILE_SCHEMA(...); DATA; ENDSEC; END-ISO-10303-21;
                int start = i;
                string keyword = ReadKeyword(ref i);
                if (keyword.Length == 0)
                {
                    i = start + 1; // stray character
                    continue;
                }
                SkipSpace(ref i);
                if (i < n && d[i] == (byte)'(')
                {
                    int headerArgs = i;
                    SkipStatement(ref i);
                    ReadHeaderRecord(keyword, headerArgs);
                }
                else
                {
                    SkipStatement(ref i);
                }
            }

            if (_types.Count == 0) { throw new InvalidDataException("The IFC file holds no data."); }
        }

        /// <summary>
        /// Picks what BimGo reports from the header (schema, originating system, view definition).
        /// </summary>
        private void ReadHeaderRecord(string keyword, int argStart)
        {
            try
            {
                int i = argStart;
                if (ParseValue(ref i) is not object[] args) { return; }
                switch (keyword)
                {
                    case "FILE_SCHEMA":
                        if (args.Length > 0 && args[0] is object[] schemas && schemas.Length > 0 && schemas[0] is string schema)
                        {
                            Schema = schema.Trim().ToUpperInvariant();
                        }
                        break;
                    case "FILE_NAME":
                        if (args.Length > 5 && args[5] is string preprocessor) { PreprocessorVersion = preprocessor.Trim(); }
                        if (args.Length > 6 && args[6] is string system) { OriginatingSystem = system.Trim(); }
                        break;
                    case "FILE_DESCRIPTION":
                        if (args.Length > 0 && args[0] is object[] descriptions)
                        {
                            Description = string.Join(" ", descriptions.OfType<string>()).Trim();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"IFC header record {keyword} unreadable: {ex.Message}");
            }
        }

        #endregion

        #region Lookup

        /// <summary>True when the file holds an instance with this number.</summary>
        public bool Contains(int id) => _slotById.ContainsKey(id);

        /// <summary>The instance's upper-case type (e.g. "IFCWALL"), or null when there is no such instance.</summary>
        public string TypeOf(int id) => _slotById.TryGetValue(id, out int slot) ? _types[slot] : null;

        /// <summary>True when the instance exists and has the given type.</summary>
        public bool Is(int id, string type) => string.Equals(TypeOf(id), type, StringComparison.Ordinal);

        /// <summary>
        /// The instance's arguments (parsed now; never cached), or an empty array when there is no such instance or its
        /// arguments are damaged.
        /// </summary>
        public object[] Args(int id)
        {
            if (!_slotById.TryGetValue(id, out int slot)) { return Array.Empty<object>(); }
            try
            {
                int i = _argStart[slot];
                return ParseValue(ref i) as object[] ?? Array.Empty<object>();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"IFC #{id} unreadable: {ex.Message}");
                return Array.Empty<object>();
            }
        }

        /// <summary>The numbers of every instance of a type (upper case), in file order; empty when there are none.</summary>
        public IReadOnlyList<int> OfType(string type) =>
            _idsByType.TryGetValue(type, out List<int> ids) ? ids : (IReadOnlyList<int>)Array.Empty<int>();

        /// <summary>Every type in the file with its instance count (for the import log).</summary>
        public IEnumerable<KeyValuePair<string, int>> TypeCounts() => _idsByType.Select(p => new KeyValuePair<string, int>(p.Key, p.Value.Count));

        #endregion

        #region Value helpers

        /// <summary>The argument at an index, or null when the list is shorter.</summary>
        public static object Arg(object[] args, int index) => args != null && index >= 0 && index < args.Length ? args[index] : null;

        /// <summary>A reference's instance number, or 0 when the value isn't a reference.</summary>
        public static int Ref(object value) => value is StepRef r ? r.Id : 0;

        /// <summary>A number (integer, real or a typed number such as IFCLENGTHMEASURE), or the fallback.</summary>
        public static double Number(object value, double fallback = double.NaN) => value switch
        {
            double d => d,
            long l => l,
            StepTyped t => Number(t.Value, fallback),
            _ => fallback
        };

        /// <summary>A string (or a typed string such as IFCLABEL), or null.</summary>
        public static string Text(object value) => value switch
        {
            string s => s,
            StepTyped t => Text(t.Value),
            _ => null
        };

        /// <summary>An enumeration's name, or null.</summary>
        public static string Enum(object value) => value switch
        {
            StepEnum e => e.Name,
            StepTyped t => Enum(t.Value),
            _ => null
        };

        /// <summary>A logical (.T. / .F.; anything else is the fallback).</summary>
        public static bool Bool(object value, bool fallback) => Enum(value) switch
        {
            "T" => true,
            "F" => false,
            _ => fallback
        };

        /// <summary>A list, or an empty array.</summary>
        public static object[] List(object value) => value as object[] ?? Array.Empty<object>();

        #endregion

        #region Lexing

        private bool StartsWith(int i, string text)
        {
            if (i + text.Length > _data.Length) { return false; }
            for (int k = 0; k < text.Length; k++)
            {
                if (_data[i + k] != (byte)text[k]) { return false; }
            }
            return true;
        }

        /// <summary>Skips white space and /* comments */.</summary>
        private void SkipSpace(ref int i)
        {
            byte[] d = _data;
            int n = d.Length;
            while (i < n)
            {
                byte c = d[i];
                if (c == (byte)' ' || c == (byte)'\r' || c == (byte)'\n' || c == (byte)'\t' || c == 0xEF || c == 0xBB || c == 0xBF)
                {
                    i++;
                }
                else if (c == (byte)'/' && i + 1 < n && d[i + 1] == (byte)'*')
                {
                    int end = i + 2;
                    while (end + 1 < n && !(d[end] == (byte)'*' && d[end + 1] == (byte)'/')) { end++; }
                    i = Math.Min(n, end + 2);
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>Skips to just past the statement's terminating semicolon (strings and comments respected).</summary>
        private void SkipStatement(ref int i)
        {
            byte[] d = _data;
            int n = d.Length;
            while (i < n)
            {
                byte c = d[i];
                if (c == (byte)'\'')
                {
                    i++;
                    while (i < n)
                    {
                        if (d[i] == (byte)'\'')
                        {
                            if (i + 1 < n && d[i + 1] == (byte)'\'') { i += 2; continue; }
                            break;
                        }
                        i++;
                    }
                    i++;
                }
                else if (c == (byte)'/' && i + 1 < n && d[i + 1] == (byte)'*')
                {
                    SkipSpace(ref i);
                }
                else if (c == (byte)';')
                {
                    i++;
                    return;
                }
                else
                {
                    i++;
                }
            }
        }

        private int ReadInt(ref int i)
        {
            long value = 0;
            while (i < _data.Length && _data[i] >= (byte)'0' && _data[i] <= (byte)'9')
            {
                value = value * 10 + (_data[i] - (byte)'0');
                if (value > int.MaxValue) { value = int.MaxValue; }
                i++;
            }
            return (int)value;
        }

        /// <summary>Reads an upper-case keyword (letters, digits, '_', '-'), interned; empty when there is none.</summary>
        private string ReadKeyword(ref int i)
        {
            int start = i;
            while (i < _data.Length)
            {
                byte c = _data[i];
                bool ok = (c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'a' && c <= (byte)'z') || (c >= (byte)'0' && c <= (byte)'9') || c == (byte)'_' || c == (byte)'-';
                if (!ok) { break; }
                i++;
            }
            if (i == start) { return string.Empty; }
            return string.Intern(Encoding.ASCII.GetString(_data, start, i - start).ToUpperInvariant());
        }

        #endregion

        #region Parsing values

        /// <summary>
        /// Parses one value at <paramref name="i"/> (white space first skipped).
        /// </summary>
        private object ParseValue(ref int i)
        {
            byte[] d = _data;
            SkipSpace(ref i);
            if (i >= d.Length) { throw new InvalidDataException("Unexpected end of file."); }
            byte c = d[i];
            switch (c)
            {
                case (byte)'(':
                {
                    i++;
                    var items = new List<object>();
                    SkipSpace(ref i);
                    if (i < d.Length && d[i] == (byte)')')
                    {
                        i++;
                        return Array.Empty<object>();
                    }
                    while (i < d.Length)
                    {
                        items.Add(ParseValue(ref i));
                        SkipSpace(ref i);
                        if (i >= d.Length) { break; }
                        if (d[i] == (byte)',') { i++; continue; }
                        if (d[i] == (byte)')') { i++; break; }
                        throw new InvalidDataException($"Unexpected '{(char)d[i]}' in a list.");
                    }
                    return items.ToArray();
                }
                case (byte)'$':
                    i++;
                    return null;
                case (byte)'*':
                    i++;
                    return StepDerived.Instance;
                case (byte)'#':
                    i++;
                    return new StepRef(ReadInt(ref i));
                case (byte)'\'':
                    return ReadString(ref i);
                case (byte)'"':
                {
                    // Binary: kept as its hex text
                    int start = ++i;
                    while (i < d.Length && d[i] != (byte)'"') { i++; }
                    string hex = Encoding.ASCII.GetString(d, start, i - start);
                    i++;
                    return hex;
                }
                case (byte)'.':
                {
                    int start = ++i;
                    while (i < d.Length && d[i] != (byte)'.') { i++; }
                    string name = string.Intern(Encoding.ASCII.GetString(d, start, i - start).Trim().ToUpperInvariant());
                    i++;
                    return name switch
                    {
                        "T" => TRUE,
                        "F" => FALSE,
                        _ => new StepEnum(name)
                    };
                }
                default:
                    if (c == (byte)'-' || c == (byte)'+' || (c >= (byte)'0' && c <= (byte)'9')) { return ReadNumber(ref i); }
                    if ((c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'a' && c <= (byte)'z'))
                    {
                        // A typed value: KEYWORD(value)
                        string type = ReadKeyword(ref i);
                        SkipSpace(ref i);
                        if (i < d.Length && d[i] == (byte)'(')
                        {
                            object[] inner = ParseValue(ref i) as object[];
                            return new StepTyped(type, inner != null && inner.Length == 1 ? inner[0] : inner);
                        }
                        return new StepEnum(type);
                    }
                    throw new InvalidDataException($"Unexpected '{(char)c}'.");
            }
        }

        private static readonly StepEnum TRUE = new("T");
        private static readonly StepEnum FALSE = new("F");

        private object ReadNumber(ref int i)
        {
            byte[] d = _data;
            int start = i;
            bool real = false;
            if (d[i] == (byte)'-' || d[i] == (byte)'+') { i++; }
            while (i < d.Length)
            {
                byte c = d[i];
                if (c >= (byte)'0' && c <= (byte)'9') { i++; }
                else if (c == (byte)'.' || c == (byte)'E' || c == (byte)'e') { real = true; i++; }
                else if ((c == (byte)'-' || c == (byte)'+') && (d[i - 1] == (byte)'E' || d[i - 1] == (byte)'e')) { i++; }
                else { break; }
            }
            int length = i - start;
            Span<char> text = length <= 64 ? stackalloc char[length] : new char[length];
            for (int k = 0; k < length; k++) { text[k] = (char)d[start + k]; }
            if (!real && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)) { return integer; }
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0.0;
        }

        /// <summary>
        /// Reads a quoted string and decodes STEP's escapes: '' (a quote), \\ (a backslash), \X2\…\X0\ (UTF-16 hex),
        /// \X4\…\X0\ (UTF-32 hex), \X\hh (an ISO 8859-1 byte), \S\c (c + 128) and \Px\ (code page, ignored). Other bytes
        /// are read as UTF-8 (some writers emit it raw), else ISO 8859-1.
        /// </summary>
        private string ReadString(ref int i)
        {
            byte[] d = _data;
            int n = d.Length;
            i++; // opening quote
            var text = new StringBuilder();
            var raw = new List<byte>();

            void FlushRaw()
            {
                if (raw.Count == 0) { return; }
                byte[] bytes = raw.ToArray();
                raw.Clear();
                try { text.Append(STRICT_UTF8.GetString(bytes)); }
                catch (DecoderFallbackException) { text.Append(Encoding.Latin1.GetString(bytes)); }
            }

            while (i < n)
            {
                byte c = d[i];
                if (c == (byte)'\'')
                {
                    if (i + 1 < n && d[i + 1] == (byte)'\'')
                    {
                        raw.Add((byte)'\'');
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                if (c == (byte)'\\' && i + 1 < n)
                {
                    if (d[i + 1] == (byte)'\\') { raw.Add((byte)'\\'); i += 2; continue; }
                    if (MatchAt(i, "\\X2\\") || MatchAt(i, "\\X4\\"))
                    {
                        FlushRaw();
                        int width = d[i + 2] == (byte)'2' ? 4 : 8;
                        i += 4;
                        while (i < n && !MatchAt(i, "\\X0\\") && d[i] != (byte)'\'')
                        {
                            if (i + width > n) { break; }
                            int code = 0;
                            for (int k = 0; k < width; k++) { code = code * 16 + HexValue(d[i + k]); }
                            if (width == 4) { text.Append((char)code); }
                            else { text.Append(char.ConvertFromUtf32(code >= 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? code : 0xFFFD)); }
                            i += width;
                        }
                        if (MatchAt(i, "\\X0\\")) { i += 4; }
                        continue;
                    }
                    if (MatchAt(i, "\\X\\") && i + 4 < n)
                    {
                        FlushRaw();
                        text.Append((char)(HexValue(d[i + 3]) * 16 + HexValue(d[i + 4])));
                        i += 5;
                        continue;
                    }
                    if (MatchAt(i, "\\S\\") && i + 3 < n)
                    {
                        FlushRaw();
                        text.Append((char)(d[i + 3] + 128));
                        i += 4;
                        continue;
                    }
                    if (i + 3 < n && d[i + 1] == (byte)'P' && d[i + 3] == (byte)'\\')
                    {
                        i += 4; // code page switch: ignored
                        continue;
                    }
                }
                raw.Add(c);
                i++;
            }
            FlushRaw();
            return text.ToString();
        }

        private static readonly Encoding STRICT_UTF8 = new UTF8Encoding(false, true);

        private bool MatchAt(int i, string token) => StartsWith(i, token);

        private static int HexValue(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            _ => 0
        };

        #endregion
    }
}
