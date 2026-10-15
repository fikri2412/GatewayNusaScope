using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;

namespace OperatorAi.Worker.Specialist.Excel;

/// <summary>
/// Profil data file .xlsx (ClosedXML) atau .csv (parser sendiri, pemisah , atau ;) sesuai DESIGN bagian 11.1:
/// deteksi header, tipe kolom dominan, statistik ringkas, contoh nilai, dan contoh baris — bukan seluruh isi file.
/// Angka dan tanggal selalu dibaca dengan aturan InvariantCulture (titik desimal) supaya hasil tidak bergantung
/// culture mesin; nilai yang tidak cocok tetap dianggap teks. Isi file tidak pernah dieksekusi.
/// </summary>
public static class ExcelProfiler
{
    /// <summary>Jumlah baris awal yang dipindai untuk mendeteksi header dan menghitung pemisah CSV.</summary>
    private const int HeaderScanRows = 10;

    private const int MaxSheets = 10, MaxColumns = 100, MaxExamples = 5, MaxSampleRows = 5, UniqueRowLimit = 10_000;

    private const string NumberType = "number", DateType = "date", TextType = "text", BoolType = "bool", EmptyType = "empty";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Format tanggal CSV yang dikenali; urutannya tetap supaya hasil deterministik (hari dulu untuk d/M).</summary>
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy",
    ];

    /// <summary>Profil <paramref name="fileName"/> sebagai JSON (output job ExcelRead).</summary>
    /// <param name="content">Stream file; dibaca sampai habis dan tidak ditutup method ini.</param>
    /// <param name="fileName">Dipakai untuk ekstensi (.xlsx/.csv) dan field <c>fileName</c> di output.</param>
    /// <exception cref="NotSupportedException">Ekstensi selain .xlsx dan .csv.</exception>
    public static string Profile(Stream content, string fileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profile = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" => ProfileWorkbook(content, fileName, ct),
            ".csv" => ProfileCsv(content, fileName, ct),
            _ => throw new NotSupportedException($"Format file tidak didukung: {fileName}"),
        };
        return JsonSerializer.Serialize(profile, Json);
    }

    private static FileProfile ProfileWorkbook(Stream content, string fileName, CancellationToken ct)
    {
        using IXLWorkbook workbook = new XLWorkbook(content);
        var sheets = new List<SheetProfile>();
        var sheetCount = workbook.Worksheets.Count;
        var profiled = Math.Min(sheetCount, MaxSheets);
        for (var i = 1; i <= profiled; i++)
        {
            ct.ThrowIfCancellationRequested();
            sheets.Add(ProfileSheet(workbook.Worksheet(i), ct));
        }
        var notes = new List<string>();
        if (sheetCount > profiled) notes.Add($"Hanya {profiled} dari {sheetCount} sheet yang diprofilkan.");
        return new FileProfile(fileName, sheets, notes);
    }

    private static SheetProfile ProfileSheet(IXLWorksheet worksheet, CancellationToken ct)
    {
        var firstRow = worksheet.FirstRowUsed(XLCellsUsedOptions.AllContents);
        var firstColumn = worksheet.FirstColumnUsed(XLCellsUsedOptions.AllContents);
        if (firstRow is null || firstColumn is null)
            return new SheetProfile(worksheet.Name, 0, 0, 0, [], [], ["Sheet kosong."]);

        var lastRow = worksheet.LastRowUsed(XLCellsUsedOptions.AllContents)!;
        var lastColumn = worksheet.LastColumnUsed(XLCellsUsedOptions.AllContents)!;
        var columnStart = firstColumn.ColumnNumber();
        var totalColumns = lastColumn.ColumnNumber() - columnStart + 1;
        var truncationNote = totalColumns > MaxColumns ? $"Kolom dibatasi {MaxColumns} dari {totalColumns}; duplikat tetap membandingkan seluruh kolom." : null;
        var merged = worksheet.MergedRanges.Count;

        IEnumerable<Cell[]> Rows()
        {
            for (var r = 1; r <= lastRow.RowNumber(); r++)
            {
                ct.ThrowIfCancellationRequested();
                var cells = new Cell[totalColumns];
                for (var c = 0; c < totalColumns; c++) cells[c] = ToCell(worksheet.Cell(r, columnStart + c).CachedValue);
                yield return cells;
            }
        }

        return ProfileRows(worksheet.Name, Rows(), merged, truncationNote, ct);
    }

    private static FileProfile ProfileCsv(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var sheet = ProfileRows(name.Length == 0 ? "CSV" : name, new CsvReader(reader).Rows(), 0, null, ct);
        return new FileProfile(fileName, [sheet], []);
    }

    /// <summary>
    /// Statistik satu sheet dari baris-baris sel; baca sekali, simpan baris unik untuk menghitung duplikat persis.
    /// </summary>
    private static SheetProfile ProfileRows(string name, IEnumerable<Cell[]> rows, int mergedRanges, string? truncationNote, CancellationToken ct)
    {
        var notes = new List<string>();
        if (mergedRanges > 0) notes.Add($"Terdapat {mergedRanges} rentang merged cell.");
        if (truncationNote is not null) notes.Add(truncationNote);

        using var enumerator = rows.GetEnumerator();
        var head = new List<Cell[]>(HeaderScanRows);
        while (head.Count < HeaderScanRows && enumerator.MoveNext()) head.Add(enumerator.Current);
        if (head.Count == 0)
        {
            notes.Insert(0, "Sheet kosong.");
            return new SheetProfile(name, 0, 0, 0, [], [], notes);
        }

        var width = 0;
        foreach (var row in head) width = Math.Max(width, Math.Min(row.Length, MaxColumns));
        var headerIndex = DetectHeader(head, width);
        var headerRow = headerIndex < 0 ? 0 : headerIndex + 1;
        notes.Insert(0, headerIndex >= 0
            ? $"Header ditemukan di baris {headerRow}."
            : "Header tidak ditemukan pada 10 baris pertama; semua baris dianggap data dan nama kolom dibuat otomatis.");
        var headerCells = headerIndex >= 0 ? head[headerIndex] : Array.Empty<Cell>();

        var columns = new List<ColumnAccumulator>();
        Ensure(columns, Math.Min(Math.Max(headerCells.Length, 1), MaxColumns), 0);
        var sampleCells = new List<Cell[]>(MaxSampleRows);
        var seenRows = new HashSet<Cell[]>(RowComparer.Instance);
        var rowCount = 0;
        var duplicateRows = 0;

        for (var i = headerIndex + 1; i < head.Count; i++) Process(head[i]);
        while (enumerator.MoveNext()) Process(enumerator.Current);
        if (rowCount > UniqueRowLimit) notes.Add($"Nilai unik dan contoh dihitung hanya dari {UniqueRowLimit} baris data pertama.");

        var names = NameColumns(headerCells, columns.Count, out var renamed);
        if (renamed) notes.Add("Nama kolom kosong/duplikat diganti otomatis.");
        var profiles = new List<ColumnProfile>(columns.Count);
        for (var i = 0; i < columns.Count; i++) profiles.Add(columns[i].ToProfile(names[i]));
        var sampleRows = new List<Dictionary<string, object?>>(sampleCells.Count);
        foreach (var row in sampleCells)
        {
            var item = new Dictionary<string, object?>(names.Count, StringComparer.Ordinal);
            for (var c = 0; c < names.Count; c++) item[names[c]] = c < row.Length ? Display(row[c]) : null;
            sampleRows.Add(item);
        }
        return new SheetProfile(name, headerRow, rowCount, duplicateRows, profiles, sampleRows, notes);

        void Process(Cell[] row)
        {
            ct.ThrowIfCancellationRequested();
            var empty = true;
            foreach (var cell in row)
            {
                if (cell.IsEmpty) continue;
                empty = false;
                break;
            }
            if (empty) return; // baris kosong (mis. sisa format Excel atau baris akhir CSV) bukan data

            if (row.Length > MaxColumns && truncationNote is null)
            {
                truncationNote = $"Kolom dibatasi {MaxColumns}; duplikat tetap membandingkan seluruh kolom.";
                notes.Add(truncationNote);
            }
            Ensure(columns, Math.Min(row.Length, MaxColumns), rowCount);
            for (var c = 0; c < columns.Count; c++)
                columns[c].Add(c < row.Length ? row[c] : Cell.Empty, rowCount < UniqueRowLimit);
            if (!seenRows.Add(row)) duplicateRows++;
            rowCount++;
            if (sampleCells.Count < MaxSampleRows) sampleCells.Add(row);
        }

        static void Ensure(List<ColumnAccumulator> list, int count, int precedingRows)
        {
            while (list.Count < count) list.Add(new ColumnAccumulator(precedingRows));
        }
    }

    /// <summary>Baris pertama dari 10 baris awal yang minimal 50% selnya terisi dan mayoritas sel terisi berupa teks.</summary>
    private static int DetectHeader(List<Cell[]> head, int width)
    {
        if (width == 0) return -1;
        for (var i = 0; i < head.Count; i++)
        {
            var row = head[i];
            var filled = 0;
            var text = 0;
            for (var c = 0; c < width && c < row.Length; c++)
            {
                if (row[c].IsEmpty) continue;
                filled++;
                if (row[c].IsText) text++;
            }
            if (filled * 2 >= width && text * 2 > filled) return i;
        }
        return -1;
    }

    /// <summary>Nama kolom dari baris header; sel kosong dan nama duplikat diganti deterministik tanpa membuang kolom.</summary>
    private static List<string> NameColumns(IReadOnlyList<Cell> header, int count, out bool renamed)
    {
        var names = new List<string>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        renamed = false;
        for (var i = 0; i < count; i++)
        {
            var name = i < header.Count ? HeaderName(header[i]) : "";
            if (name.Length == 0)
            {
                name = $"kolom_{i + 1}";
                renamed = true;
            }
            if (!used.Add(name))
            {
                renamed = true;
                var suffix = 2;
                string candidate;
                do candidate = $"{name}_{suffix++}"; while (!used.Add(candidate));
                name = candidate;
            }
            names.Add(name);
        }
        return names;
    }

    private static string HeaderName(Cell cell) => cell.Kind switch
    {
        CellKind.Number => FormatNumber(cell.Number),
        CellKind.Date => FormatDate(cell.Date),
        CellKind.Bool => cell.Number != 0 ? "true" : "false",
        CellKind.Text => cell.Text!.Trim(),
        _ => "",
    };

    private static Cell ToCell(XLCellValue value) => value.Type switch
    {
        XLDataType.Blank => Cell.Empty,
        XLDataType.Number => NumberCell(value),
        XLDataType.Boolean => Cell.FromBool(value.GetBoolean()),
        XLDataType.DateTime => Cell.FromDate(value.GetDateTime()),
        XLDataType.Text => Cell.FromText(value.GetText()),
        XLDataType.TimeSpan => Cell.FromText(value.GetTimeSpan().ToString()),
        XLDataType.Error => Cell.FromText(value.GetError().ToString()),
        _ => Cell.Empty,
    };

    /// <summary>NaN/Infinity tidak bisa disimpan di JSON; diperlakukan sebagai teks.</summary>
    private static Cell NumberCell(XLCellValue value)
    {
        var number = value.GetNumber();
        return double.IsFinite(number) ? Cell.FromNumber(number) : Cell.FromText(FormatNumber(number));
    }

    /// <summary>Nilai untuk <c>examples</c> dan <c>sampleRows</c>: number/bool apa adanya, tanggal sebagai string ISO.</summary>
    private static object? Display(Cell cell) => cell.Kind switch
    {
        CellKind.Number => cell.Number,
        CellKind.Date => FormatDate(cell.Date),
        CellKind.Bool => cell.Number != 0,
        CellKind.Text => cell.Text,
        _ => null,
    };


    private static string FormatDate(DateTime date) =>
        date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatNumber(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    private enum CellKind { Empty, Number, Date, Text, Bool }

    /// <summary>Satu sel yang sudah dinormalisasi; number dan bool sama-sama disimpan di Number (bool 0/1).</summary>
    private readonly record struct Cell(CellKind Kind, double Number = 0, DateTime Date = default, string? Text = null)
    {

        public static Cell Empty => default;

        /// <summary>Sel kosong atau hanya spasi dianggap kosong, bukan teks.</summary>
        public static Cell FromText(string? text) =>
            string.IsNullOrWhiteSpace(text) ? default(Cell) : new Cell(CellKind.Text, Text: text);

        public static Cell FromNumber(double number) => new(CellKind.Number, Number: number);

        public static Cell FromDate(DateTime date) => new(CellKind.Date, Date: date);

        public static Cell FromBool(bool value) => new(CellKind.Bool, Number: value ? 1 : 0);

        public bool IsEmpty => Kind == CellKind.Empty;

        public bool IsText => Kind == CellKind.Text;
    }

    private sealed class RowComparer : IEqualityComparer<Cell[]>
    {
        public static readonly RowComparer Instance = new();

        public bool Equals(Cell[]? x, Cell[]? y) =>
            x is not null && y is not null && x.AsSpan(0, Length(x)).SequenceEqual(y.AsSpan(0, Length(y)));

        public int GetHashCode(Cell[] row)
        {
            var hash = new HashCode();
            foreach (var cell in row.AsSpan(0, Length(row))) hash.Add(cell);
            return hash.ToHashCode();
        }

        private static int Length(Cell[] row)
        {
            var length = row.Length;
            while (length > 0 && row[length - 1].IsEmpty) length--;
            return length;
        }
    }

    private sealed class ColumnAccumulator(int precedingRows)
    {
        private readonly HashSet<Cell> _unique = [];
        private readonly List<object?> _examples = new(MaxExamples);
        private int _empty = precedingRows, _number, _date, _text, _bool;
        private double _min, _max, _average;
        private DateTime _minDate, _maxDate;
        private bool _hasNumber, _hasDate;

        /// <summary><paramref name="countUnique"/> false setelah 10.000 baris data (batas hitung nilai unik DESIGN 11.1).</summary>
        public void Add(Cell cell, bool countUnique)
        {
            switch (cell.Kind)
            {
                case CellKind.Empty:
                    _empty++;
                    return;
                case CellKind.Number:
                    _number++;
                    if (!_hasNumber)
                    {
                        _min = _max = cell.Number;
                        _hasNumber = true;
                    }
                    else
                    {
                        _min = Math.Min(_min, cell.Number);
                        _max = Math.Max(_max, cell.Number);
                    }
                    var delta = cell.Number - _average;
                    _average = double.IsFinite(delta) ? _average + delta / _number
                        : _average * ((_number - 1d) / _number) + cell.Number / _number;
                    break;
                case CellKind.Date:
                    _date++;
                    if (!_hasDate)
                    {
                        _minDate = _maxDate = cell.Date;
                        _hasDate = true;
                    }
                    else
                    {
                        if (cell.Date < _minDate) _minDate = cell.Date;
                        if (cell.Date > _maxDate) _maxDate = cell.Date;
                    }
                    break;
                case CellKind.Text:
                    _text++;
                    break;
                default:
                    _bool++;
                    break;
            }

            if (!countUnique) return;
            if (_unique.Add(cell) && _examples.Count < MaxExamples) _examples.Add(Display(cell));
        }

        public ColumnProfile ToProfile(string name)
        {
            var type = Type();
            object? min = null, max = null;
            double? avg = null;
            if (type == NumberType)
            {
                min = _min;
                max = _max;
                avg = _average;
            }
            else if (type == DateType)
            {
                min = FormatDate(_minDate);
                max = FormatDate(_maxDate);
            }
            return new ColumnProfile(name, type, _empty, _unique.Count, min, max, avg, _examples);
        }

        private string Type()
        {
            if (_number + _date + _text + _bool == 0) return EmptyType;
            if (_number >= _date && _number >= _text && _number >= _bool) return NumberType;
            if (_date >= _text && _date >= _bool) return DateType;
            return _text >= _bool ? TextType : BoolType;
        }
    }

    /// <summary>
    /// Parser CSV RFC 4180 sederhana: field berkutip, kutip ganda (""), dan newline di dalam kutip. Pemisah ,
    /// atau ; dipilih dari 10 record pertama, dibaca lewat buffer kecil sehingga file besar tidak disalin utuh.
    /// </summary>
    private sealed class CsvReader(TextReader reader)
    {
        private const int ProbeRecords = HeaderScanRows, ProbeChars = 128 * 1024;

        private readonly List<string> _probe = [];
        private char _delimiter;
        private bool _decided;

        public IEnumerable<Cell[]> Rows()
        {
            if (!_decided)
            {
                var chars = 0;
                var commas = 0;
                var semicolons = 0;
                string raw;
                while (_probe.Count < ProbeRecords && chars < ProbeChars && ReadRecord(out raw))
                {
                    _probe.Add(raw);
                    chars += raw.Length;
                    commas += CountOutsideQuotes(raw, ',');
                    semicolons += CountOutsideQuotes(raw, ';');
                }
                _delimiter = semicolons > commas ? ';' : ',';
                _decided = true;
            }
            foreach (var probe in _probe) yield return Cells(probe);
            string line;
            while (ReadRecord(out line)) yield return Cells(line);
        }

        // Satu record logis berhenti di newline di luar kutip; kutip dibiarkan mentah untuk diurai jadi field.
        // ponytail: baca per karakter lewat buffer internal StreamReader — cukup untuk upload ≤10 MB (Upload:MaxBytes);
        // ganti ke buffer blok sendiri bila throughput CSV jadi masalah.
        private bool ReadRecord(out string record)
        {
            var text = new StringBuilder();
            var inQuotes = false;
            var started = false;
            while (true)
            {
                var read = reader.Read();
                if (read < 0)
                {
                    if (inQuotes) throw new FormatException("CSV memiliki field berkutip yang tidak ditutup.");
                    record = text.ToString();
                    return started;
                }
                started = true;
                var ch = (char)read;
                if (ch == '"') inQuotes = !inQuotes;
                if (!inQuotes && (ch is '\r' or '\n'))
                {
                    if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                    record = text.ToString();
                    return true;
                }
                text.Append(ch);
            }
        }

        private Cell[] Cells(string record)
        {
            if (record.Length > 0 && record[0] == '\uFEFF') record = record[1..]; // jaring pengaman bila BOM lolos
            var fields = SplitFields(record, _delimiter);
            var cells = new Cell[fields.Count];
            for (var i = 0; i < fields.Count; i++) cells[i] = ParseValue(fields[i]);
            return cells;
        }

        private static List<string> SplitFields(string record, char delimiter)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var closedQuote = false;
            for (var i = 0; i < record.Length; i++)
            {
                var ch = record[i];
                if (inQuotes)
                {
                    if (ch != '"')
                    {
                        field.Append(ch);
                        continue;
                    }
                    if (i + 1 < record.Length && record[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                        continue;
                    }
                    inQuotes = false;
                    closedQuote = true;
                    continue;
                }
                if (ch == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    closedQuote = false;
                    continue;
                }
                if (ch == '"')
                {
                    if (field.Length != 0 || closedQuote) throw new FormatException("Kutip CSV harus membungkus seluruh field.");
                    inQuotes = true;
                }
                else if (closedQuote)
                {
                    if (!char.IsWhiteSpace(ch)) throw new FormatException("Teks setelah penutup kutip CSV tidak valid.");
                }
                else field.Append(ch);
            }
            fields.Add(field.ToString());
            return fields;
        }

        /// <summary>Number/date/bool dikenali InvariantCulture; sisanya teks apa adanya.</summary>
        private static Cell ParseValue(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0) return Cell.Empty;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return Cell.FromBool(true);
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return Cell.FromBool(false);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number))
                return Cell.FromNumber(number);
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return Cell.FromDate(date);
            return Cell.FromText(raw);
        }

        private static int CountOutsideQuotes(string record, char candidate)
        {
            var count = 0;
            var inQuotes = false;
            foreach (var ch in record)
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == candidate && !inQuotes) count++;
            }
            return count;
        }
    }

    private sealed record FileProfile(
        string FileName,
        List<SheetProfile> Sheets,
        [property: JsonPropertyName("catatan")] List<string> Notes);

    private sealed record SheetProfile(
        string Name,
        int HeaderRow,
        int RowCount,
        int DuplicateRows,
        List<ColumnProfile> Columns,
        List<Dictionary<string, object?>> SampleRows,
        [property: JsonPropertyName("catatan")] List<string> Notes)
    {
        public int ColumnCount => Columns.Count;
    }

    private sealed record ColumnProfile(
        string Name,
        string Type,
        int Empty,
        int Unique,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Min,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Max,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Avg,
        List<object?> Examples);
}
