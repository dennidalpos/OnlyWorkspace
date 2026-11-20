using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// E8: local OCR of scanned PDF pages and images with Windows.Media.Ocr, in the languages of the Windows user profile
/// (https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine). A page or image is read only when the
/// recognized text reaches the same threshold as a PDF text page; otherwise the whole document stays unread.
/// </summary>
internal static partial class ContentReader
{
    // Windows.Data.Pdf sizes pages in 1/96 inch: rendered at 300 dpi for recognition.
    private const double OcrScale = 300.0 / 96.0;
    // A TIFF fax can hold many pages; more than this is not a document to read whole within the time limit.
    private const int MaxImageFrames = 200;
    private const string OcrMarker = "[testo riconosciuto con OCR]";

    private static bool _ocrLoaded;
    private static OcrEngine? _ocr;

    /// <summary>The administrator's setting: off, scans and images stay unread as "ocr-off".</summary>
    internal static bool OcrEnabled { get; set; } = true;

    /// <summary>Recognizer for the user's profile languages; null when Windows has none or the API is missing.</summary>
    private static OcrEngine? Ocr
    {
        get
        {
            if (_ocrLoaded) return _ocr;
            _ocrLoaded = true;
            try { _ocr = OcrEngine.TryCreateFromUserProfileLanguages(); }
            catch (Exception) { _ocr = null; }
            return _ocr;
        }
    }

    /// <summary>Recognizer languages installed in Windows and the one OCR uses (null: none of the profile's).</summary>
    internal static (string[] Installed, string? Used) OcrLanguages()
    {
        try { return (OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray(), Ocr?.RecognizerLanguage.LanguageTag); }
        catch (Exception) { return (Array.Empty<string>(), null); }
    }

    private static T Await<T>(IAsyncOperation<T> operation) => operation.AsTask().GetAwaiter().GetResult();

    private static int LettersAndDigits(IEnumerable<string> lines) => lines.Sum(line => line.Count(char.IsLetterOrDigit));

    private static List<string> Recognize(OcrEngine engine, SoftwareBitmap bitmap, bool preserveLayout)
    {
        var result = Await(engine.RecognizeAsync(bitmap));
        if (!preserveLayout)
            return result.Lines.Select(line => Cents.Replace(line.Text, "00")).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

        var words = result.Lines.SelectMany(line => line.Words).Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderBy(w => w.BoundingRect.Bottom).ToList();
        var rows = new List<string>();
        if (words.Count == 0) return rows;
        double charWidth = Math.Max(1, Median(words.Select(w => w.BoundingRect.Width / w.Text.Length)));
        double tolerance = Math.Max(1, Median(words.Select(w => w.BoundingRect.Height)) * 0.5);
        double left = words.Min(w => w.BoundingRect.Left);
        for (int start = 0; start < words.Count;)
        {
            int end = start + 1;
            while (end < words.Count && words[end].BoundingRect.Bottom - words[start].BoundingRect.Bottom <= tolerance) end++;
            var row = words.GetRange(start, end - start).OrderBy(w => w.BoundingRect.Left)
                .Select(w => (w.Text, w.BoundingRect.Left, w.BoundingRect.Right));
            rows.Add(Cents.Replace(RenderLine(row, left, charWidth), "00"));
            start = end;
        }
        return rows;
    }

    // Retry passes (rotation, contrast, enlargement) run only on a page whose first reading looks like noise, and
    // each document has a budget so a long scan cannot multiply its OCR time.
    private const int RetryBudgetPerDocument = 40;
    private const double GoodQuality = 0.9;
    private const int EnlargeBelowSide = 2400;
    // Text in a source this small is a few pixels high: Windows OCR reads it better enlarged.
    private const int SmallSourceSide = 1000;
    private static int _retryBudget = RetryBudgetPerDocument;

    /// <summary>
    /// Characters that sit in plausible words, split at every punctuation mark: a number of two or more digits, or three
    /// or more letters with a vowel, no letter repeated three times in a row, no digit among the letters and no capital
    /// after a lowercase letter. A page read upside down, sideways or from a blurred photo comes out as fragments such as
    /// "9ZOZ", "OleaoL" or "lJ0J" that fail this test, even when it has more than the minimum of letters and digits.
    /// </summary>
    private static int PlausibleChars(IEnumerable<string> lines)
    {
        int total = 0;
        foreach (var line in lines)
            foreach (var token in line.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                int letters = token.Count(char.IsLetter), digits = token.Count(char.IsDigit);
                if (letters == 0) { if (digits >= 2) total += digits; continue; }
                if (digits > 0 || letters < 3 || token.IndexOfAny(Vowels) < 0 || HasTripleRun(token) || HasInnerCapital(token)) continue;
                total += letters;
            }
        return total;
    }

    private static readonly char[] Separators = Enumerable.Range(0, 0x250).Select(c => (char)c)
        .Where(c => !char.IsLetterOrDigit(c)).Concat(new[] { '€', '–', '—', '’', '“', '”', '•' }).ToArray();

    private static bool HasInnerCapital(string token)
    {
        bool lower = false;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLower(token[i])) lower = true;
            else if (char.IsUpper(token[i]) && (i > 0 && lower)) return true;
        }
        return false;
    }

    private static readonly char[] Vowels = "aeiouyàèéìòùAEIOUYÀÈÉÌÒÙ".ToCharArray();

    private static bool HasTripleRun(string token)
    {
        for (int i = 2; i < token.Length; i++)
            if (char.ToLowerInvariant(token[i]) == char.ToLowerInvariant(token[i - 1]) && char.ToLowerInvariant(token[i - 1]) == char.ToLowerInvariant(token[i - 2]) && char.IsLetter(token[i]))
                return true;
        return false;
    }

    private static bool IsGood(List<string> lines)
    {
        int alnum = LettersAndDigits(lines);
        return alnum >= MinCharsPerTextPage && PlausibleChars(lines) >= GoodQuality * alnum;
    }

    private static byte[] Bytes(SoftwareBitmap bitmap)
    {
        var data = new byte[4 * bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyToBuffer(data.AsBuffer());
        return data;
    }

    private static SoftwareBitmap FromBytes(byte[] data, int width, int height) =>
        SoftwareBitmap.CreateCopyFromBuffer(data.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);

    /// <summary>Clockwise quarter turns of a BGRA8 image.</summary>
    private static (byte[] Data, int Width, int Height) Rotate(byte[] src, int width, int height, int quarterTurns)
    {
        quarterTurns &= 3;
        if (quarterTurns == 0) return (src, width, height);
        int nw = quarterTurns == 2 ? width : height, nh = quarterTurns == 2 ? height : width;
        var dst = new byte[src.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int nx, ny;
                switch (quarterTurns)
                {
                    case 1: nx = height - 1 - y; ny = x; break;
                    case 2: nx = width - 1 - x; ny = height - 1 - y; break;
                    default: nx = y; ny = width - 1 - x; break;
                }
                System.Buffer.BlockCopy(src, 4 * (y * width + x), dst, 4 * (ny * nw + nx), 4);
            }
        return (dst, nw, nh);
    }

    /// <summary>Gray levels stretched between the 1st and 99th percentile: faint or washed-out scans and photos.</summary>
    private static byte[] Stretch(byte[] src)
    {
        var histogram = new int[256];
        int pixels = src.Length / 4;
        for (int i = 0; i < src.Length; i += 4) histogram[(src[i] * 29 + src[i + 1] * 150 + src[i + 2] * 77) >> 8]++;
        int low = 0, high = 255, seen = 0;
        for (; low < 255 && seen + histogram[low] < pixels * 0.01; low++) seen += histogram[low];
        seen = 0;
        for (; high > 0 && seen + histogram[high] < pixels * 0.01; high--) seen += histogram[high];
        if (high - low < 16) return src;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            int gray = (src[i] * 29 + src[i + 1] * 150 + src[i + 2] * 77) >> 8;
            byte v = (byte)Math.Max(0, Math.Min(255, (gray - low) * 255 / (high - low)));
            dst[i] = dst[i + 1] = dst[i + 2] = v;
            dst[i + 3] = 255;
        }
        return dst;
    }

    /// <summary>
    /// First reading as is. A page that does not look like text gets more passes (contrast stretch, three rotations,
    /// then a 2x enlargement of small sources with and without stretch) and the reading with the most plausible
    /// characters wins. <paramref name="load"/> returns the page at the given scale relative to the base size.
    /// </summary>
    private static List<string> RecognizeBest(OcrEngine engine, Func<double, SoftwareBitmap> load, bool preserveLayout = false)
    {
        List<string> best;
        byte[] data; int width, height;
        bool small, good;
        using (var first = load(1.0))
        {
            best = Recognize(engine, first, preserveLayout);
            width = first.PixelWidth; height = first.PixelHeight;
            small = Math.Max(width, height) < SmallSourceSide;
            good = IsGood(best);
            // A clean reading of a page of normal size stands; a small source is always tried enlarged too.
            if ((good && !small) || _retryBudget <= 0) return best;
            data = Bytes(first);
        }

        int bestScore = PlausibleChars(best);
        // For a small source that already reads well only the enlarged readings run, and the better one is kept.
        if (good)
        {
            using var enlarged = load(2.0);
            var pixels = Bytes(enlarged);
            _retryBudget -= 2;
            foreach (var candidate in new[] { pixels, Stretch(pixels) })
            {
                using var bitmap = FromBytes(candidate, enlarged.PixelWidth, enlarged.PixelHeight);
                var lines = Recognize(engine, bitmap, preserveLayout);
                int score = PlausibleChars(lines);
                if (score > bestScore) { best = lines; bestScore = score; }
            }
            return best;
        }

        bool Try(byte[] pixels, int w, int h)
        {
            if (_retryBudget <= 0) return true;
            _retryBudget--;
            if (Math.Max(w, h) > OcrEngine.MaxImageDimension) return false;
            using var bitmap = FromBytes(pixels, w, h);
            var lines = Recognize(engine, bitmap, preserveLayout);
            int score = PlausibleChars(lines);
            if (score > bestScore || (score == bestScore && LettersAndDigits(lines) > LettersAndDigits(best))) { best = lines; bestScore = score; }
            return IsGood(best);
        }

        var stretched = Stretch(data);
        if (!ReferenceEquals(stretched, data) && Try(stretched, width, height)) return best;
        for (int turns = 1; turns <= 3; turns++)
        {
            var (rotated, rw, rh) = Rotate(data, width, height, turns);
            if (Try(rotated, rw, rh)) return best;
        }
        if (Math.Max(width, height) < EnlargeBelowSide)
        {
            using var bigger = load(2.0);
            var big = Bytes(bigger);
            if (Try(big, bigger.PixelWidth, bigger.PixelHeight)) return best;
            var bigStretched = Stretch(big);
            if (!ReferenceEquals(bigStretched, big) && Try(bigStretched, bigger.PixelWidth, bigger.PixelHeight)) return best;
            for (int turns = 1; turns <= 3; turns++)
            {
                var (rotated, rw, rh) = Rotate(bigStretched, bigger.PixelWidth, bigger.PixelHeight, turns);
                if (Try(rotated, rw, rh)) return best;
            }
        }
        return best;
    }

    // The recognizer often reads the cents of an amount as letters ("1.250,OO", 233 of 400 lines of a synthetic
    // 10-page scan): two O after a digit and a separator, not followed by a letter or digit, are zeros.
    private static readonly Regex Cents = new(@"(?<=\d[.,])(?=0?[Oo])[0Oo]{2}(?![\p{L}\d])", RegexOptions.CultureInvariant);

    /// <summary>Scaled down only when a side exceeds what the recognizer accepts.</summary>
    private static BitmapTransform Fit(uint width, uint height)
    {
        double fit = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(1, Math.Max(width, height)));
        return new BitmapTransform { ScaledWidth = (uint)Math.Max(1, width * fit), ScaledHeight = (uint)Math.Max(1, height * fit), InterpolationMode = BitmapInterpolationMode.Fant };
    }

    /// <returns>null when every frame was read, else "scanned", "ocr-off" or "ocr-unavailable".</returns>
    private static string? ReadImage(string path, LimitedText text)
    {
        if (!OcrEnabled) return "ocr-off";
        var engine = Ocr;
        if (engine == null) return "ocr-unavailable";
        var pages = new List<List<string>>();
        _retryBudget = RetryBudgetPerDocument;
        using (var file = File.OpenRead(path))
        {
            var decoder = Await(BitmapDecoder.CreateAsync(file.AsRandomAccessStream()));
            if (decoder.FrameCount > MaxImageFrames) return "scanned";
            for (uint index = 0; index < decoder.FrameCount; index++)
            {
                var frame = Await(decoder.GetFrameAsync(index));
                var lines = RecognizeBest(engine, scale => Await(frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    Fit((uint)Math.Ceiling(frame.OrientedPixelWidth * scale), (uint)Math.Ceiling(frame.OrientedPixelHeight * scale)),
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)));
                if (LettersAndDigits(lines) < MinCharsPerTextPage) return "scanned";
                pages.Add(lines);
            }
        }
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages.Count > 1 && !text.AppendLine($"--- pagina {i + 1} ---")) break;
            if (!text.AppendLine(OcrMarker)) break;
            foreach (var line in pages[i]) if (!text.AppendLine(line)) break;
        }
        return null;
    }

    /// <summary>Recognizes the given 1-based pages; every one must reach the threshold.</summary>
    /// <returns>null when all were read, else "scanned", "ocr-off" or "ocr-unavailable".</returns>
    private static string? RecognizePdfPages(string path, IReadOnlyCollection<int> pages, Dictionary<int, List<string>> recognized)
    {
        if (!OcrEnabled) return "ocr-off";
        var engine = Ocr;
        if (engine == null) return "ocr-unavailable";
        using var file = File.OpenRead(path);
        WinPdf.PdfDocument pdf;
        try { pdf = Await(WinPdf.PdfDocument.LoadFromStreamAsync(file.AsRandomAccessStream())); }
        catch (Exception) { return "scanned"; }
        _retryBudget = RetryBudgetPerDocument;
        foreach (int number in pages.OrderBy(n => n))
        {
            if (number > pdf.PageCount) return "scanned";
            using var page = pdf.GetPage((uint)(number - 1));
            var lines = RecognizeBest(engine, scale =>
            {
                var size = Fit((uint)Math.Ceiling(page.Size.Width * OcrScale * scale), (uint)Math.Ceiling(page.Size.Height * OcrScale * scale));
                using var stream = new InMemoryRandomAccessStream();
                page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = size.ScaledWidth, DestinationHeight = size.ScaledHeight })
                    .AsTask().GetAwaiter().GetResult();
                var decoder = Await(BitmapDecoder.CreateAsync(stream));
                return Await(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
            }, preserveLayout: true);
            if (LettersAndDigits(lines) < MinCharsPerTextPage) return "scanned";
            recognized[number] = lines;
        }
        return null;
    }
}
