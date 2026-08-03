using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Analytics tab (Settings): most recent OCR / speak result with pipeline images
    /// (capture, prep, detect regions, crops) and step detail.
    /// Read-only snapshot from <see cref="OcrProcessor.LastResult"/>.
    /// Export packs the same snapshot to a zip (works in Release — no debug build needed).
    /// </summary>
    public sealed class frm_Analytics : Form
    {
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;
        private readonly Button? _btnClose;
        private readonly Button _btnRefresh;
        private readonly Button _btnExport;
        private readonly Label _lblSummary;
        private readonly Label _lblImagesHeader;
        private readonly FlowLayoutPanel _imageFlow;
        private readonly Panel _imageHost;
        private readonly RichTextBox _rtb;
        private readonly Label _lblStatus;

        /// <summary>Loaded PictureBox images to dispose on refresh.</summary>
        private readonly List<Image> _ownedImages = new();

        public frm_Analytics(bool embedded = false, Action? onRequestClose = null)
        {
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Analytics";
            if (_embedded)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = false;
                ControlBox = false;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.SizableToolWindow;
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(640, 640);
                ClientSize = new Size(720, 760);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9.5f);

            // ---- Bottom actions ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = UiTheme.BgBar,
            };
            _btnRefresh = MakePrimaryButton("Refresh");
            _btnRefresh.Click += (_, _) => ReloadFromSettings();
            bottom.Controls.Add(_btnRefresh);

            _btnExport = MakePrimaryButton("Export…");
            _btnExport.Click += (_, _) => ExportLastResult();
            bottom.Controls.Add(_btnExport);

            if (!_embedded)
            {
                _btnClose = MakePrimaryButton("Close");
                UiTheme.StyleButton(_btnClose);
                _btnClose.Click += (_, _) =>
                {
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                };
                bottom.Controls.Add(_btnClose);
            }
            void LayoutBottom()
            {
                int y = Math.Max(8, (bottom.ClientSize.Height - _btnRefresh.Height) / 2);
                _btnRefresh.Location = new Point(14, y);
                _btnExport.Location = new Point(_btnRefresh.Right + 10, y);
                if (_btnClose != null)
                {
                    _btnClose.Location = new Point(
                        Math.Max(14, bottom.ClientSize.Width - _btnClose.Width - 14), y);
                }
            }
            bottom.Resize += (_, _) => LayoutBottom();
            LayoutBottom();

            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Padding(16, 0, 12, 0),
                BackColor = UiTheme.BgStatus,
                Text = "No OCR run yet this session.",
            };

            // Intro strip
            var intro = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = UiTheme.BgRaised,
                Padding = new Padding(16, 10, 16, 10),
            };
            var introTitle = new Label
            {
                Text = "Most recent result",
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = UiTheme.Fg,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            _lblSummary = new Label
            {
                Text = "Speak a region to populate this view.",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            intro.Controls.Add(_lblSummary);
            intro.Controls.Add(introTitle);

            // Body: images (top) + detail text (fill)
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(10, 8, 8, 6),
                BackColor = UiTheme.Bg,
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // Favor the image gallery so pipeline thumbs are readable without scrolling as hard.
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));

            var imagesPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0),
            };
            _lblImagesHeader = new Label
            {
                Text = "PIPELINE IMAGES",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
            };
            _imageHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = UiTheme.BgDeep,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(4),
            };
            _imageFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = UiTheme.BgDeep,
                Padding = new Padding(6),
                Margin = new Padding(0),
            };
            _imageHost.Controls.Add(_imageFlow);
            imagesPanel.Controls.Add(_imageHost);
            imagesPanel.Controls.Add(_lblImagesHeader);

            var detailHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = UiTheme.Bg,
            };
            var lblDetail = new Label
            {
                Text = "PIPELINE DETAIL",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
            };
            _rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Bg,
                ForeColor = UiTheme.Fg,
                Font = new Font("Consolas", 9f),
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = true,
                HideSelection = true,
            };
            detailHost.Controls.Add(_rtb);
            detailHost.Controls.Add(lblDetail);

            body.Controls.Add(imagesPanel, 0, 0);
            body.Controls.Add(detailHost, 0, 1);

            Controls.Add(body);
            Controls.Add(intro);
            Controls.Add(_lblStatus);
            Controls.Add(bottom);

            Load += (_, _) =>
            {
                ReloadFromSettings();
                LayoutBottom();
                ActiveControl = _btnRefresh;
            };
            FormClosed += (_, _) => DisposeOwnedImages();

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape && !_embedded)
                {
                    e.Handled = true;
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                }
                else if (e.KeyCode == Keys.F5)
                {
                    e.Handled = true;
                    ReloadFromSettings();
                }
            };
        }

        /// <summary>Reload snapshot from <see cref="OcrProcessor.LastResult"/>.</summary>
        public void ReloadFromSettings()
        {
            var result = OcrProcessor.LastResult;
            if (result == null)
            {
                _lblSummary.Text = "Speak a region to populate this view.";
                _lblStatus.Text = "No OCR run yet this session.";
                _lblStatus.ForeColor = UiTheme.FgMuted;
                _lblImagesHeader.Text = "PIPELINE IMAGES";
                _btnExport.Enabled = false;
                ClearImageGallery();
                FillEmptyRtf();
                return;
            }

            _btnExport.Enabled = true;

            var b = result.CaptureBounds;
            string when = result.CompletedLocal.ToString("yyyy-MM-dd HH:mm:ss");
            string size = $"{Math.Max(0, b.Width)}×{Math.Max(0, b.Height)}";
            string origin = $"({b.X}, {b.Y})";
            string outcome = result.Unreadable ? "unreadable" : "ok";
            int imgCount = result.Images?.Count ?? 0;

            string fogSummary = FormatFogAnalyticsShort(result);
            _lblSummary.Text =
                $"{when}  ·  {result.Shape}  ·  {size} at {origin}  ·  {outcome}" +
                (imgCount > 0 ? $"  ·  {imgCount} image{(imgCount == 1 ? "" : "s")}" : "") +
                (string.IsNullOrEmpty(fogSummary) ? "" : $"  ·  {fogSummary}");
            _lblStatus.Text = result.Unreadable
                ? "Last run produced no usable text (or was cancelled mid-plan). Double-click an image to enlarge · Export… for a zip."
                : "Last run recorded. Double-click an image to enlarge · Export… packs detail + PNGs · Refresh / F5 after the next speak.";
            _lblStatus.ForeColor = result.Unreadable
                ? UiTheme.Warn
                : UiTheme.Ok;

            PopulateImageGallery(result.Images);
            FillResultRtf(result);
            _rtb.Select(0, 0);
            _rtb.ScrollToCaret();
        }

        /// <summary>
        /// Save the in-memory last OCR snapshot as a zip (last_ocr.txt + pipeline PNGs).
        /// Same data Analytics already holds — works in Release without a debug build.
        /// </summary>
        private void ExportLastResult()
        {
            var result = OcrProcessor.LastResult;
            if (result == null)
            {
                UiMessageBox.Show(
                    this,
                    "No OCR run to export yet.\nSpeak a region first, then try Export again.",
                    "Export analytics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string stamp = result.CompletedLocal.ToString("yyyyMMdd_HHmmss");
            using var dlg = new SaveFileDialog
            {
                Title = "Export analytics debug package",
                Filter = "Zip archive (*.zip)|*.zip",
                DefaultExt = "zip",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"SpeakRect-analytics-{stamp}.zip",
            };

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                WriteAnalyticsExportZip(dlg.FileName, result);
                _lblStatus.Text = $"Exported debug package → {dlg.FileName}";
                _lblStatus.ForeColor = UiTheme.Ok;

                // Offer to open the containing folder (Explorer selects the zip).
                var open = UiMessageBox.Show(
                    this,
                    $"Saved:\n{dlg.FileName}\n\nOpen containing folder?",
                    "Export analytics",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (open == DialogResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{dlg.FileName}\"",
                            UseShellExecute = true,
                        });
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Export failed.";
                _lblStatus.ForeColor = UiTheme.Warn;
                UiMessageBox.Show(
                    this,
                    "Could not write the export zip.\n\n" + ex.Message,
                    "Export analytics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Build a self-contained debug zip from <see cref="OcrLastResult"/>
        /// (no disk dumps required — uses memory snapshot only).
        /// </summary>
        public static void WriteAnalyticsExportZip(string zipPath, OcrLastResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (string.IsNullOrWhiteSpace(zipPath))
                throw new ArgumentException("Zip path required.", nameof(zipPath));

            string? dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Overwrite cleanly
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using var fs = new FileStream(
                zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            // last_ocr.txt — same shape as Debug-only dump
            {
                var entry = zip.CreateEntry("last_ocr.txt", CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                w.Write(result.SpokenText ?? "");
                w.Write("\n\n--- detail ---\n");
                w.Write(result.Detail ?? "");
                if (!string.IsNullOrEmpty(result.Detail) &&
                    !result.Detail.EndsWith('\n'))
                    w.Write('\n');
            }

            // meta.txt — identity for support without reading binaries
            {
                var entry = zip.CreateEntry("meta.txt", CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                w.WriteLine(FormatExportBuildStamp());
                w.WriteLine($"when={result.CompletedLocal:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"shape={result.Shape}");
                var b = result.CaptureBounds;
                w.WriteLine(
                    $"bounds=x={b.X},y={b.Y},w={Math.Max(0, b.Width)},h={Math.Max(0, b.Height)}");
                w.WriteLine($"unreadable={(result.Unreadable ? "yes" : "no")}");
                w.WriteLine($"spoken_chars={(result.SpokenText ?? "").Length}");
                w.WriteLine($"detail_chars={(result.Detail ?? "").Length}");
                w.WriteLine(
                    $"fog_amount_used={result.FogAmountUsed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
                int n = result.Images?.Count ?? 0;
                w.WriteLine($"images={n}");
                if (n > 0)
                {
                    for (int i = 0; i < result.Images!.Count; i++)
                    {
                        var img = result.Images[i];
                        w.WriteLine(
                            $"  [{i}] key={img.Key} title={img.Title} " +
                            $"{img.Width}x{img.Height} bytes={img.PngBytes?.Length ?? 0}");
                    }
                }
            }

            // Pipeline PNGs (full resolution, same bytes as Analytics gallery)
            if (result.Images != null)
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < result.Images.Count; i++)
                {
                    var img = result.Images[i];
                    if (img.PngBytes == null || img.PngBytes.Length == 0)
                        continue;

                    string baseName = SanitizeExportFileName(
                        string.IsNullOrWhiteSpace(img.Key) ? img.Title : img.Key);
                    if (string.IsNullOrEmpty(baseName))
                        baseName = "image";
                    string fileName = $"{i:00}_{baseName}.png";
                    // Deduplicate if keys collide
                    string candidate = fileName;
                    int suffix = 2;
                    while (!usedNames.Add(candidate))
                    {
                        candidate = $"{i:00}_{baseName}_{suffix}.png";
                        suffix++;
                    }

                    var entry = zip.CreateEntry(
                        "images/" + candidate, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(img.PngBytes, 0, img.PngBytes.Length);
                }
            }
        }

        private static string FormatExportBuildStamp()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetName();
                string ver =
                    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion
                    ?? name.Version?.ToString()
                    ?? "?";
                int plus = ver.IndexOf('+');
                if (plus > 0)
                    ver = ver[..plus];

                string path = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "SpeakRect.exe");
                string writeTime = File.Exists(path)
                    ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss")
                    : "unknown";

#if DEBUG
                const string config = "Debug";
#else
                const string config = "Release";
#endif
                return $"build={name.Name ?? "SpeakRect"} {ver} {config} write={writeTime}";
            }
            catch
            {
                return "build=unknown";
            }
        }

        private static string SanitizeExportFileName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                    sb.Append(c);
                else if (char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '|' or '·')
                    sb.Append('_');
                // drop other punctuation
            }
            string s = sb.ToString().Trim('_');
            while (s.Contains("__", StringComparison.Ordinal))
                s = s.Replace("__", "_", StringComparison.Ordinal);
            if (s.Length > 48)
                s = s[..48].TrimEnd('_');
            return s;
        }

        private void ClearImageGallery()
        {
            DisposeOwnedImages();
            _imageFlow.SuspendLayout();
            _imageFlow.Controls.Clear();
            _imageFlow.ResumeLayout(true);
        }

        private void DisposeOwnedImages()
        {
            foreach (var img in _ownedImages)
            {
                try { img.Dispose(); } catch { /* ignore */ }
            }
            _ownedImages.Clear();
        }

        private void PopulateImageGallery(IReadOnlyList<OcrResultImage>? images)
        {
            ClearImageGallery();
            if (images == null || images.Count == 0)
            {
                _lblImagesHeader.Text = "PIPELINE IMAGES  ·  none yet";
                var empty = new Label
                {
                    Text = "No pipeline images for this run.\nSpeak a region to capture them.",
                    AutoSize = true,
                    ForeColor = UiTheme.FgMuted,
                    Font = new Font("Segoe UI", 9f),
                    Margin = new Padding(8, 12, 8, 8),
                    MaximumSize = new Size(420, 0),
                };
                _imageFlow.Controls.Add(empty);
                return;
            }

            _lblImagesHeader.Text =
                $"PIPELINE IMAGES  ·  {images.Count}  ·  full pipe res (same as live)  ·  double-click to enlarge";

            _imageFlow.SuspendLayout();
            foreach (var entry in images)
            {
                if (entry.PngBytes == null || entry.PngBytes.Length == 0)
                    continue;

                Image? img = null;
                try
                {
                    using var ms = new MemoryStream(entry.PngBytes);
                    // Clone so the stream can close
                    using var temp = Image.FromStream(ms);
                    img = new Bitmap(temp);
                }
                catch
                {
                    continue;
                }

                _ownedImages.Add(img);
                _imageFlow.Controls.Add(MakeImageCard(entry, img));
            }
            _imageFlow.ResumeLayout(true);
        }

        private Control MakeImageCard(OcrResultImage entry, Image img)
        {
            // Large enough to read pipeline frames (capture/prep/regions/crops) at a glance.
            const int thumbW = 260;
            const int thumbH = 180;

            var card = new Panel
            {
                Width = thumbW + 16,
                Height = thumbH + 44,
                Margin = new Padding(8),
                BackColor = UiTheme.BgRaised,
                Padding = new Padding(6, 6, 6, 4),
                Cursor = Cursors.Hand,
            };

            var pb = new PictureBox
            {
                Width = thumbW,
                Height = thumbH,
                Location = new Point(6, 6),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiTheme.BgDeep,
                Image = img,
                Cursor = Cursors.Hand,
            };

            string dim =
                entry.SourceWidth != entry.Width || entry.SourceHeight != entry.Height
                    ? $"{entry.SourceWidth}×{entry.SourceHeight} → {entry.Width}×{entry.Height}"
                    : $"{entry.Width}×{entry.Height}";

            var title = new Label
            {
                Text = entry.Title,
                AutoEllipsis = true,
                Location = new Point(6, thumbH + 10),
                Width = thumbW,
                Height = 16,
                ForeColor = UiTheme.Fg,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var sub = new Label
            {
                Text = dim,
                AutoEllipsis = true,
                Location = new Point(6, thumbH + 26),
                Width = thumbW,
                Height = 14,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Segoe UI", 7.5f),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            void OpenPreview(object? s, EventArgs e) =>
                ShowImagePreview(entry.Title, img, dim);

            pb.DoubleClick += OpenPreview;
            card.DoubleClick += OpenPreview;
            title.DoubleClick += OpenPreview;
            sub.DoubleClick += OpenPreview;
            // Single-click also fine for discoverability
            pb.Click += (_, e) =>
            {
                if (e is MouseEventArgs me && me.Clicks >= 2)
                    return;
                // leave single-click for focus; enlarge on double-click only
            };

            // Right-click: enlarge immediately
            void ContextOpen(object? s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                    ShowImagePreview(entry.Title, img, dim);
            }
            pb.MouseUp += ContextOpen;
            card.MouseUp += ContextOpen;

            card.Controls.Add(pb);
            card.Controls.Add(title);
            card.Controls.Add(sub);
            return card;
        }

        private void ShowImagePreview(string title, Image source, string dimLabel)
        {
            using var dlg = new Form
            {
                Text = $"{title}  ·  {dimLabel}",
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                MinimumSize = new Size(360, 280),
                ClientSize = new Size(
                    Math.Min(900, Math.Max(420, source.Width + 40)),
                    Math.Min(720, Math.Max(320, source.Height + 60))),
                BackColor = UiTheme.BgDeep,
                ForeColor = UiTheme.Fg,
                ShowInTaskbar = false,
                TopMost = true,
                KeyPreview = true,
                Font = new Font("Segoe UI", 9f),
            };
            // Dark DWM title bar + chrome (matches Settings / rest of the app).
            UiTheme.ApplyForm(dlg);
            dlg.BackColor = UiTheme.BgDeep;

            var pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiTheme.BgDeep,
                Image = source,
            };
            var hint = new Label
            {
                Text = "Esc to close",
                Dock = DockStyle.Bottom,
                Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = UiTheme.FgMuted,
                BackColor = UiTheme.BgStatus,
            };
            dlg.Controls.Add(pb);
            dlg.Controls.Add(hint);
            dlg.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    dlg.Close();
                }
            };
            dlg.ShowDialog(FindForm() ?? this);
        }

        private void FillEmptyRtf()
        {
            var sb = new StringBuilder();
            AppendRtfHeader(sb);
            sb.Append(@"\cf2\b ");
            sb.Append(RtfEscape("NO RESULT YET"));
            sb.Append(@"\b0\cf1\par\par ");
            sb.Append(RtfEscape(
                "After you speak a region (Enter on the overlay, a region hotkey, or Follow), " +
                "this tab shows the spoken text, pipeline images (capture, OCR prep, detect fog, " +
                "box map, and Local-LLM island/crop frames), and step timings."));
            sb.Append(@"\par\par\cf6 ");
            sb.Append(RtfEscape(
                "Tip: press Refresh (or F5) after a speak if this window was already open. " +
                "Double-click a thumbnail to enlarge. Export… saves detail + pipeline PNGs as a zip " +
                "(works in Release — no debug build required)."));
            sb.Append(@"\cf1\par }");
            _rtb.Rtf = sb.ToString();
            _rtb.Select(0, 0);
            _rtb.ScrollToCaret();
        }

        private void FillResultRtf(OcrLastResult result)
        {
            var sb = new StringBuilder();
            AppendRtfHeader(sb);

            Section(sb, "SPOKEN TEXT");
            if (result.Unreadable || string.IsNullOrWhiteSpace(result.SpokenText))
            {
                sb.Append(@"\cf3\i ");
                sb.Append(RtfEscape("(unreadable)"));
                sb.Append(@"\i0\cf1\par\par ");
            }
            else
            {
                foreach (string line in result.SpokenText.Replace("\r\n", "\n").Split('\n'))
                {
                    sb.Append(@"\cf4 ");
                    sb.Append(RtfEscape(line));
                    sb.Append(@"\cf1\par ");
                }
                sb.Append(@"\par ");
            }

            Section(sb, "CAPTURE");
            Row(sb, "When", result.CompletedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
            Row(sb, "Shape", result.Shape);
            var b = result.CaptureBounds;
            Row(sb, "Bounds",
                $"x={b.X}, y={b.Y}, w={Math.Max(0, b.Width)}, h={Math.Max(0, b.Height)}");
            int n = result.Images?.Count ?? 0;
            Row(sb, "Images", n == 0 ? "none" : n.ToString());
            if (n > 0)
            {
                string list = string.Join(", ", result.Images!.Select(i => i.Title));
                Row(sb, "Slots", list);
            }
            sb.Append(@"\par ");

            Section(sb, "DETECT FOG");
            if (result.FogAmountUsed > 0.001f)
            {
                Row(sb, "Mode", "fixed");
                Row(sb, "Amount", result.FogAmountUsed.ToString("0.00"));
            }
            else
            {
                Row(sb, "Mode", "off / not used");
                Row(sb, "Amount", "0.00");
            }
            sb.Append(@"\par ");

            Section(sb, "PIPELINE DETAIL");
            if (string.IsNullOrWhiteSpace(result.Detail))
            {
                sb.Append(@"\cf6\i ");
                sb.Append(RtfEscape("(no detail recorded)"));
                sb.Append(@"\i0\cf1\par ");
            }
            else
            {
                // Never surface internal engine product names in the UI.
                string detailUi = UiTheme.SanitizeUiEngineNames(result.Detail);
                foreach (string line in detailUi.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.StartsWith("--- ", StringComparison.Ordinal) ||
                        line.StartsWith("pipeline=", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("strategy=", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("winner=", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("settings:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(@"\cf5 ");
                        sb.Append(RtfEscape(line));
                        sb.Append(@"\cf1\par ");
                    }
                    else if (line.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) ||
                             line.TrimStart().StartsWith("(sum of steps)", StringComparison.Ordinal))
                    {
                        sb.Append(@"\b\cf2 ");
                        sb.Append(RtfEscape(line));
                        sb.Append(@"\b0\cf1\par ");
                    }
                    else
                    {
                        sb.Append(@"\cf1 ");
                        sb.Append(RtfEscape(line));
                        sb.Append(@"\par ");
                    }
                }
            }

            sb.Append(@"}");
            _rtb.Rtf = sb.ToString();
        }

        /// <summary>Short summary fragment for the intro strip (empty when not applicable).</summary>
        private static string FormatFogAnalyticsShort(OcrLastResult result)
        {
            if (result.FogAmountUsed > 0.001f)
                return $"fog={result.FogAmountUsed:0.00}";
            return "";
        }

        private static void AppendRtfHeader(StringBuilder sb)
        {
            sb.Append(@"{\rtf1\ansi\deff0");
            sb.Append(@"{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}");
            sb.Append(@"{\colortbl;");
            sb.Append(@"\red236\green236\blue240;");   // 1 body
            sb.Append(@"\red255\green152\blue48;");    // 2 section (orange)
            sb.Append(@"\red255\green170\blue70;");    // 3 warn
            sb.Append(@"\red130\green190\blue110;");   // 4 spoken (ok)
            sb.Append(@"\red255\green168\blue72;");    // 5 highlight
            sb.Append(@"\red150\green150\blue158;");   // 6 muted
            sb.Append(@"}");
            sb.Append(@"\fs18\cf1\f1 ");
        }

        private static void Section(StringBuilder sb, string title)
        {
            sb.Append(@"\par\sb80\sa60\b\fs20\f0\cf2 ");
            sb.Append(RtfEscape(title));
            sb.Append(@"\b0\fs18\f1\cf1\par\sa40 ");
        }

        private static void Row(StringBuilder sb, string key, string value)
        {
            sb.Append(@"\sb20\sa20\f0\b\cf5 ");
            sb.Append(RtfEscape(key));
            sb.Append(@"\b0\cf1\~\emdash\~\f1 ");
            sb.Append(RtfEscape(value));
            sb.Append(@"\par ");
        }

        private static string RtfEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\n': sb.Append(@"\par "); break;
                    default:
                        if (ch > 127)
                            sb.Append(@"\u").Append((int)ch).Append('?');
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static Button MakePrimaryButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(100, 32),
                Padding = new Padding(14, 4, 14, 4),
                Font = new Font("Segoe UI", 9f),
            };
            UiTheme.StylePrimaryButton(btn);
            return btn;
        }

    }
}
