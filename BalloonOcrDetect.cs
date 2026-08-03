using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SpeakRect
{
    /// <summary>
    /// Windows.Media.Ocr balloon / line detect: engine acquisition, one-pass
    /// recognize + line cluster, and pure box helpers. Multi-pass / orphan /
    /// mega-split orchestration remains on <see cref="OcrProcessor"/>.
    /// </summary>
    public static class BalloonOcrDetect
    {
        public const int MinLineBoxSize = 3;
        public const int MinClusterSize = 6;
        public const int MinWinOcrAlnumChars = 2;
        public const int MaxRawLineLogEntries = 16;

        /// <summary>
        /// Internal WinOCR line-group factors (median line height × this).
        /// Not a user setting — multi-line speech must group into one island.
        /// </summary>
        private const double LineGroupGapXFactor = 1.05;
        private const double LineGroupGapYFactor = 1.15;

        private static OcrEngine? _engine;
        private static bool _tried;

        /// <summary>
        /// Lazy OCR engine from profile languages, else any available pack, else en-US.
        /// </summary>
        public static OcrEngine? GetEngine()
        {
            if (_tried)
                return _engine;

            _tried = true;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine != null)
                {
                    Debug.WriteLine(
                        $"[OCR] engine ready: {_engine.RecognizerLanguage.LanguageTag}");
                    return _engine;
                }

                foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                {
                    _engine = OcrEngine.TryCreateFromLanguage(lang);
                    if (_engine != null)
                    {
                        Debug.WriteLine(
                            $"[OCR] engine ready (fallback): {lang.LanguageTag}");
                        return _engine;
                    }
                }

                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
                if (_engine != null)
                    Debug.WriteLine("[OCR] engine ready (en-US)");
                else
                    Debug.WriteLine("[OCR] no recognizer language packs available");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] init failed: {ex.Message}");
                _engine = null;
            }

            return _engine;
        }

        /// <summary>
        /// True when inflated <paramref name="a"/> intersects <paramref name="b"/>.
        /// </summary>
        public static bool BoxesNear(Rectangle a, Rectangle b, int gapX, int gapY)
        {
            var expanded = Rectangle.Inflate(a, gapX, gapY);
            return expanded.IntersectsWith(b);
        }

        /// <summary>
        /// Map a box from detect-bitmap space back to pipeline capture space.
        /// </summary>
        public static Rectangle MapRectToCapture(
            Rectangle detectRect, double scale, int capW, int capH)
        {
            if (scale <= 0) scale = 1;
            int x = (int)Math.Floor(detectRect.X / scale);
            int y = (int)Math.Floor(detectRect.Y / scale);
            int w = (int)Math.Ceiling(detectRect.Width / scale);
            int h = (int)Math.Ceiling(detectRect.Height / scale);
            x = Math.Clamp(x, 0, Math.Max(0, capW - 1));
            y = Math.Clamp(y, 0, Math.Max(0, capH - 1));
            w = Math.Min(w, capW - x);
            h = Math.Min(h, capH - y);
            return new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        /// <summary>
        /// Cluster nearby WinOCR lines into one island (multi-line balloon).
        /// Does not intentionally join separate speech balloons — gaps are modest.
        /// Internal engine step only (no Balloons line-merge knobs).
        /// </summary>
        public static List<DetectedTextRegion> ClusterTextBoxesWithText(
            List<(Rectangle Box, string Text)> lines,
            int captureW,
            int captureH)
        {
            if (lines.Count == 0)
                return new List<DetectedTextRegion>();

            int medianH = lines
                .Select(l => l.Box.Height)
                .OrderBy(h => h)
                .ElementAt(lines.Count / 2);
            int gapX = Math.Max(12, (int)(medianH * LineGroupGapXFactor));
            int gapY = Math.Max(10, (int)(medianH * LineGroupGapYFactor));

            int n = lines.Count;
            var parent = Enumerable.Range(0, n).ToArray();

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (BoxesNear(lines[i].Box, lines[j].Box, gapX, gapY))
                        Union(i, j);
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    groups[r] = list;
                }
                list.Add(i);
            }

            double minArea = Math.Max(36, captureW * captureH * 0.00002);
            var regions = new List<DetectedTextRegion>();

            foreach (var idxs in groups.Values)
            {
                var orderedIdx = idxs
                    .OrderBy(i => lines[i].Box.Top + lines[i].Box.Height / 2.0)
                    .ThenBy(i => lines[i].Box.Left)
                    .ToList();

                Rectangle bounds = lines[orderedIdx[0]].Box;
                var textParts = new List<string>();
                foreach (int i in orderedIdx)
                {
                    bounds = Rectangle.Union(bounds, lines[i].Box);
                    if (!IsJunkWinOcrText(lines[i].Text))
                        textParts.Add(lines[i].Text.Trim());
                }

                string text = Regex.Replace(string.Join(" ", textParts), @"\s+", " ").Trim();

                if (IsJunkWinOcrText(text))
                    continue;
                if (bounds.Width < MinClusterSize || bounds.Height < MinClusterSize)
                    continue;
                if (bounds.Width * bounds.Height < minArea && SpeechCleaner.CountAlnum(text) < 4)
                    continue;

                bounds.Intersect(new Rectangle(0, 0, captureW, captureH));
                if (bounds.Width < 1 || bounds.Height < 1)
                    continue;

                regions.Add(new DetectedTextRegion { Bounds = bounds, WinOcrText = text });
            }

            return regions;
        }

        /// <summary>
        /// True when WinOCR text is empty or below the alphanumeric floor.
        /// </summary>
        public static bool IsJunkWinOcrText(string? text)
            => ComicBestOfFusion.IsJunkWinOcrText(text, MinWinOcrAlnumChars);

        /// <summary>
        /// GDI Bitmap → WinRT SoftwareBitmap (BGRA8) for Windows.Media.Ocr.
        /// </summary>
        public static async Task<SoftwareBitmap?> ToSoftwareBitmapAsync(Bitmap bitmap)
        {
            try
            {
                using var ms = new MemoryStream();
                if (bitmap.PixelFormat == PixelFormat.Format32bppArgb)
                {
                    bitmap.Save(ms, ImageFormat.Png);
                }
                else
                {
                    using var converted = new Bitmap(
                        bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(converted))
                    {
                        g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                    }
                    converted.Save(ms, ImageFormat.Png);
                }

                ms.Position = 0;
                byte[] bytes = ms.ToArray();

                using var raStream = new InMemoryRandomAccessStream();
                using (var output = raStream.GetOutputStreamAt(0))
                {
                    using var writer = new DataWriter(output);
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await output.FlushAsync();
                }
                raStream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(raStream);
                return await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinOCR] Bitmap→SoftwareBitmap failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One detect pass: optional scale (via <paramref name="buildDetectBitmap"/>),
        /// WinRT OCR, map lines to capture coords, cluster, reading-order sort.
        /// </summary>
        /// <param name="buildDetectBitmap">
        /// Builds the bitmap OCR sees and the scale factor mapping detect → capture.
        /// Caller owns scaling implementation (Lanczos/bicubic live in <see cref="OcrProcessor"/>).
        /// Returned bitmap is disposed by this method.
        /// </param>
        public static async Task<List<DetectedTextRegion>> RunPassAsync(
            OcrEngine engine,
            Bitmap capture,
            double requestedScale,
            Func<Bitmap, double, (Bitmap DetectBmp, double UsedScale)> buildDetectBitmap,
            CancellationToken token,
            StringBuilder log,
            Action<Bitmap>? onDebugDetectBmp = null)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));
            if (capture == null)
                throw new ArgumentNullException(nameof(capture));
            if (buildDetectBitmap == null)
                throw new ArgumentNullException(nameof(buildDetectBitmap));
            log ??= new StringBuilder();

            var (detectBmp, usedScale) = buildDetectBitmap(capture, requestedScale);
            using (detectBmp)
            {
                log.AppendLine(
                    $"  detectBmp {detectBmp.Width}x{detectBmp.Height} usedScale={usedScale:F2} " +
                    "(from pipeline tone)");
                onDebugDetectBmp?.Invoke(detectBmp);

                using var softwareBitmap = await ToSoftwareBitmapAsync(detectBmp);
                if (softwareBitmap == null)
                    return new List<DetectedTextRegion>();

                token.ThrowIfCancellationRequested();
                var result = await engine.RecognizeAsync(softwareBitmap).AsTask(token);

                var lines = new List<(Rectangle Box, string Text)>();
                int junkSkipped = 0;
                foreach (var line in result.Lines)
                {
                    if (IsJunkWinOcrText(line.Text) || line.Words == null || line.Words.Count == 0)
                    {
                        junkSkipped++;
                        continue;
                    }

                    Rectangle? union = null;
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        int wx = Math.Max(0, (int)Math.Floor(r.X));
                        int wy = Math.Max(0, (int)Math.Floor(r.Y));
                        int ww = Math.Max(1, (int)Math.Ceiling(r.Width));
                        int wh = Math.Max(1, (int)Math.Ceiling(r.Height));
                        var wordRect = new Rectangle(wx, wy, ww, wh);
                        union = union == null ? wordRect : Rectangle.Union(union.Value, wordRect);
                    }

                    if (union == null)
                        continue;

                    var box = union.Value;
                    if (box.Width < MinLineBoxSize || box.Height < MinLineBoxSize)
                        continue;

                    lines.Add((box, line.Text.Trim()));
                }

                log.AppendLine(
                    $"  rawLines={lines.Count} junkSkipped={junkSkipped} " +
                    $"ocrTextLen={result.Text?.Length ?? 0}");
                int logN = Math.Min(lines.Count, MaxRawLineLogEntries);
                for (int i = 0; i < logN; i++)
                {
                    var (box, text) = lines[i];
                    string preview = text.Length <= 48 ? text : text.Substring(0, 48) + "\u2026";
                    preview = preview.Replace('\r', ' ').Replace('\n', ' ');
                    log.AppendLine(
                        $"  L{i} \"{preview}\" @{box.X},{box.Y} {box.Width}x{box.Height}");
                }
                if (lines.Count > MaxRawLineLogEntries)
                    log.AppendLine($"  … +{lines.Count - MaxRawLineLogEntries} more lines");

                if (lines.Count == 0)
                    return new List<DetectedTextRegion>();

                var mapped = lines.Select(l =>
                {
                    var b = MapRectToCapture(l.Box, usedScale, capture.Width, capture.Height);
                    return (Box: b, Text: l.Text);
                }).Where(l => l.Box.Width >= MinLineBoxSize && l.Box.Height >= MinLineBoxSize)
                  .ToList();

                var clusters = ClusterTextBoxesWithText(
                    mapped, capture.Width, capture.Height);
                return ComicRegionGeometry.SortComicReadingOrderRegions(clusters);
            }
        }
    }
}
