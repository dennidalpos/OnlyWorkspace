using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private static List<string> Recognize(OcrEngine engine, SoftwareBitmap bitmap) =>
        Await(engine.RecognizeAsync(bitmap)).Lines.Select(line => Cents.Replace(line.Text, "00")).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

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
        using (var file = File.OpenRead(path))
        {
            var decoder = Await(BitmapDecoder.CreateAsync(file.AsRandomAccessStream()));
            if (decoder.FrameCount > MaxImageFrames) return "scanned";
            for (uint index = 0; index < decoder.FrameCount; index++)
            {
                var frame = Await(decoder.GetFrameAsync(index));
                using var bitmap = Await(frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    Fit(frame.OrientedPixelWidth, frame.OrientedPixelHeight), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage));
                var lines = Recognize(engine, bitmap);
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
        foreach (int number in pages.OrderBy(n => n))
        {
            if (number > pdf.PageCount) return "scanned";
            using var page = pdf.GetPage((uint)(number - 1));
            var size = Fit((uint)Math.Ceiling(page.Size.Width * OcrScale), (uint)Math.Ceiling(page.Size.Height * OcrScale));
            using var stream = new InMemoryRandomAccessStream();
            page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = size.ScaledWidth, DestinationHeight = size.ScaledHeight })
                .AsTask().GetAwaiter().GetResult();
            var decoder = Await(BitmapDecoder.CreateAsync(stream));
            using var bitmap = Await(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
            var lines = Recognize(engine, bitmap);
            if (LettersAndDigits(lines) < MinCharsPerTextPage) return "scanned";
            recognized[number] = lines;
        }
        return null;
    }
}
