using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Settings tab: capture image pipeline (letterbox / upscale / gray / tone).
    /// Live stage preview. Profile-backed via [IMAGE_PREP].
    /// </summary>
    public sealed class frm_ImagePrep : Form
    {
        private readonly CheckBox _chkPrepEnabled;
        private readonly CheckBox _chkLetterbox;
        private readonly TrackBar _trkLetterboxPad;
        private readonly Label _lblLetterboxPadVal;
        private readonly TrackBar _trkLetterboxBlack;
        private readonly Label _lblLetterboxBlackVal;
        private readonly TrackBar _trkLetterboxWhite;
        private readonly Label _lblLetterboxWhiteVal;

        private readonly TrackBar _trkUpscale;
        private readonly Label _lblUpscaleVal;

        private readonly CheckBox _chkLlmSendDownscale;
        private readonly TrackBar _trkLlmSendMaxEdge;
        private readonly Label _lblLlmSendMaxEdgeVal;

        private readonly CheckBox _chkGray;
        private readonly TrackBar _trkInkWeight;
        private readonly Label _lblInkWeightVal;

        private readonly TrackBar _trkDenoiseR;
        private readonly Label _lblDenoiseRVal;
        private readonly TrackBar _trkDenoiseSigma;
        private readonly Label _lblDenoiseSigmaVal;

        private readonly CheckBox _chkAutoLevels;
        private readonly TrackBar _trkLevelsLow;
        private readonly Label _lblLevelsLowVal;
        private readonly TrackBar _trkLevelsHigh;
        private readonly Label _lblLevelsHighVal;
        private readonly TrackBar _trkLevelsMinRange;
        private readonly Label _lblLevelsMinRangeVal;

        private readonly TrackBar _trkSharpenAmt;
        private readonly Label _lblSharpenAmtVal;
        private readonly TrackBar _trkSharpenPasses;
        private readonly Label _lblSharpenPassesVal;

        private readonly PictureBox _preview;
        private readonly Label _lblPreviewStatus;
        private readonly TextBox _txtDetail;
        private readonly Button _btnOpenImage;
        private readonly Button _btnUseLast;
        private readonly Button _btnSnapRegion;
        private readonly Button _btnPreview;
        private readonly Button _btnReset;
        private readonly Label _lblStatus;
        private readonly ThemeProgressBar _progress;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly Func<Task<(Bitmap? Bitmap, string Error)>>? _onCaptureActiveRegion;
        private readonly bool _embedded;
        private bool _snapBusy;

        private bool _loading;
        private Bitmap? _sourceImage;
        private Bitmap? _previewImage;
        private string _sourceLabel = "(no image)";
        /// <summary>When preview is "last capture", stamp of <see cref="OcrProcessor.LastResult"/> we loaded.</summary>
        private DateTime _lastCaptureStamp;
        private readonly System.Windows.Forms.Timer _liveTimer;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private int _liveGen;
        private bool _diskSavePending;
        /// <summary>
        /// Live + button preview run the full image prep pipe and show the
        /// live OCR input (tone). Default and ComicBook share the same prep.
        /// </summary>
        private const string FullPipelineStage = "tone";

        public frm_ImagePrep(
            bool embedded = false,
            Action? onRequestClose = null,
            Func<Task<(Bitmap? Bitmap, string Error)>>? onCaptureActiveRegion = null)
        {
            _embedded = embedded;
            _onRequestClose = onRequestClose;
            _onCaptureActiveRegion = onCaptureActiveRegion;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Image";
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
                MinimumSize = new Size(720, 560);
                ClientSize = new Size(920, 720);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9f);
            UiTheme.ApplyForm(this);

            // Debounced live preview — always full pipeline (same as Preview / F5).
            _liveTimer = new System.Windows.Forms.Timer { Interval = 160 };
            _liveTimer.Tick += (_, _) =>
            {
                _liveTimer.Stop();
                if (_sourceImage == null || IsDisposed) return;
                RunPipelinePreview(fromLive: true);
            };

            // Memory knobs update every tick; disk write is debounced so drag stays snappy.
            _diskSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diskSaveTimer.Tick += (_, _) =>
            {
                _diskSaveTimer.Stop();
                FlushDiskSave(force: false);
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = UiTheme.Bg,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 6, 0),
            };
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 4, 4),
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int row = 0;
            void AddRow(Control label, Control field, int height = 36)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.Controls.Add(label, 0, row);
                body.Controls.Add(field, 1, row);
                row++;
            }
            void AddFull(Control c, int height)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.SetColumnSpan(c, 2);
                body.Controls.Add(c, 0, row);
                row++;
            }

            // Section rows taller than the bold label so the previous multi-line
            // hint does not collide with the next header.
            AddFull(MakeSection("PICTURE CLEANUP"), 26);
            AddFull(MakeHint(
                "Trim edges, resize, and clean the picture before text is read. " +
                "Same steps as when you speak. Turn off to use the raw capture."),
                52);

            _chkPrepEnabled = MakeCheck("Clean up the picture before reading");
            _chkPrepEnabled.Checked = true;
            _chkPrepEnabled.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkPrepEnabled, 28);
            AddFull(MakeHint("Off = use the capture as captured, with no cleanup."), 32);

            // Letterbox
            AddFull(MakeSection("TRIM BARS"), 28);
            _chkLetterbox = MakeCheck("Trim black or white edges");
            _chkLetterbox.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkLetterbox, 28);
            _trkLetterboxPad = MakeTrack(0, 32, 3);
            _lblLetterboxPadVal = MakeValueLabel();
            AddRow(MakeLabel("Keep border"), WrapTrack(_trkLetterboxPad, _lblLetterboxPadVal), 42);
            _trkLetterboxBlack = MakeTrack(0, 255, 80);
            _lblLetterboxBlackVal = MakeValueLabel();
            AddRow(MakeLabel("Dark edges"), WrapTrack(_trkLetterboxBlack, _lblLetterboxBlackVal), 42);
            _trkLetterboxWhite = MakeTrack(0, 255, AppSettings.DefaultImageLetterboxWhite);
            _lblLetterboxWhiteVal = MakeValueLabel();
            AddRow(MakeLabel("Light edges"), WrapTrack(_trkLetterboxWhite, _lblLetterboxWhiteVal), 42);
            AddFull(MakeHint("Higher dark / lower light trims more. Off keeps the full capture."), 40);

            // Scale — not free-form resolution. Long edge only; aspect always kept.
            AddFull(MakeSection("SIZE"), 28);
            _trkUpscale = MakeTrack(640, 4096, AppSettings.DefaultImageUpscaleLongSide);
            _trkUpscale.TickFrequency = 128;
            _lblUpscaleVal = MakeValueLabel();
            // Wider value column for "1920 → ~1920×1080"
            AddRow(MakeLabel("Longer side"), WrapTrack(_trkUpscale, _lblUpscaleVal, valueWidth: 110), 42);
            AddFull(MakeHint(
                "Resize so the longer side is this many pixels. " +
                "Shape is kept (no stretch). Larger values are sharper but slower."),
                48);

            // Send size after prep
            AddFull(MakeSection("READING SIZE"), 28);
            _chkLlmSendDownscale = MakeCheck("Shrink before reading");
            _chkLlmSendDownscale.Checked = false;
            _chkLlmSendDownscale.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkLlmSendDownscale, 28);
            _trkLlmSendMaxEdge = MakeTrack(256, 2048, AppSettings.DefaultImageLlmSendMaxLongEdge);
            _trkLlmSendMaxEdge.TickFrequency = 64;
            _lblLlmSendMaxEdgeVal = MakeValueLabel();
            AddRow(
                MakeLabel("Max longer side"),
                WrapTrack(_trkLlmSendMaxEdge, _lblLlmSendMaxEdgeVal, valueWidth: 90),
                42);
            // 3 wrapped lines were clipping at 52 — leave room + gap before next section.
            AddFull(MakeHint(
                "Off (default): read at the size after cleanup. " +
                "On: shrink so the longer side is no larger than this (faster, less detail). " +
                "Finding balloons still uses the Size setting above."),
                64);

            // Gray
            AddFull(MakeSection("GRAYSCALE"), 28);
            _chkGray = MakeCheck("Convert to grayscale");
            _chkGray.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkGray, 28);
            _trkInkWeight = MakeTrack(0, 100, 55);
            _lblInkWeightVal = MakeValueLabel();
            AddRow(MakeLabel("Ink weight"), WrapTrack(_trkInkWeight, _lblInkWeightVal), 42);
            AddFull(MakeHint("Higher keeps bright colors (like yellow SFX) darker in gray."), 40);

            // Tone: denoise
            AddFull(MakeSection("SMOOTH"), 28);
            _trkDenoiseR = MakeTrack(0, 4, 1);
            _lblDenoiseRVal = MakeValueLabel();
            AddRow(MakeLabel("Strength"), WrapTrack(_trkDenoiseR, _lblDenoiseRVal), 42);
            _trkDenoiseSigma = MakeTrack(1, 80, 22);
            _lblDenoiseSigmaVal = MakeValueLabel();
            AddRow(MakeLabel("Detail keep"), WrapTrack(_trkDenoiseSigma, _lblDenoiseSigmaVal), 42);
            AddFull(MakeHint("0 = off. Softens noise while trying to keep edges."), 32);

            // Tone: levels
            AddFull(MakeSection("CONTRAST"), 28);
            _chkAutoLevels = MakeCheck("Auto contrast");
            _chkAutoLevels.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkAutoLevels, 28);
            _trkLevelsLow = MakeTrack(0, 200, 10); // 0.0–20.0 via /10
            _lblLevelsLowVal = MakeValueLabel();
            AddRow(MakeLabel("Shadows"), WrapTrack(_trkLevelsLow, _lblLevelsLowVal), 42);
            _trkLevelsHigh = MakeTrack(800, 1000, 990); // 80.0–100.0 via /10
            _lblLevelsHighVal = MakeValueLabel();
            AddRow(MakeLabel("Highlights"), WrapTrack(_trkLevelsHigh, _lblLevelsHighVal), 42);
            _trkLevelsMinRange = MakeTrack(8, 200, 48);
            _lblLevelsMinRangeVal = MakeValueLabel();
            AddRow(MakeLabel("Skip if already strong"), WrapTrack(_trkLevelsMinRange, _lblLevelsMinRangeVal), 42);
            AddFull(MakeHint("Gently boosts contrast. Skip if the page is already clean."), 36);

            // Tone: sharpen
            AddFull(MakeSection("SHARPEN"), 28);
            _trkSharpenAmt = MakeTrack(0, 200, 55); // 0.00–2.00
            _lblSharpenAmtVal = MakeValueLabel();
            AddRow(MakeLabel("Amount"), WrapTrack(_trkSharpenAmt, _lblSharpenAmtVal), 42);
            _trkSharpenPasses = MakeTrack(0, 4, 1);
            _lblSharpenPassesVal = MakeValueLabel();
            AddRow(MakeLabel("Passes"), WrapTrack(_trkSharpenPasses, _lblSharpenPassesVal), 42);
            AddFull(MakeHint("0 = off. Too much can fringe black lettering."), 32);

            scroll.Controls.Add(body);
            root.Controls.Add(scroll, 0, 0);

            // Preview panel — live + Preview show full pipeline end = OCR input (tone).
            var previewPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = UiTheme.Bg,
                Padding = new Padding(8, 0, 0, 0),
            };
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 70f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));

            previewPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "PREVIEW — updates as you change settings",
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, 0);

            var previewHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiTheme.BgDeep,
                Padding = new Padding(1),
            };
            previewHost.Paint += (_, e) =>
            {
                using var pen = new Pen(UiTheme.Border, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, previewHost.Width - 1, previewHost.Height - 1);
            };
            _preview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiTheme.BgDeep,
            };
            previewHost.Controls.Add(_preview);
            previewPanel.Controls.Add(previewHost, 0, 1);

            _lblPreviewStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Load a capture or panel image.",
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            previewPanel.Controls.Add(_lblPreviewStatus, 0, 2);

            _txtDetail = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Consolas", 8f),
                WordWrap = false,
            };
            previewPanel.Controls.Add(_txtDetail, 0, 3);
            root.Controls.Add(previewPanel, 1, 0);

            // Bottom bar: progress strip + status + buttons
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = UiTheme.Bg,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.SetColumnSpan(bottom, 2);

            _progress = new ThemeProgressBar();
            bottom.Controls.Add(_progress, 0, 0);
            bottom.SetColumnSpan(_progress, 2);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Picture cleanup.",
                ForeColor = UiTheme.Ok,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                BackColor = UiTheme.Bg,
            };

            _btnPreview = MakeButton("Preview");
            UiTheme.StylePrimaryButton(_btnPreview);
            _btnPreview.Click += (_, _) =>
            {
                RunPipelinePreview(fromLive: false);
            };

            _btnOpenImage = MakeButton("Open image…");
            _btnOpenImage.Click += (_, _) => OpenImageFile();

            _btnUseLast = MakeButton("Use last capture");
            _btnUseLast.Click += (_, _) => UseLastCapture();

            // Snap active overlay region (same geometry as Enter) — no OCR/TTS.
            _btnSnapRegion = MakeButton("Snap region");
            _btnSnapRegion.Click += async (_, _) => await SnapActiveRegionAsync();
            _btnSnapRegion.Enabled = _onCaptureActiveRegion != null;

            _btnReset = MakeButton("Reset defaults");
            _btnReset.Click += (_, _) => ResetDefaults();

            // RightToLeft: first added is rightmost → Open · Last · Snap · Reset · Preview
            buttons.Controls.Add(_btnPreview);
            buttons.Controls.Add(_btnReset);
            buttons.Controls.Add(_btnSnapRegion);
            buttons.Controls.Add(_btnUseLast);
            buttons.Controls.Add(_btnOpenImage);

            if (!_embedded)
            {
                _btnClose = MakeButton("Close");
                UiTheme.StylePrimaryButton(_btnClose);
                _btnClose.Click += (_, _) =>
                {
                    FlushToSettings();
                    Close();
                };
                buttons.Controls.Add(_btnClose);
            }

            bottom.Controls.Add(_lblStatus, 0, 1);
            bottom.Controls.Add(buttons, 1, 1);
            root.Controls.Add(bottom, 0, 1);
            Controls.Add(root);

            // Every knob change → persist + full-pipeline live preview.
            void WireTrack(TrackBar t)
            {
                t.ValueChanged += (_, _) =>
                {
                    RefreshValueLabels();
                    OnFieldChanged();
                };
            }

            WireTrack(_trkLetterboxPad);
            WireTrack(_trkLetterboxBlack);
            WireTrack(_trkLetterboxWhite);
            WireTrack(_trkUpscale);
            WireTrack(_trkLlmSendMaxEdge);
            WireTrack(_trkInkWeight);
            WireTrack(_trkDenoiseR);
            WireTrack(_trkDenoiseSigma);
            WireTrack(_trkLevelsLow);
            WireTrack(_trkLevelsHigh);
            WireTrack(_trkLevelsMinRange);
            WireTrack(_trkSharpenAmt);
            WireTrack(_trkSharpenPasses);

            Load += (_, _) =>
            {
                LoadFromSettings();
                ApplyKeyboardTabOrder();
            };
            Shown += (_, _) => ApplyKeyboardTabOrder();
            FormClosing += (_, _) =>
            {
                FlushToSettings();
                try { _liveTimer.Stop(); } catch { /* ignore */ }
                try { _liveTimer.Dispose(); } catch { /* ignore */ }
                try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
                try { _diskSaveTimer.Dispose(); } catch { /* ignore */ }
                DisposeSource();
                DisposePreviewImage();
            };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape && !_embedded)
                {
                    e.Handled = true;
                    FlushToSettings();
                    Close();
                }
                else if (e.KeyCode == Keys.F5)
                {
                    e.Handled = true;
                    RunPipelinePreview(fromLive: false);
                }
            };
        }

        public void ReloadFromSettings()
        {
            LoadFromSettings();
            ApplyKeyboardTabOrder();
        }

        public void FlushToSettings()
        {
            if (_loading) return;
            Persist(writeDiskNow: true);
        }

        public void ApplyKeyboardTabOrder()
        {
            int i = 0;
            void Next(Control c)
            {
                c.TabStop = c.Enabled && c is not Label;
                c.TabIndex = i++;
            }

            Next(_chkPrepEnabled);
            Next(_chkLetterbox);
            Next(_trkLetterboxPad);
            Next(_trkLetterboxBlack);
            Next(_trkLetterboxWhite);
            Next(_trkUpscale);
            Next(_chkLlmSendDownscale);
            Next(_trkLlmSendMaxEdge);
            Next(_chkGray);
            Next(_trkInkWeight);
            Next(_trkDenoiseR);
            Next(_trkDenoiseSigma);
            Next(_chkAutoLevels);
            Next(_trkLevelsLow);
            Next(_trkLevelsHigh);
            Next(_trkLevelsMinRange);
            Next(_trkSharpenAmt);
            Next(_trkSharpenPasses);
            Next(_btnOpenImage);
            Next(_btnUseLast);
            Next(_btnSnapRegion);
            Next(_btnReset);
            Next(_btnPreview);
            if (_btnClose != null)
                Next(_btnClose);
            Next(_txtDetail);

            _preview.TabStop = false;
            _lblStatus.TabStop = false;
            _lblPreviewStatus.TabStop = false;
        }

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                var s = AppSettings.Current;
                s.NormalizeImagePrepSettings();

                _chkPrepEnabled.Checked = s.ImagePrepEnabled;
                _chkLetterbox.Checked = s.ImageLetterbox;
                _trkLetterboxPad.Value = Clamp(_trkLetterboxPad, s.ImageLetterboxPad);
                _trkLetterboxBlack.Value = Clamp(_trkLetterboxBlack, s.ImageLetterboxBlack);
                _trkLetterboxWhite.Value = Clamp(_trkLetterboxWhite, s.ImageLetterboxWhite);
                _trkUpscale.Value = Clamp(_trkUpscale, s.ImageUpscaleLongSide);
                _chkLlmSendDownscale.Checked = s.ImageLlmSendDownscale;
                _trkLlmSendMaxEdge.Value = Clamp(_trkLlmSendMaxEdge, s.ImageLlmSendMaxLongEdge);
                _chkGray.Checked = s.ImageGrayscale;
                _trkInkWeight.Value = Clamp(_trkInkWeight, (int)Math.Round(s.ImageInkGrayWeight * 100));
                _trkDenoiseR.Value = Clamp(_trkDenoiseR, s.ImageDenoiseRadius);
                _trkDenoiseSigma.Value = Clamp(_trkDenoiseSigma, (int)Math.Round(s.ImageDenoiseSigma));
                _chkAutoLevels.Checked = s.ImageAutoLevels;
                _trkLevelsLow.Value = Clamp(_trkLevelsLow, (int)Math.Round(s.ImageAutoLevelsLow * 10));
                _trkLevelsHigh.Value = Clamp(_trkLevelsHigh, (int)Math.Round(s.ImageAutoLevelsHigh * 10));
                _trkLevelsMinRange.Value = Clamp(_trkLevelsMinRange, s.ImageAutoLevelsMinRange);
                _trkSharpenAmt.Value = Clamp(_trkSharpenAmt, (int)Math.Round(s.ImageSharpenAmount * 100));
                _trkSharpenPasses.Value = Clamp(_trkSharpenPasses, s.ImageSharpenPasses);

                RefreshValueLabels();
                ApplyDependentUi();

                // Cache → last capture → built-in sample so knobs can be used immediately.
                EnsurePreviewImageLoaded();

                _lblStatus.Text = HasSource
                    ? (DevCaptureCache.IsSample
                        ? "Sample page loaded — Open image… for your own capture."
                        : "Change a setting to update the preview.")
                    : "Open an image to get started.";
                _lblStatus.ForeColor = HasSource ? UiTheme.Ok : UiTheme.Warn;
            }
            finally
            {
                _loading = false;
            }

            ApplyDependentUi();
            ApplyKeyboardTabOrder();
            // Always re-preview on tab show — prep knobs may have changed on another tab.
            InvalidatePreviewForCurrentPrep(force: true);
        }

        private bool HasSource => _sourceImage != null;
        private int _appliedPrepGeneration = -1;
        private string _appliedPrepSig = "";

        /// <summary>
        /// Re-run full-pipeline preview when prep settings change or the tab is shown.
        /// </summary>
        private void InvalidatePreviewForCurrentPrep(bool force = false)
        {
            if (!HasSource || IsDisposed)
                return;

            int gen = DevCaptureCache.PrepGeneration;
            string sig = DevCaptureCache.PrepSettingsSignature();
            if (!force && gen == _appliedPrepGeneration && sig == _appliedPrepSig)
            {
                ScheduleLivePreview();
                return;
            }

            _appliedPrepGeneration = gen;
            _appliedPrepSig = sig;
            ScheduleLivePreview();
        }

        /// <summary>
        /// Always prefer last OCR capture when available; else cache/sample.
        /// Called on every tab reload so preview tracks the latest speak.
        /// </summary>
        private void EnsurePreviewImageLoaded()
        {
            try
            {
                var last = DevCaptureCache.TryLoadLastOcrCapture();
                if (last != null)
                {
                    DateTime stamp = OcrProcessor.LastResult?.CompletedLocal ?? default;
                    if (_sourceImage != null &&
                        string.Equals(_sourceLabel, "last capture", StringComparison.Ordinal) &&
                        stamp != default &&
                        stamp == _lastCaptureStamp &&
                        _sourceImage.Width == last.Width &&
                        _sourceImage.Height == last.Height)
                    {
                        try { last.Dispose(); } catch { /* ignore */ }
                        return;
                    }
                    SetSourceImage(last, "last capture");
                    _lastCaptureStamp = stamp;
                    return;
                }

                if (_sourceImage != null)
                {
                    SyncFromSharedCache();
                    return;
                }

                var bmp = DevCaptureCache.GetOrCreatePreviewSource(out string label);
                SetSourceImage(bmp, label, share: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImagePrep] EnsurePreviewImageLoaded: {ex.Message}");
            }
        }

        /// <summary>
        /// Write knobs into <see cref="AppSettings.Current"/> immediately so live preview
        /// and other tabs see them. Disk write is debounced unless
        /// <paramref name="writeDiskNow"/> (tab flush / close / explicit Preview).
        /// </summary>
        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            s.ImagePrepEnabled = _chkPrepEnabled.Checked;
            s.ImageLetterbox = _chkLetterbox.Checked;
            s.ImageLetterboxPad = _trkLetterboxPad.Value;
            s.ImageLetterboxBlack = _trkLetterboxBlack.Value;
            s.ImageLetterboxWhite = _trkLetterboxWhite.Value;
            s.ImageUpscaleLongSide = _trkUpscale.Value;
            s.ImageLlmSendDownscale = _chkLlmSendDownscale.Checked;
            s.ImageLlmSendMaxLongEdge = _trkLlmSendMaxEdge.Value;
            s.ImageGrayscale = _chkGray.Checked;
            s.ImageInkGrayWeight = _trkInkWeight.Value / 100f;
            s.ImageDenoiseRadius = _trkDenoiseR.Value;
            s.ImageDenoiseSigma = _trkDenoiseSigma.Value;
            s.ImageAutoLevels = _chkAutoLevels.Checked;
            s.ImageAutoLevelsLow = _trkLevelsLow.Value / 10.0;
            s.ImageAutoLevelsHigh = _trkLevelsHigh.Value / 10.0;
            s.ImageAutoLevelsMinRange = _trkLevelsMinRange.Value;
            s.ImageSharpenAmount = _trkSharpenAmt.Value / 100f;
            s.ImageSharpenPasses = _trkSharpenPasses.Value;
            s.NormalizeImagePrepSettings();
            // Balloons (and other tabs) must re-run prep-aware previews after these knobs change.
            DevCaptureCache.NotifyPrepSettingsChanged();

            if (writeDiskNow)
                FlushDiskSave(force: true);
            else
                ScheduleDiskSave();
        }

        private void ScheduleDiskSave()
        {
            _diskSavePending = true;
            try
            {
                _diskSaveTimer.Stop();
                _diskSaveTimer.Start();
            }
            catch { /* ignore */ }
        }

        private void FlushDiskSave(bool force = false)
        {
            try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
            if (!force && !_diskSavePending)
                return;
            _diskSavePending = false;
            try { AppSettings.Current.Save(); } catch { /* keep in-memory */ }
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            ApplyDependentUi();
            // Write knobs to AppSettings first so preview + Balloons see the same values.
            Persist(writeDiskNow: false);
            if (_sourceImage != null)
            {
                _lblStatus.Text = "Updating preview…";
                _lblStatus.ForeColor = UiTheme.Ok;
                ScheduleLivePreview();
            }
            else
            {
                _lblStatus.Text = "Load an image to enable live preview.";
                _lblStatus.ForeColor = UiTheme.Warn;
            }
        }

        private void ApplyDependentUi()
        {
            // No image → only load buttons stay usable (keeps the tab self-explanatory).
            bool ready = HasSource;
            bool prep = ready && _chkPrepEnabled.Checked;

            _chkPrepEnabled.Enabled = ready;

            _chkLetterbox.Enabled = prep;
            _trkLetterboxPad.Enabled = prep && _chkLetterbox.Checked;
            _trkLetterboxBlack.Enabled = prep && _chkLetterbox.Checked;
            _trkLetterboxWhite.Enabled = prep && _chkLetterbox.Checked;

            _trkUpscale.Enabled = prep;

            // Local-LLM send size is independent of prep master switch (applies to payload).
            _chkLlmSendDownscale.Enabled = ready;
            _trkLlmSendMaxEdge.Enabled = ready && _chkLlmSendDownscale.Checked;
            _lblLlmSendMaxEdgeVal.Enabled = _trkLlmSendMaxEdge.Enabled;

            _chkGray.Enabled = prep;
            _trkInkWeight.Enabled = prep && _chkGray.Checked;

            _trkDenoiseR.Enabled = prep;
            // Range σ only matters when denoise radius is on.
            _trkDenoiseSigma.Enabled = prep && _trkDenoiseR.Value > 0;

            _chkAutoLevels.Enabled = prep;
            _trkLevelsLow.Enabled = prep && _chkAutoLevels.Checked;
            _trkLevelsHigh.Enabled = prep && _chkAutoLevels.Checked;
            _trkLevelsMinRange.Enabled = prep && _chkAutoLevels.Checked;

            _trkSharpenAmt.Enabled = prep;
            // Passes only matter when amount is on.
            _trkSharpenPasses.Enabled = prep && _trkSharpenAmt.Value > 0;

            // Open / Use last / Snap always available so the user can replace the sample.
            _btnOpenImage.Enabled = !_snapBusy;
            _btnUseLast.Enabled = !_snapBusy;
            _btnSnapRegion.Enabled = !_snapBusy && _onCaptureActiveRegion != null;
            _btnPreview.Enabled = ready && !_snapBusy;
            _btnReset.Enabled = ready && !_snapBusy;

            // Recompute TabStop when enable flags flip (otherwise Tab skips knobs forever).
            if (!_loading)
                ApplyKeyboardTabOrder();
        }

        private void ScheduleLivePreview()
        {
            if (_loading || _sourceImage == null || IsDisposed)
                return;
            try
            {
                _liveTimer.Stop();
                _liveTimer.Start();
            }
            catch { /* ignore */ }
        }

        private void ResetDefaults()
        {
            AppSettings.Current.ResetImagePrepSettingsToDefaults();
            LoadFromSettings();
            Persist(writeDiskNow: true);
            _lblStatus.Text = "Restored defaults.";
            _lblStatus.ForeColor = UiTheme.Ok;
        }

        private void OpenImageFile()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Open capture / panel image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All files|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                using var tmp = new Bitmap(dlg.FileName);
                SetSourceImage(new Bitmap(tmp), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Could not open: {ex.Message}";
                _lblStatus.ForeColor = UiTheme.Bad;
            }
        }

        private void UseLastCapture()
        {
            try
            {
                // Prefer a real OCR capture, not the built-in sample.
                var bmp = DevCaptureCache.TryLoadLastOcrCapture();
                if (bmp == null)
                {
                    _lblStatus.Text = "No last capture yet — speak a region first, or Open image…";
                    _lblStatus.ForeColor = UiTheme.Warn;
                    return;
                }
                SetSourceImage(bmp, "last capture");
                _lblStatus.Text = "Using last capture.";
                _lblStatus.ForeColor = UiTheme.Ok;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Last capture failed: {ex.Message}";
                _lblStatus.ForeColor = UiTheme.Bad;
            }
        }

        /// <summary>
        /// Hide chrome → snap active F1–F8 region (same as Enter) → load raw → full prep preview.
        /// No Local-LLM / TTS.
        /// </summary>
        private async Task SnapActiveRegionAsync()
        {
            if (_snapBusy || _onCaptureActiveRegion == null)
                return;

            _snapBusy = true;
            ApplyDependentUi();
            _lblStatus.Text = "Snapping active region…";
            _lblStatus.ForeColor = UiTheme.Warn;
            _progress.BeginWork();
            try
            {
                var (bmp, error) = await _onCaptureActiveRegion().ConfigureAwait(true);
                if (IsDisposed)
                {
                    try { bmp?.Dispose(); } catch { /* ignore */ }
                    return;
                }

                if (bmp == null)
                {
                    _lblStatus.Text = string.IsNullOrWhiteSpace(error)
                        ? "Snap failed."
                        : error;
                    _lblStatus.ForeColor = UiTheme.Warn;
                    return;
                }

                // Raw source (same as Open / Use last); full pipe via Preview path.
                SetSourceImage(bmp, "region snap");
                RunPipelinePreview(fromLive: false);
                _lblStatus.Text = "Region loaded — preview updated.";
                _lblStatus.ForeColor = UiTheme.Ok;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Snap failed: {ex.Message}";
                _lblStatus.ForeColor = UiTheme.Bad;
            }
            finally
            {
                if (!IsDisposed)
                    _progress.EndWork();

                _snapBusy = false;
                if (!IsDisposed)
                    ApplyDependentUi();
            }
        }

        private void SyncFromSharedCache()
        {
            if (!DevCaptureCache.HasImage)
                return;
            var bmp = DevCaptureCache.CloneOrNull();
            if (bmp == null)
                return;
            if (_sourceImage != null &&
                string.Equals(_sourceLabel, DevCaptureCache.Label, StringComparison.Ordinal) &&
                _sourceImage.Width == bmp.Width &&
                _sourceImage.Height == bmp.Height)
            {
                try { bmp.Dispose(); } catch { /* ignore */ }
                return;
            }
            SetSourceImage(bmp, DevCaptureCache.Label, share: false);
        }

        private void SetSourceImage(Bitmap bmp, string label, bool share = true)
        {
            DisposeSource();
            _sourceImage = bmp;
            _sourceLabel = label;
            if (share)
            {
                try { DevCaptureCache.Set(bmp, label); } catch { /* ignore */ }
            }
            SetPreviewDisplay((Bitmap)bmp.Clone());
            _lblPreviewStatus.Text = $"Source: {_sourceLabel}  ·  {bmp.Width}×{bmp.Height}";
            _txtDetail.Text = string.Equals(label, DevCaptureCache.SampleLabel, StringComparison.Ordinal)
                ? "Sample panel — Open image… or Use last capture for a real page."
                : "Adjust any control — live preview matches OCR input (letterbox → scale → gray → tone; same for Default and ComicBook).";
            ApplyDependentUi();
            if (!_loading)
                ScheduleLivePreview();
        }

        private void SetPreviewDisplay(Bitmap bmp)
        {
            DisposePreviewImage();
            _previewImage = bmp;
            _preview.Image = _previewImage;
        }

        private void DisposeSource()
        {
            if (_sourceImage != null)
            {
                try { _sourceImage.Dispose(); } catch { /* ignore */ }
                _sourceImage = null;
            }
        }

        private void DisposePreviewImage()
        {
            _preview.Image = null;
            if (_previewImage != null)
            {
                try { _previewImage.Dispose(); } catch { /* ignore */ }
                _previewImage = null;
            }
        }

        /// <summary>
        /// Run image prep using live AppSettings. Always displays the full pipe
        /// end (tone) so knobs earlier in the chain still show gray/tone effects.
        /// </summary>
        private void RunPipelinePreview(bool fromLive)
        {
            if (_sourceImage == null)
            {
                if (!fromLive)
                    EnsurePreviewImageLoaded();
                if (_sourceImage == null)
                {
                    if (!fromLive)
                    {
                        _lblStatus.Text = "Open an image or use last capture first.";
                        _lblStatus.ForeColor = UiTheme.Warn;
                    }
                    return;
                }
            }

            // Critical: write UI knobs before any Task.Run so pad/black/white/etc. are live.
            // Explicit Preview / F5 flushes disk; live drag keeps memory-only until debounce.
            Persist(writeDiskNow: !fromLive);
            AppSettings.Current.NormalizeImagePrepSettings();
            _appliedPrepGeneration = DevCaptureCache.PrepGeneration;
            _appliedPrepSig = DevCaptureCache.PrepSettingsSignature();

            int gen = ++_liveGen;
            _ = RunPipelinePreviewCoreAsync(gen, fromLive, FullPipelineStage);
        }

        private async Task RunPipelinePreviewCoreAsync(int gen, bool fromLive, string stage)
        {
            Bitmap? work = null;
            _progress.BeginWork();
            try
            {
                if (_sourceImage == null || IsDisposed)
                    return;

                try { work = new Bitmap(_sourceImage); }
                catch (Exception ex)
                {
                    if (!IsDisposed && gen == _liveGen)
                    {
                        _lblStatus.Text = $"Preview source error: {ex.Message}";
                        _lblStatus.ForeColor = UiTheme.Bad;
                    }
                    return;
                }

                string stageCopy = stage;
                ImagePrepPreviewResult result = await Task.Run(
                    () => OcrProcessor.PreviewImagePrep(work, stageCopy)).ConfigureAwait(true);

                if (IsDisposed || gen != _liveGen) { result.Dispose(); return; }

                int w = result.Width;
                int h = result.Height;
                string detail = result.Detail ?? "";
                string stageName = result.StageName ?? stage;
                bool prepOn = AppSettings.Current.ImagePrepEnabled;
                bool grayOn = prepOn && AppSettings.Current.ImageGrayscale;
                if (result.Display != null)
                {
                    Bitmap d = result.Display;
                    result.Display = null;
                    SetPreviewDisplay(d);
                }
                result.Dispose();

                _txtDetail.Text = UiTheme.SanitizeUiEngineNames(detail);
                _lblPreviewStatus.Text =
                    $"{_sourceLabel}  ·  {stageName}  ·  " +
                    $"cleanup={(prepOn ? "on" : "off")} gray={(grayOn ? "on" : "off")}  ·  " +
                    $"{w}×{h}";
                _lblPreviewStatus.ForeColor = UiTheme.Ok;
                _lblStatus.Text = $"{stageName} preview.";
                _lblStatus.ForeColor = UiTheme.Ok;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && gen == _liveGen)
                {
                    _lblStatus.Text = $"Preview failed: {ex.Message}";
                    _lblStatus.ForeColor = UiTheme.Bad;
                    _txtDetail.Text = ex.ToString();
                }
            }
            finally
            {
                try { work?.Dispose(); } catch { /* ignore */ }
                if (!IsDisposed)
                    _progress.EndWork();
            }
        }

        private void RefreshValueLabels()
        {
            var inv = CultureInfo.InvariantCulture;
            _lblLetterboxPadVal.Text = $"{_trkLetterboxPad.Value}px";
            _lblLetterboxBlackVal.Text = _trkLetterboxBlack.Value.ToString(inv);
            _lblLetterboxWhiteVal.Text = _trkLetterboxWhite.Value.ToString(inv);
            // Target long edge after letterbox. W×H estimate uses source size (pre-letterbox);
            // trust the preview status line for exact post-prep dimensions.
            int longEdge = _trkUpscale.Value;
            if (_sourceImage != null && _sourceImage.Width > 0 && _sourceImage.Height > 0)
            {
                int srcLong = Math.Max(_sourceImage.Width, _sourceImage.Height);
                double scale = (double)longEdge / Math.Max(1, srcLong);
                int tw = Math.Max(1, (int)Math.Round(_sourceImage.Width * scale));
                int th = Math.Max(1, (int)Math.Round(_sourceImage.Height * scale));
                // "~" marks pre-letterbox estimate; live preview shows true W×H after trim.
                _lblUpscaleVal.Text = $"{longEdge} → ~{tw}×{th}";
            }
            else
            {
                _lblUpscaleVal.Text = $"{longEdge}px";
            }
            _lblLlmSendMaxEdgeVal.Text = $"{_trkLlmSendMaxEdge.Value}px";
            _lblInkWeightVal.Text = (_trkInkWeight.Value / 100.0).ToString("0.00", inv);
            _lblDenoiseRVal.Text = _trkDenoiseR.Value == 0 ? "off" : _trkDenoiseR.Value.ToString(inv);
            _lblDenoiseSigmaVal.Text = _trkDenoiseSigma.Value.ToString(inv);
            _lblLevelsLowVal.Text = (_trkLevelsLow.Value / 10.0).ToString("0.0", inv);
            _lblLevelsHighVal.Text = (_trkLevelsHigh.Value / 10.0).ToString("0.0", inv);
            _lblLevelsMinRangeVal.Text = _trkLevelsMinRange.Value.ToString(inv);
            _lblSharpenAmtVal.Text = _trkSharpenAmt.Value == 0
                ? "off"
                : (_trkSharpenAmt.Value / 100.0).ToString("0.00", inv);
            _lblSharpenPassesVal.Text = _trkSharpenPasses.Value == 0
                ? "off"
                : _trkSharpenPasses.Value.ToString(inv);
        }

        private static int Clamp(TrackBar t, int v) =>
            Math.Clamp(v, t.Minimum, t.Maximum);

        private static Label MakeSection(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgHeader,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            // Bottom-align in the taller section row so prior hints keep a clear gap.
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 2),
        };

        private static Label MakeLabel(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        private static Label MakeHint(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgDim,
            Font = new Font("Segoe UI", 8f),
            TextAlign = ContentAlignment.TopLeft,
        };

        private static Label MakeValueLabel() => new()
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Fg,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 8.5f),
            AutoEllipsis = true,
        };

        private static CheckBox MakeCheck(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Fg,
            BackColor = UiTheme.Bg,
            AutoSize = false,
        };

        private static TrackBar MakeTrack(int min, int max, int value)
        {
            return new TrackBar
            {
                Dock = DockStyle.Fill,
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                TickStyle = TickStyle.None,
                AutoSize = false,
                Height = 28,
                BackColor = UiTheme.Bg,
            };
        }

        private static Control WrapTrack(TrackBar track, Label value, int valueWidth = 52)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                BackColor = UiTheme.Bg,
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, valueWidth));
            p.Controls.Add(track, 0, 0);
            p.Controls.Add(value, 1, 0);
            return p;
        }

        private static Button MakeButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(88, 30),
                Margin = new Padding(6, 2, 0, 2),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }
    }
}
