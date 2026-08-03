using System.IO.Compression;
using System.Text;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class AnalyticsExportTests
{
    [Fact]
    public void WriteAnalyticsExportZip_writes_last_ocr_meta_and_images()
    {
        byte[] tinyPng = MinimalPngBytes();
        var result = new OcrLastResult
        {
            CompletedLocal = new DateTime(2026, 7, 31, 12, 0, 0),
            CaptureBounds = new System.Drawing.Rectangle(10, 20, 300, 200),
            Shape = "Rectangle",
            SpokenText = "hello world",
            Detail = "strategy=test\nstep=ok\ndetect-fog=fixed amount=0.30",
            Unreadable = false,
            FogAmountUsed = 0.30f,
            Images = new[]
            {
                new OcrResultImage
                {
                    Key = "capture",
                    Title = "Capture",
                    Width = 2,
                    Height = 2,
                    SourceWidth = 2,
                    SourceHeight = 2,
                    PngBytes = tinyPng,
                },
            },
        };

        string zipPath = Path.Combine(Path.GetTempPath(), $"SpeakRect-analytics-test-{Guid.NewGuid():N}.zip");
        try
        {
            frm_Analytics.WriteAnalyticsExportZip(zipPath, result);
            Assert.True(File.Exists(zipPath));

            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var lastOcr = zip.GetEntry("last_ocr.txt");
            Assert.NotNull(lastOcr);
            using (var r = new StreamReader(lastOcr!.Open(), Encoding.UTF8))
            {
                string body = r.ReadToEnd();
                Assert.Contains("hello world", body, StringComparison.Ordinal);
                Assert.Contains("--- detail ---", body, StringComparison.Ordinal);
                Assert.Contains("strategy=test", body, StringComparison.Ordinal);
            }

            var meta = zip.GetEntry("meta.txt");
            Assert.NotNull(meta);
            using (var r = new StreamReader(meta!.Open(), Encoding.UTF8))
            {
                string body = r.ReadToEnd();
                Assert.Contains("shape=Rectangle", body, StringComparison.Ordinal);
                Assert.Contains("images=1", body, StringComparison.Ordinal);
                Assert.Contains("key=capture", body, StringComparison.Ordinal);
                Assert.Contains("fog_amount_used=0.3", body, StringComparison.Ordinal);
            }

            var img = zip.GetEntry("images/00_capture.png");
            Assert.NotNull(img);
            Assert.Equal(tinyPng.Length, img!.Length);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
        }
    }

    /// <summary>1×1 transparent PNG (valid, tiny).</summary>
    private static byte[] MinimalPngBytes()
    {
        // Precomputed 1x1 RGBA PNG
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }
}
