using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Settings tab: Comic Book balloon / OCR region detect tuning + live preview.
    /// Writes to AppSettings / SpeakRect.ini [COMIC_REGIONS] (and named profiles).
    /// </summary>
    public sealed class frm_ComicRegions : Form
    {
        private readonly CheckBox _chkPoiMarkers;
        private readonly CheckBox _chkPoiFogOutside;
        private readonly CheckBox _chkPoiAutoStack;
        private readonly TrackBar _trkPoiAutoStackGap;
        private readonly Label _lblPoiAutoStackGapVal;
        private readonly TrackBar _trkPoiAutoStackMargin;
        private readonly Label _lblPoiAutoStackMarginVal;
        private readonly TrackBar _trkPoiStackBeef;
        private readonly Label _lblPoiStackBeefVal;
        private readonly TrackBar _trkPoiStackBottomPad;
        private readonly Label _lblPoiStackBottomPadVal;

        private readonly CheckBox _chkFog;
        private readonly CheckBox _chkDynamicFog;
        private readonly TrackBar _trkFogAmount;
        private readonly Label _lblFogAmount;
        private readonly Label _lblFogAmountVal;

        private readonly TrackBar _trkClusterX;
        private readonly Label _lblClusterXVal;
        private readonly TrackBar _trkClusterY;
        private readonly Label _lblClusterYVal;

        private readonly TrackBar _trkInflateX;
        private readonly Label _lblInflateXVal;
        private readonly TrackBar _trkInflateY;
        private readonly Label _lblInflateYVal;
        private readonly TrackBar _trkPadding;
        private readonly Label _lblPaddingVal;

        private readonly TrackBar _trkDenseCount;
        private readonly Label _lblDenseCountVal;

        private readonly CheckBox _chkSplitLarge;
        private readonly CheckBox _chkMergeOverlap;

        private readonly TrackBar _trkOrphan;
        private readonly Label _lblOrphanVal;

        private readonly TrackBar _trkMinAlnum;
        private readonly Label _lblMinAlnumVal;

        private readonly CheckBox _chkSequential;

        private readonly RegionRefineSurface _refine;
        private readonly Label _lblPreviewHeader;
        private readonly Label _lblPreviewStatus;
        private readonly TextBox _txtDetail;
        private readonly Button _btnOpenImage;
        private readonly Button _btnUseLast;
        private readonly Button _btnSnapRegion;
        private readonly Button _btnPreview;
        private readonly Button _btnSpeak;
        private readonly Button _btnStop;
        private readonly Button _btnReset;
        private readonly Button _btnRedetect;
        private readonly Button _btnRegionUp;
        private readonly Button _btnRegionDown;
        private readonly Button _btnRegionDelete;
        private readonly Label _lblStatus;
        private readonly ThemeProgressBar _progress;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly Action? _onModeChanged;
        private readonly Func<Task<(Bitmap? Bitmap, string Error)>>? _onCaptureActiveRegion;
        private readonly bool _embedded;
        private bool _snapBusy;

        private bool _loading;
        /// <summary>True during Speak (locks UI). Live preview does not set this.</summary>
        private bool _speakBusy;
        /// <summary>In-flight OCR detect previews (live or manual). Used for Stop enable.</summary>
        private int _previewInFlight;
        private Bitmap? _sourceImage;
        private CancellationTokenSource? _workCts;
        private string _sourceLabel = "(no image)";
        /// <summary>When preview is "last capture", stamp of <see cref="OcrProcessor.LastResult"/> we loaded.</summary>
        private DateTime _lastCaptureStamp;
        private readonly System.Windows.Forms.Timer _liveTimer;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private int _liveGeneration;
        private bool _diskSavePending;
        /// <summary>
        /// When true, next live-timer tick only rebuilds detect-view base (fog)
        /// without re-running OCR detect (locked refine / fog-only updates).
        /// </summary>
        private bool _detectViewOnly;

        /// <summary>
        /// Detect-knob signature baked into the current refine seed (grow, pad, cluster, …).
        /// When this drifts and boxes are not user-edited, Speak re-detects so solid
        /// green boxes match the knobs.
        /// </summary>
        private string _seededDetectSig = "";

        // Track scales (integer) ↔ real values
        // Fog amount: 0..100 → 0.00..1.00
        // Cluster: 25..300 → 0.25..3.00
        // Inflate: 0..80 → 0.00..0.80
        // Pad: 0..64 px
        // Dense: 0..20
        // Orphan: 0..16
        // Min alnum: 0..40 (0 = off)

        public frm_ComicRegions(
            bool embedded = false,
            Action? onRequestClose = null,
            Func<Task<(Bitmap? Bitmap, string Error)>>? onCaptureActiveRegion = null,
            Action? onModeChanged = null)
        {
            _embedded = embedded;
            _onRequestClose = onRequestClose;
            _onCaptureActiveRegion = onCaptureActiveRegion;
            _onModeChanged = onModeChanged;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Balloons";
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
                ClientSize = new Size(900, 700);
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

            // Debounced live region preview when an image is loaded.
            _liveTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _liveTimer.Tick += async (_, _) =>
            {
                _liveTimer.Stop();
                if (_speakBusy || _sourceImage == null || IsDisposed)
                    return;
                if (_detectViewOnly)
                {
                    _detectViewOnly = false;
                    await RefreshDetectViewBaseAsync().ConfigureAwait(true);
                    return;
                }
                await RunPreviewAsync(fromLive: true);
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

            // ---- Left: scrollable controls ----
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

            AddFull(MakeSection("BALLOON DETECT"), 22);
            AddFull(MakeHint(
                "Tune how OCR finds speech balloons. Comic Book uses boxes for crops; " +
                "POI is a Comic Book alternate: green region boxes on gray prep " +
                "(same image as live/analytics). Preview is that pipe — not a separate paint path."),
                48);

            // 0) Comic Book POI alternate path
            AddFull(MakeSection("0 · POI GUIDE (COMIC BOOK)"), 20);
            _chkPoiMarkers = new CheckBox
            {
                Text = "POI region boxes (Comic Book)",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = false,
            };
            _chkPoiMarkers.CheckedChanged += (_, _) =>
            {
                if (_loading)
                    return;
                OnFieldChanged();
                if (_chkPoiMarkers.Checked)
                    ForceComicBookModeForPoi();
                ApplyPoiPreviewUi();
                ApplyControlsEnabled();
            };
            AddFull(_chkPoiMarkers, 28);
            AddFull(MakeHint(
                "On: switches to Comic Book mode (sidebar MODE updates). OCR finds text islands, " +
                "draws bright green boxes on the gray prep (same as this preview), optional fog " +
                "outside them. Speak: with Stack on (stock) → each island on its own orange " +
                "canvas to Local-LLM one at a time; Stack off/fail multi → §9 Sequential or " +
                "crop-stack; 1 island → full-page guide. " +
                "Edit the POI prompt under Speech → Prompts. Grow / crop pad size the islands."),
                72);

            _chkPoiFogOutside = new CheckBox
            {
                Text = "    Fog outside islands (thick)",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkPoiFogOutside.CheckedChanged += (_, _) =>
            {
                if (_loading)
                    return;
                OnFieldChanged();
                // Live toggle: fog veil on/off in the preview immediately (no re-detect).
                ApplyPoiPreviewUi();
            };
            AddFull(_chkPoiFogOutside, 28);
            AddFull(MakeHint(
                "Sub-option of POI: heavy gray fog over everything outside the green boxes " +
                "(clear holes for speech islands). Hides art/UI so OCR and speech stay on " +
                "dialogue. Preview matches what the Local-LLM sees."),
                48);

            _chkPoiAutoStack = new CheckBox
            {
                Text = "    Island canvases for Local-LLM (one at a time)",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkPoiAutoStack.CheckedChanged += (_, _) =>
            {
                if (_loading)
                    return;
                OnFieldChanged();
                ApplyPoiPreviewUi();
                ApplyControlsEnabled();
            };
            AddFull(_chkPoiAutoStack, 28);
            _trkPoiAutoStackGap = MakeTrack(0, 64, ComicPoiGuide.DefaultAutoStackGapPx);
            _lblPoiAutoStackGapVal = MakeValueLabel();
            _trkPoiAutoStackGap.ValueChanged += (_, _) =>
            {
                if (_loading)
                    return;
                RefreshValueLabels();
                OnFieldChanged();
            };
            AddRow(
                MakeLabel("    Between islands"),
                WrapTrack(_trkPoiAutoStackGap, _lblPoiAutoStackGapVal),
                42);
            _trkPoiAutoStackMargin = MakeTrack(0, 64, ComicPoiGuide.LlmSendStackMarginPx);
            _lblPoiAutoStackMarginVal = MakeValueLabel();
            _trkPoiAutoStackMargin.ValueChanged += (_, _) =>
            {
                if (_loading)
                    return;
                RefreshValueLabels();
                OnFieldChanged();
            };
            AddRow(
                MakeLabel("    Outer margin"),
                WrapTrack(_trkPoiAutoStackMargin, _lblPoiAutoStackMarginVal),
                42);
            // A/B: canvas beef (+% size) and vertical pad bias (bottom-heavy vs center).
            int defaultBeefPct = (int)Math.Round(
                ComicPoiGuide.DefaultStackBeefExtra * 100.0);
            _trkPoiStackBeef = MakeTrack(0, 100, defaultBeefPct);
            _lblPoiStackBeefVal = MakeValueLabel();
            _trkPoiStackBeef.ValueChanged += (_, _) =>
            {
                if (_loading)
                    return;
                RefreshValueLabels();
                OnFieldChanged();
            };
            AddRow(
                MakeLabel("    Canvas beef (+%)"),
                WrapTrack(_trkPoiStackBeef, _lblPoiStackBeefVal),
                42);
            int defaultBotPct = (int)Math.Round(
                ComicPoiGuide.DefaultStackBottomPadShare * 100.0);
            _trkPoiStackBottomPad = MakeTrack(0, 100, defaultBotPct);
            _lblPoiStackBottomPadVal = MakeValueLabel();
            _trkPoiStackBottomPad.ValueChanged += (_, _) =>
            {
                if (_loading)
                    return;
                RefreshValueLabels();
                OnFieldChanged();
            };
            AddRow(
                MakeLabel("    Bottom pad share"),
                WrapTrack(_trkPoiStackBottomPad, _lblPoiStackBottomPadVal),
                42);
            AddFull(MakeHint(
                "On (default): Speak crops each green island onto its own orange canvas " +
                "(beef knobs + margin), sends to Local-LLM one island at a time. " +
                "Preview stays full page for edit. Same canvas rules apply to non-POI " +
                "crop-stack (multi-strip) and to POI multi when this is off and §9 is off. " +
                "Stock beef +67%, bottom pad 50%. Margin=12px. " +
                "Compose hard-caps canvas long edge at 2560; Image-tab downscale is separate (off)."),
                72);

            // 1) Detect fog
            AddFull(MakeSection("1 · DETECT FOG"), 20);
            _chkFog = new CheckBox
            {
                Text = "Gray fog for OCR detect",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkFog.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyFogUiState();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkFog, 28);

            _chkDynamicFog = new CheckBox
            {
                Text = "Dynamic fog (auto — start low, climb to best)",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkDynamicFog.CheckedChanged += (_, _) =>
            {
                OnFieldChanged();
                ApplyFogUiState();
                ApplyKeyboardTabOrder();
            };
            AddFull(_chkDynamicFog, 28);
            AddFull(MakeHint(
                "On (default): ignore Fog strength — start low (~0.10) and raise fog until " +
                "island area shrinks; keep the peak. Merge-overlap is off during the search, " +
                "then restored. Same pipe for live + preview."),
                56);

            _trkFogAmount = MakeTrack(0, 100, 35);
            _lblFogAmount = MakeLabel("Fog strength");
            _lblFogAmountVal = MakeValueLabel();
            AddRow(_lblFogAmount, WrapTrack(_trkFogAmount, _lblFogAmountVal), 42);
            AddFull(MakeHint(
                "Fixed fog when Dynamic fog is off. Softens art so ink plates stand out. " +
                "Preview shows the fog used; speech OCR still reads the clear tone image."),
                40);

            // 2) Line merge
            AddFull(MakeSection("2 · LINE MERGE"), 20);
            _trkClusterX = MakeTrack(25, 300, 105);
            _lblClusterXVal = MakeValueLabel();
            AddRow(MakeLabel("Sideways"), WrapTrack(_trkClusterX, _lblClusterXVal), 42);
            _trkClusterY = MakeTrack(25, 300, 115);
            _lblClusterYVal = MakeValueLabel();
            AddRow(MakeLabel("Stacked"), WrapTrack(_trkClusterY, _lblClusterYVal), 42);
            AddFull(MakeHint("Lower = keep balloons separate. Higher = glue multi-line text into one island."), 36);

            // 3) Box pad
            AddFull(MakeSection("3 · BOX PADDING"), 20);
            _trkInflateX = MakeTrack(0, 80, 22);
            _lblInflateXVal = MakeValueLabel();
            AddRow(MakeLabel("Grow X"), WrapTrack(_trkInflateX, _lblInflateXVal), 42);
            _trkInflateY = MakeTrack(0, 80, 28);
            _lblInflateYVal = MakeValueLabel();
            AddRow(MakeLabel("Grow Y"), WrapTrack(_trkInflateY, _lblInflateYVal), 42);
            _trkPadding = MakeTrack(0, 64, 16);
            _lblPaddingVal = MakeValueLabel();
            AddRow(MakeLabel("Crop pad"), WrapTrack(_trkPadding, _lblPaddingVal), 42);
            // Extra row height leaves a clear gap before section 4.
            AddFull(MakeHint(
                "Grow X/Y and Crop pad resize the solid green boxes (what Speak crops). " +
                "Pad stops at neighbor islands. Live preview updates the boxes."),
                48);

            // 4) Merge overlapping (default on)
            AddFull(MakeSection("4 · MERGE OVERLAPPING ISLANDS"), 20);
            _chkMergeOverlap = new CheckBox
            {
                Text = "Merge overlapping islands",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkMergeOverlap.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkMergeOverlap, 28);
            // Tall enough for 3-line wrap + gap before the next section header.
            AddFull(MakeHint(
                "On (default): after Grow X/Y and Crop pad, any islands whose boxes " +
                "would overlap become one union rectangle large enough to cover all " +
                "text — good for busy pages where OCR splits close balloons. " +
                "Off: nudge grow-overlaps apart instead."),
                68);

            // 5) Dense-page pad
            AddFull(MakeSection("5 · DENSE PAGE PAD"), 20);
            _trkDenseCount = MakeTrack(0, 20, 4);
            _lblDenseCountVal = MakeValueLabel();
            AddRow(MakeLabel("When ≥ N"), WrapTrack(_trkDenseCount, _lblDenseCountVal), 42);
            AddFull(MakeHint("Use milder pad when this many islands found. 0 = always use full pad."), 36);

            // 6) Split large
            AddFull(MakeSection("6 · SPLIT LARGE REGIONS"), 20);
            _chkSplitLarge = new CheckBox
            {
                Text = "Re-detect inside mega caption / row islands",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkSplitLarge.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkSplitLarge, 28);
            AddFull(MakeHint("On when one huge box swallows several balloons."), 32);

            // 7) Orphan recover
            AddFull(MakeSection("7 · RECOVER MISSED BALLOONS"), 20);
            _trkOrphan = MakeTrack(0, 16, 6);
            _lblOrphanVal = MakeValueLabel();
            AddRow(MakeLabel("Orphan passes"), WrapTrack(_trkOrphan, _lblOrphanVal), 42);
            AddFull(MakeHint(
                "Bright empty plates re-OCR’d after a miss on Speak / live Comic Book. " +
                "0 = off. Detect-only preview does not run orphan fill."),
                44);

            // 8) Drop weak (0 = off)
            AddFull(MakeSection("8 · DROP WEAK ISLANDS"), 20);
            _trkMinAlnum = MakeTrack(0, 40, 4);
            _lblMinAlnumVal = MakeValueLabel();
            AddRow(MakeLabel("Min letters"), WrapTrack(_trkMinAlnum, _lblMinAlnumVal), 42);
            AddFull(MakeHint("Drop islands below this letter count. 0 = off (keep all non-junk islands)."), 36);

            // 9) Speak path — sequential isolates balloons from global dedupe
            AddFull(MakeSection("9 · SPEAK PATH"), 20);
            _chkSequential = new CheckBox
            {
                Text = "Sequential regions (OCR + speak each balloon.)",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = false,
            };
            _chkSequential.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkSequential, 28);
            AddFull(MakeHint(
                "Off (default): crop-stack (one stacked OCR + global speak plan) when not using " +
                "POI AutoStack. On: OCR+speak each balloon alone (isolates short replies like " +
                "\"Really?\" from global speak-dedupe). Under POI: used when AutoStack is off " +
                "or the stack VL fails on multi-island pages."),
                56);

            scroll.Controls.Add(body);
            root.Controls.Add(scroll, 0, 0);

            // ---- Right: preview ----
            var previewPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = UiTheme.Bg,
                Padding = new Padding(8, 0, 0, 0),
            };
            previewPanel.RowCount = 5;
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 70f));
            // Tall enough for AutoSize refine buttons (were clipped at 34px).
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));

            _lblPreviewHeader = new Label
            {
                Dock = DockStyle.Fill,
                Text = "PREVIEW — detect view (fog when on)  ·  drag / resize / add  ·  Del = remove  ·  Ctrl+↑↓ = reorder",
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            previewPanel.Controls.Add(_lblPreviewHeader, 0, 0);

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
            _refine = new RegionRefineSurface
            {
                Dock = DockStyle.Fill,
            };
            _refine.RegionsChanged += (_, _) =>
            {
                PersistRefineSession();
                // Only real geometry edits arm one-shot overlay-hide speak.
                if (_refine.HasUserOverride)
                    ComicRegionOverrideSession.ArmOverlaySpeak();
                UpdateRefineStatus();
            };
            _refine.SelectionChanged += (_, _) => UpdateRefineButtons();
            previewHost.Controls.Add(_refine);
            previewPanel.Controls.Add(previewHost, 0, 1);

            var refineBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 4, 0, 0),
            };
            _btnRedetect = MakeRefineButton("Re-detect");
            _btnRedetect.Click += async (_, _) =>
            {
                // Explicit re-detect: drop locked overrides and reseed from OCR.
                ComicRegionOverrideSession.Clear();
                _refine.MarkClean();
                await RunPreviewAsync(fromLive: false, forceRedetect: true);
            };
            _btnRegionUp = MakeRefineButton("Order ↑");
            _btnRegionUp.Click += (_, _) => _refine.MoveSelected(-1);
            _btnRegionDown = MakeRefineButton("Order ↓");
            _btnRegionDown.Click += (_, _) => _refine.MoveSelected(1);
            _btnRegionDelete = MakeRefineButton("Delete");
            _btnRegionDelete.Click += (_, _) => _refine.DeleteSelected();
            refineBar.Controls.Add(_btnRedetect);
            refineBar.Controls.Add(_btnRegionUp);
            refineBar.Controls.Add(_btnRegionDown);
            refineBar.Controls.Add(_btnRegionDelete);
            previewPanel.Controls.Add(refineBar, 0, 2);

            _lblPreviewStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Load a panel image, then Preview to seed OCR boxes.",
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            previewPanel.Controls.Add(_lblPreviewStatus, 0, 3);

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
            previewPanel.Controls.Add(_txtDetail, 0, 4);

            root.Controls.Add(previewPanel, 1, 0);

            // ---- Bottom bar: progress strip + status + buttons ----
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
            bottom.SetColumnSpan(bottom, 2);
            root.SetColumnSpan(bottom, 2);

            _progress = new ThemeProgressBar();
            bottom.Controls.Add(_progress, 0, 0);
            bottom.SetColumnSpan(_progress, 2);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Comic Book balloon detect.",
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

            // FlowLayout RightToLeft: add in reverse visual order (left→right for user).
            // Desired left→right: Open · Last · Snap · Reset · Stop · Speak · Preview
            _btnPreview = MakeButton("Preview");
            UiTheme.StylePrimaryButton(_btnPreview);
            _btnPreview.Click += async (_, _) => await RunPreviewAsync();

            _btnSpeak = MakeButton("Speak");
            UiTheme.StylePrimaryButton(_btnSpeak);
            _btnSpeak.Click += async (_, _) => await RunSpeakAsync();

            _btnStop = MakeButton("Stop");
            _btnStop.Enabled = false;
            _btnStop.Click += (_, _) => CancelWork();

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

            // RightToLeft: first added is rightmost
            buttons.Controls.Add(_btnPreview);
            buttons.Controls.Add(_btnSpeak);
            buttons.Controls.Add(_btnStop);
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
                // Rightmost when not embedded
                buttons.Controls.Add(_btnClose);
            }

            bottom.Controls.Add(_lblStatus, 0, 1);
            bottom.Controls.Add(buttons, 1, 1);
            root.Controls.Add(bottom, 0, 1);

            Controls.Add(root);

            // Track change handlers
            foreach (var t in new[]
            {
                _trkFogAmount, _trkClusterX, _trkClusterY,
                _trkInflateX, _trkInflateY, _trkPadding,
                _trkDenseCount, _trkOrphan, _trkMinAlnum,
            })
            {
                t.ValueChanged += (_, _) =>
                {
                    RefreshValueLabels();
                    OnFieldChanged();
                };
            }

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
                CancelWork(disposeCts: true);
                DisposeSource();
                try { _refine.Clear(); } catch { /* ignore */ }
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
                    _ = RunPreviewAsync();
                }
                else if (e.KeyCode == Keys.F6)
                {
                    e.Handled = true;
                    _ = RunSpeakAsync();
                }
            };
        }

        public void ReloadFromSettings()
        {
            LoadFromSettings();
            ApplyKeyboardTabOrder();
        }

        /// <summary>
        /// Explicit Tab order for this tab (overrides screen-position auto sort).
        /// Left controls top→bottom, then action buttons left→right, then detail log.
        /// </summary>
        public void ApplyKeyboardTabOrder()
        {
            int i = 0;
            void Next(Control c, bool tabStop = true)
            {
                c.TabStop = tabStop && c.Enabled;
                c.TabIndex = i++;
            }

            // 0) POI guide (Comic Book)
            Next(_chkPoiMarkers);
            Next(_chkPoiFogOutside);
            Next(_chkPoiAutoStack);
            Next(_trkPoiAutoStackGap);
            Next(_trkPoiAutoStackMargin);
            Next(_trkPoiStackBeef);
            Next(_trkPoiStackBottomPad);
            // 1) Detect fog
            Next(_chkFog);
            Next(_chkDynamicFog);
            Next(_trkFogAmount);
            // 2) Line merge
            Next(_trkClusterX);
            Next(_trkClusterY);
            // 3) Box pad
            Next(_trkInflateX);
            Next(_trkInflateY);
            Next(_trkPadding);
            // 4) Merge overlap
            Next(_chkMergeOverlap);
            // 5) Dense
            Next(_trkDenseCount);
            // 6) Split
            Next(_chkSplitLarge);
            // 7) Orphan
            Next(_trkOrphan);
            // 8) Min letters
            Next(_trkMinAlnum);
            // 9) Speak path
            Next(_chkSequential);
            // Actions (left → right for user)
            Next(_btnOpenImage);
            Next(_btnUseLast);
            Next(_btnSnapRegion);
            Next(_btnReset);
            Next(_btnStop);
            Next(_btnSpeak);
            Next(_btnPreview);
            Next(_btnRedetect);
            Next(_btnRegionUp);
            Next(_btnRegionDown);
            Next(_btnRegionDelete);
            Next(_refine);
            if (_btnClose != null)
                Next(_btnClose);
            // Detail log last
            Next(_txtDetail);

            // Non-interactive chrome
            _lblStatus.TabStop = false;
            _lblPreviewStatus.TabStop = false;
            _lblFogAmountVal.TabStop = false;
            _lblClusterXVal.TabStop = false;
            _lblClusterYVal.TabStop = false;
            _lblInflateXVal.TabStop = false;
            _lblInflateYVal.TabStop = false;
            _lblPaddingVal.TabStop = false;
            _lblDenseCountVal.TabStop = false;
            _lblOrphanVal.TabStop = false;
            _lblMinAlnumVal.TabStop = false;
        }

        /// <summary>Push control values into <see cref="AppSettings"/> before profile save.</summary>
        public void FlushToSettings()
        {
            if (_loading) return;
            Persist(writeDiskNow: true);
            PersistRefineSession();
        }

        /// <summary>
        /// Push refined regions into the session so overlay-hide speak can use them
        /// even after this form is disposed.
        /// </summary>
        public void FlushRefineSessionForOverlay()
        {
            PersistRefineSession();
        }

        /// <summary>
        /// One-shot: if the user <b>edited</b> refine boxes since last overlay speak,
        /// start Comic Book speak with that override. Looking at Balloons without
        /// edits never arms this. Called on overlay hide. Fire-and-forget.
        /// </summary>
        public static void TrySpeakOverrideOnOverlayHide()
        {
            if (!ComicRegionOverrideSession.TryConsumeOverlaySpeak(out var regions))
                return;
            if (regions.Count == 0)
                return;

            Bitmap? work = null;
            try
            {
                work = DevCaptureCache.CloneOrNull()
                    ?? DevCaptureCache.TryLoadLastOcrCapture();
            }
            catch { work = null; }

            if (work == null)
            {
                Debug.WriteLine("[Balloons] overlay hide: pending speak but no image");
                return;
            }

            var overrideCopy = regions.ToList();
            Debug.WriteLine(
                $"[Balloons] Overlay hide → one-shot speak with {overrideCopy.Count} refined region(s)");
            _ = Task.Run(async () =>
            {
                try
                {
                    using (work)
                    using (await OcrProcessor.SpeakComicFromBitmapAsync(
                               work, CancellationToken.None, overrideCopy)
                           .ConfigureAwait(false))
                    {
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Balloons] override speak on overlay hide: {ex.Message}");
                }
            });
        }

        private string CurrentCaptureId()
        {
            if (_sourceImage == null)
                return "";
            return ComicRegionOverrideSession.MakeCaptureId(
                _sourceLabel,
                _sourceImage.Width,
                _sourceImage.Height);
        }

        /// <summary>
        /// Regions Speak should crop (pipeline coords). Prefer the boxes currently
        /// on the preview surface so Speak matches the solid/dashed outlines.
        /// Falls back to a session override for this capture only.
        /// </summary>
        private List<Rectangle>? GetActiveOverrideRegions()
        {
            PersistRefineSession();
            // On-screen boxes are display-final (pad already baked at Preview seed,
            // or user-edited). Speak override uses ForcedCropPadPx=0 — do not pad again.
            if (_refine.RegionCount > 0)
                return _refine.Regions.ToList();
            // Only the override bound to this capture — never a stale other-page session.
            if (ComicRegionOverrideSession.TryGet(
                    CurrentCaptureId(), out var fromSession, out _, out _, out _))
                return fromSession;
            return null;
        }

        /// <summary>
        /// Settings that change solid green box size (grow, crop pad, cluster, …).
        /// </summary>
        private static string BuildDetectSettingsSignature()
        {
            var s = AppSettings.Current;
            s.NormalizeComicRegionSettings();
            s.NormalizeImagePrepSettings();
            var inv = CultureInfo.InvariantCulture;
            return string.Join('|',
                s.ComicDetectFog ? "1" : "0",
                s.ComicDynamicFog ? "1" : "0",
                s.ComicDetectFogAmount.ToString("0.###", inv),
                s.ComicClusterGapX.ToString("0.###", inv),
                s.ComicClusterGapY.ToString("0.###", inv),
                s.ComicInflateFracX.ToString("0.###", inv),
                s.ComicInflateFracY.ToString("0.###", inv),
                s.ComicRegionPadding.ToString(inv),
                s.ComicDenseIslandCount.ToString(inv),
                s.ComicMergeOverlappingIslands ? "1" : "0",
                s.ComicSplitLargeRegions ? "1" : "0",
                s.ComicOrphanRecoverPasses.ToString(inv),
                s.ComicMinIslandAlnum.ToString(inv),
                DevCaptureCache.PrepSettingsSignature());
        }

        private void MarkSeedMatchesLiveDetectSettings()
        {
            _seededDetectSig = BuildDetectSettingsSignature();
        }

        /// <summary>
        /// True when grow / cluster / merge / prep etc. changed since the last seed
        /// and the user has not locked geometry by editing boxes.
        /// </summary>
        private bool DetectKnobsChangedSinceSeed()
        {
            if (string.IsNullOrEmpty(_seededDetectSig))
                return true;
            return !string.Equals(
                _seededDetectSig, BuildDetectSettingsSignature(), StringComparison.Ordinal);
        }

        /// <summary>
        /// True only when the user has actually edited boxes (or a matching session
        /// override is still active). Bare <see cref="RegionRefineSurface.IsDirty"/>
        /// alone is not enough — e.g. delete-all is dirty with zero regions and must
        /// allow live knob previews again.
        /// </summary>
        private bool HasLockedRefine =>
            _refine.HasUserOverride || ComicRegionOverrideSession.IsActive;

        private void PersistRefineSession()
        {
            if (_sourceImage == null)
                return;
            // Only lock when the user has edited geometry (or already locked).
            if (!_refine.HasUserOverride && !ComicRegionOverrideSession.IsActive)
            {
                // Dirty-with-empty (deleted all) must drop any prior lock.
                if (_refine.IsDirty && _refine.RegionCount == 0)
                    ComicRegionOverrideSession.Clear();
                return;
            }
            if (_refine.RegionCount == 0)
            {
                ComicRegionOverrideSession.Clear();
                return;
            }

            // Keep dirty flag true once user has overridden.
            _refine.MarkDirty();
            string id = CurrentCaptureId();
            using var baseClone = _refine.CloneBaseImage();
            ComicRegionOverrideSession.Set(
                id,
                _refine.Regions.ToList(),
                pipeW: _refine.BaseWidth,
                pipeH: _refine.BaseHeight,
                basePipeline: baseClone);
        }

        private bool TryRestoreRefineSession()
        {
            string id = CurrentCaptureId();
            // Only restore when capture id matches — never attach another page's boxes.
            if (!ComicRegionOverrideSession.TryGet(
                    id, out var regions, out _, out _, out Bitmap? baseClone))
                return false;
            if (regions.Count == 0)
            {
                try { baseClone?.Dispose(); } catch { /* ignore */ }
                return false;
            }

            if (baseClone != null)
            {
                _refine.RestoreLocked(baseClone, regions);
            }
            else if (_sourceImage != null)
            {
                try
                {
                    _refine.RestoreLocked((Bitmap)_sourceImage.Clone(), regions);
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            UpdateRefineStatus();
            _lblStatus.Text =
                $"Restored {regions.Count} refined region(s) — locked until next capture / Re-detect.";
            _lblStatus.ForeColor = UiTheme.Ok;
            return true;
        }

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                var s = AppSettings.Current;
                s.NormalizeComicRegionSettings();

                _chkPoiMarkers.Checked = s.ComicPoiMarkers;
                _chkPoiFogOutside.Checked = s.ComicPoiFogOutside;
                _chkPoiAutoStack.Checked = s.ComicPoiAutoStack;
                _trkPoiAutoStackGap.Value = Math.Clamp(
                    s.ComicPoiAutoStackGapPx,
                    _trkPoiAutoStackGap.Minimum,
                    _trkPoiAutoStackGap.Maximum);
                _trkPoiAutoStackMargin.Value = Math.Clamp(
                    s.ComicPoiAutoStackMarginPx,
                    _trkPoiAutoStackMargin.Minimum,
                    _trkPoiAutoStackMargin.Maximum);
                _trkPoiStackBeef.Value = Math.Clamp(
                    (int)Math.Round(s.ComicPoiStackBeefExtra * 100.0),
                    _trkPoiStackBeef.Minimum,
                    _trkPoiStackBeef.Maximum);
                _trkPoiStackBottomPad.Value = Math.Clamp(
                    (int)Math.Round(s.ComicPoiStackBottomPadShare * 100.0),
                    _trkPoiStackBottomPad.Minimum,
                    _trkPoiStackBottomPad.Maximum);
                _chkFog.Checked = s.ComicDetectFog;
                _chkDynamicFog.Checked = s.ComicDynamicFog;
                _trkFogAmount.Value = FogToTick(s.ComicDetectFogAmount);
                _trkClusterX.Value = GapToTick(s.ComicClusterGapX);
                _trkClusterY.Value = GapToTick(s.ComicClusterGapY);
                _trkInflateX.Value = InflateToTick(s.ComicInflateFracX);
                _trkInflateY.Value = InflateToTick(s.ComicInflateFracY);
                _trkPadding.Value = Math.Clamp(s.ComicRegionPadding, _trkPadding.Minimum, _trkPadding.Maximum);
                _trkDenseCount.Value = Math.Clamp(s.ComicDenseIslandCount, _trkDenseCount.Minimum, _trkDenseCount.Maximum);
                _chkMergeOverlap.Checked = s.ComicMergeOverlappingIslands;
                _chkSplitLarge.Checked = s.ComicSplitLargeRegions;
                _trkOrphan.Value = Math.Clamp(s.ComicOrphanRecoverPasses, _trkOrphan.Minimum, _trkOrphan.Maximum);
                _trkMinAlnum.Value = Math.Clamp(s.ComicMinIslandAlnum, _trkMinAlnum.Minimum, _trkMinAlnum.Maximum);
                _chkSequential.Checked = s.ComicSequentialRegions;

                RefreshValueLabels();
                ApplyFogUiState();
                ApplyPoiPreviewUi();

                // Cache → last capture → built-in sample so knobs are usable immediately.
                EnsurePreviewImageLoaded();

                if (HasSource)
                {
                    if (DevCaptureCache.IsSample)
                    {
                        _lblStatus.Text = "Sample panel loaded — Open image… for your own capture.";
                        _lblStatus.ForeColor = UiTheme.Ok;
                    }
                    else if (s.ComicPoiMarkers)
                    {
                        _lblStatus.Text =
                            "Comic Book + POI — green region boxes (same as live speak).";
                        _lblStatus.ForeColor = UiTheme.Ok;
                    }
                    else if (s.ComicBook)
                    {
                        _lblStatus.Text = "Comic Book is ON — these settings apply to the next speak.";
                        _lblStatus.ForeColor = UiTheme.Ok;
                    }
                    else
                    {
                        _lblStatus.Text =
                            "Comic Book is OFF — enable Ctrl+B for live reads (preview still works).";
                        _lblStatus.ForeColor = UiTheme.Warn;
                    }
                }
                else
                {
                    _lblStatus.Text = "Load an image to enable balloon controls.";
                    _lblStatus.ForeColor = UiTheme.Warn;
                }
            }
            finally
            {
                _loading = false;
            }

            ApplyControlsEnabled();
            ApplyKeyboardTabOrder();
            // Restore locked refine across tab switches; otherwise re-preview if prep changed.
            if (TryRestoreRefineSession())
            {
                // Keep locked boxes.
            }
            else
            {
                InvalidatePreviewForCurrentPrep(force: true);
            }
        }

        private bool HasSource => _sourceImage != null;
        private int _appliedPrepGeneration = -1;
        private string _appliedPrepSig = "";

        /// <summary>
        /// Drop stale overlay and re-run detect preview when shared Image prep knobs change.
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
            // Prep change invalidates pipeline coords — must drop locked refine.
            ComicRegionOverrideSession.Clear();
            if (_sourceImage != null)
            {
                try
                {
                    _refine.SetSeed((Bitmap)_sourceImage.Clone(), Array.Empty<Rectangle>());
                }
                catch { /* ignore */ }
            }
            _lblPreviewStatus.Text =
                $"Source: {_sourceLabel}  ·  re-preview with current prep…";
            _lblPreviewStatus.ForeColor = UiTheme.Warn;
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
                    // Skip only if we already hold this exact OCR run.
                    if (_sourceImage != null &&
                        string.Equals(_sourceLabel, "last capture", StringComparison.Ordinal) &&
                        stamp != default &&
                        stamp == _lastCaptureStamp &&
                        _sourceImage.Width == last.Width &&
                        _sourceImage.Height == last.Height)
                    {
                        try { last.Dispose(); } catch { /* ignore */ }
                        // Same capture frame — restore locked refine if any.
                        TryRestoreRefineSession();
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
                Debug.WriteLine($"[Balloons] EnsurePreviewImageLoaded: {ex.Message}");
            }
        }

        private void ApplyControlsEnabled()
        {
            bool ready = HasSource && !_speakBusy;
            // §9 Sequential: non-POI speak path, and POI multi fallback when stack off/fail.
            bool sequentialOk = ready;

            bool poiOn = ready && _chkPoiMarkers.Checked;
            _chkPoiMarkers.Enabled = ready;
            _chkPoiFogOutside.Enabled = poiOn;
            _chkPoiAutoStack.Enabled = poiOn;
            bool stackOn = poiOn && _chkPoiAutoStack.Checked;
            _trkPoiAutoStackGap.Enabled = stackOn;
            _lblPoiAutoStackGapVal.Enabled = stackOn;
            _trkPoiAutoStackMargin.Enabled = stackOn;
            _lblPoiAutoStackMarginVal.Enabled = stackOn;
            _trkPoiStackBeef.Enabled = stackOn;
            _lblPoiStackBeefVal.Enabled = stackOn;
            _trkPoiStackBottomPad.Enabled = stackOn;
            _lblPoiStackBottomPadVal.Enabled = stackOn;
            _chkFog.Enabled = ready;
            _chkDynamicFog.Enabled = ready && _chkFog.Checked;
            // Dyn auto-search ignores the slider — keep it off while dyn is on.
            bool fogSliderOn = ready && _chkFog.Checked && !_chkDynamicFog.Checked;
            _trkFogAmount.Enabled = fogSliderOn;
            _lblFogAmount.Enabled = fogSliderOn;
            _lblFogAmountVal.Enabled = fogSliderOn;
            _lblFogAmount.Text = (ready && _chkFog.Checked && _chkDynamicFog.Checked)
                ? "Fog strength (auto)"
                : "Fog strength";
            _trkClusterX.Enabled = ready;
            _trkClusterY.Enabled = ready;
            _trkInflateX.Enabled = ready;
            _trkInflateY.Enabled = ready;
            // Crop pad / dense pad still size islands for POI (bullseyes + outside fog).
            _trkPadding.Enabled = ready;
            _trkDenseCount.Enabled = ready;
            _chkMergeOverlap.Enabled = ready;
            _chkSplitLarge.Enabled = ready;
            _trkOrphan.Enabled = ready;
            _trkMinAlnum.Enabled = ready;
            _chkSequential.Enabled = sequentialOk;

            _btnPreview.Enabled = ready && !_snapBusy;
            _btnSpeak.Enabled = ready && !_snapBusy;
            _btnReset.Enabled = ready && !_snapBusy;
            _btnRedetect.Enabled = ready && !_snapBusy;
            UpdateRefineButtons();
            // Load actions always available (replace sample / load capture / snap region).
            _btnOpenImage.Enabled = !_speakBusy && !_snapBusy;
            _btnUseLast.Enabled = !_speakBusy && !_snapBusy;
            _btnSnapRegion.Enabled =
                !_speakBusy && !_snapBusy && _onCaptureActiveRegion != null;
            _refine.Enabled = ready && !_snapBusy;
            RefreshStopEnabled();
            // Recompute TabStop — Enabled flips must not leave TabStop=false forever.
            if (!_loading)
                ApplyKeyboardTabOrder();
        }

        private void UpdateRefineButtons()
        {
            bool ready = HasSource && !_speakBusy;
            bool hasSel = ready && _refine.SelectedIndex >= 0;
            bool hasAny = ready && _refine.RegionCount > 0;
            _btnRegionUp.Enabled = hasSel && _refine.SelectedIndex > 0;
            _btnRegionDown.Enabled = hasSel && _refine.SelectedIndex < _refine.RegionCount - 1;
            _btnRegionDelete.Enabled = hasSel;
            _btnRedetect.Enabled = ready;
            _ = hasAny; // speak works with 0 (falls back to auto detect)
        }

        private void UpdateRefineStatus()
        {
            if (IsDisposed)
                return;
            int n = _refine.RegionCount;
            string dirty = HasLockedRefine ? "  ·  LOCKED override" : "  ·  auto seed";
            int sel = _refine.SelectedIndex;
            string selTxt = sel >= 0 ? $"  ·  selected #{sel + 1}" : "";
            string poiNote;
            if (!_chkPoiMarkers.Checked)
            {
                poiNote = "  ·  solid = Speak crop (grow + pad baked in)";
            }
            else if (_refine.IsShowingPoiGuidePreview)
            {
                poiNote = _refine.RegionCount >= 2
                    ? "  ·  POI map on tone (speak = sequential)"
                    : "  ·  POI guide on tone (= VL input)";
            }
            else if (_chkPoiFogOutside.Checked)
            {
                poiNote = "  ·  green POI boxes + thick fog outside islands";
            }
            else
            {
                poiNote = "  ·  green boxes = Local-LLM POI guide";
            }
            _lblPreviewStatus.Text =
                $"Source: {_sourceLabel}  ·  {n} region" +
                (n == 1 ? "" : "s") +
                dirty + selTxt +
                poiNote;
            _lblPreviewStatus.ForeColor = HasLockedRefine ? UiTheme.Warn
                : (n > 0 ? UiTheme.Ok : UiTheme.FgMuted);
            UpdateRefineButtons();
        }

        /// <summary>
        /// Write knobs into <see cref="AppSettings.Current"/> immediately so live
        /// detect / crop-pad paint see them. Disk write is debounced unless
        /// <paramref name="writeDiskNow"/>.
        /// </summary>
        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            bool poiWas = s.ComicPoiMarkers;
            bool comicWas = s.ComicBook;

            s.ComicPoiMarkers = _chkPoiMarkers.Checked;
            s.ComicPoiFogOutside = _chkPoiFogOutside.Checked;
            s.ComicPoiAutoStack = _chkPoiAutoStack.Checked;
            s.ComicPoiAutoStackGapPx = _trkPoiAutoStackGap.Value;
            s.ComicPoiAutoStackMarginPx = _trkPoiAutoStackMargin.Value;
            s.ComicPoiStackBeefExtra = _trkPoiStackBeef.Value / 100.0;
            s.ComicPoiStackBottomPadShare = _trkPoiStackBottomPad.Value / 100.0;
            // POI is a Comic Book attack — keep MODE row / sidebar in sync.
            // Only force Comic on when the user actually enables POI (not every Persist).
            if (s.ComicPoiMarkers && !comicWas)
                s.ComicBook = true;
            s.ComicDetectFog = _chkFog.Checked;
            s.ComicDynamicFog = _chkDynamicFog.Checked;
            s.ComicDetectFogAmount = TickToFog(_trkFogAmount.Value);
            s.ComicClusterGapX = TickToGap(_trkClusterX.Value);
            s.ComicClusterGapY = TickToGap(_trkClusterY.Value);
            s.ComicInflateFracX = TickToInflate(_trkInflateX.Value);
            s.ComicInflateFracY = TickToInflate(_trkInflateY.Value);
            s.ComicRegionPadding = _trkPadding.Value;
            s.ComicDenseIslandCount = _trkDenseCount.Value;
            s.ComicMergeOverlappingIslands = _chkMergeOverlap.Checked;
            s.ComicSplitLargeRegions = _chkSplitLarge.Checked;
            s.ComicOrphanRecoverPasses = _trkOrphan.Value;
            s.ComicMinIslandAlnum = _trkMinAlnum.Value;
            s.ComicSequentialRegions = _chkSequential.Checked;
            s.NormalizeComicRegionSettings();
            // If user left Comic Book (POI suspended), do not let a stale checkbox
            // rewrite POI on via Persist before Reload — honor mode.
            if (!s.ComicBook && s.ComicPoiMarkers)
            {
                s.ComicPoiMarkers = false;
                if (!_loading && _chkPoiMarkers.Checked)
                {
                    _loading = true;
                    try { _chkPoiMarkers.Checked = false; }
                    finally { _loading = false; }
                }
            }
            s.NormalizeModeFlags();

            // MODE stack: only when comic/POI actually flipped.
            if (poiWas != s.ComicPoiMarkers || comicWas != s.ComicBook)
            {
                try { _onModeChanged?.Invoke(); } catch { /* ignore */ }
            }

            if (writeDiskNow)
                FlushDiskSave(force: true);
            else
                ScheduleDiskSave();
        }

        /// <summary>
        /// POI runs on the Comic Book path — force Comic Book on and refresh overlay MODE.
        /// </summary>
        private void ForceComicBookModeForPoi()
        {
            var s = AppSettings.Current;
            s.ComicPoiMarkers = true;
            if (!s.ComicBook)
            {
                // Same path as sidebar COMIC BOOK: ComicBook=true + save + profile sync.
                s.SetFlag(AppSettings.FlagIndexComicBook, true);
            }
            else
            {
                s.NormalizeModeFlags();
                try { s.Save(); s.SyncActiveProfileFile(); } catch { /* ignore */ }
            }
            try { _onModeChanged?.Invoke(); } catch { /* host refresh */ }
        }

        /// <summary>POI guide on tone (same DrawRegionGuides as live). Stack is Speak-only.</summary>
        private void ApplyPoiPreviewUi()
        {
            bool poi = _chkPoiMarkers.Checked;
            bool outside = poi && _chkPoiFogOutside.Checked;
            try
            {
                _refine.ShowPoiMarkers = poi;
                _refine.ShowPoiOutsideFog = outside;
                // Never swap preview to stack canvas — user must edit islands on the page.
                _refine.ShowPoiAutoStack = false;
                _refine.PoiAutoStackGapPx = _trkPoiAutoStackGap.Value;
                _refine.Invalidate();
            }
            catch { /* ignore */ }
            if (_lblPreviewHeader != null)
            {
                if (poi && _chkPoiAutoStack.Checked)
                {
                    _lblPreviewHeader.Text =
                        "PREVIEW — full page for editing  ·  Speak = orange island stack VL  ·  drag / resize / add";
                }
                else if (poi && _refine.RegionCount >= 2)
                {
                    _lblPreviewHeader.Text = _chkSequential.Checked
                        ? "PREVIEW — POI map on TONE (Speak multi = sequential)  ·  drag / resize / add"
                        : "PREVIEW — POI map on TONE (Speak multi = crop-stack)  ·  drag / resize / add";
                }
                else if (poi && outside)
                {
                    _lblPreviewHeader.Text =
                        "PREVIEW — TONE + outside fog + green boxes = VL input (1 island)";
                }
                else if (poi)
                {
                    _lblPreviewHeader.Text =
                        "PREVIEW — TONE + green boxes = VL input (1 island)  ·  drag / resize / add";
                }
                else
                {
                    _lblPreviewHeader.Text =
                        "PREVIEW — detect view (fog when on)  ·  drag / resize / add  ·  Del = remove  ·  Ctrl+↑↓ = reorder";
                }
            }
            UpdateRefineStatus();
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
            try { AppSettings.Current.Save(); } catch { /* still keep in-memory */ }
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            ApplyFogUiState();
            ApplyControlsEnabled();
            Persist(writeDiskNow: false);
            // Crop-pad dashed outline reads live settings — refresh paint.
            try { _refine.Invalidate(); } catch { /* ignore */ }
            if (_sourceImage != null)
            {
                if (HasLockedRefine)
                {
                    // Keep boxes; still refresh detect-view base so fog strength is visible.
                    _lblStatus.Text =
                        "Saved knobs — refined boxes kept · fog preview updating… " +
                        "Re-detect to re-run OCR.";
                    _lblStatus.ForeColor = UiTheme.Warn;
                    ScheduleDetectViewRefresh();
                }
                else
                {
                    _lblStatus.Text = "Saved — live preview…";
                    _lblStatus.ForeColor = UiTheme.Ok;
                    ScheduleLivePreview();
                }
            }
            else
            {
                _lblStatus.Text = "Load an image to enable balloon controls.";
                _lblStatus.ForeColor = UiTheme.Warn;
            }
        }

        /// <summary>
        /// Debounced: rebuild detect-view base (prep + fog) without re-running OCR detect.
        /// Used when boxes are locked so fog slider still updates the preview image.
        /// </summary>
        private void ScheduleDetectViewRefresh()
        {
            if (_loading || _sourceImage == null || _speakBusy || IsDisposed)
                return;
            try
            {
                // Reuse live debounce timer; RunDetectViewRefreshOnly is chosen via flag.
                _detectViewOnly = true;
                _liveTimer.Stop();
                _liveTimer.Start();
            }
            catch { /* ignore */ }
        }

        private void ScheduleLivePreview()
        {
            // Live detect may already be running — debounce restarts it after knobs settle.
            // Never auto-redect while the user is refining boxes.
            if (_loading || _sourceImage == null || _speakBusy || IsDisposed)
                return;
            if (HasLockedRefine)
                return;
            try
            {
                _detectViewOnly = false;
                _liveTimer.Stop();
                _liveTimer.Start();
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Rebuild prep + fog base for the refine surface; keep existing boxes.
        /// So Fog strength / on-off is visible without wiping a locked override.
        /// </summary>
        private async Task RefreshDetectViewBaseAsync()
        {
            if (_sourceImage == null || IsDisposed || _speakBusy)
                return;

            Persist(writeDiskNow: false);
            AppSettings.Current.NormalizeImagePrepSettings();
            AppSettings.Current.NormalizeComicRegionSettings();

            int gen = ++_liveGeneration;
            Bitmap? work = null;
            _progress.BeginWork();
            try
            {
                work = new Bitmap(_sourceImage);
                bool fogOn = AppSettings.Current.ComicDetectFog;
                bool dynFog = AppSettings.Current.ComicDynamicFog;
                float fogAmt = AppSettings.Current.ComicDetectFogAmount;
                Bitmap detectView = await Task.Run(
                    () => OcrProcessor.BuildComicDetectViewBitmap(work),
                    CancellationToken.None).ConfigureAwait(true);

                if (IsDisposed || gen != _liveGeneration)
                {
                    try { detectView.Dispose(); } catch { /* ignore */ }
                    return;
                }

                _refine.UpdateBaseKeepRegions(detectView);
                // Locked-box fog view: fixed slider amount only (dyn needs full re-detect).
                string fogHint = !fogOn
                    ? "fog=off"
                    : dynFog
                        ? "dyn=on (re-detect for auto fog)"
                        : $"fog={fogAmt:0.00}";
                _lblPreviewStatus.Text =
                    $"Source: {_sourceLabel}  ·  {_refine.RegionCount} region" +
                    (_refine.RegionCount == 1 ? "" : "s") +
                    $"  ·  detect view ({fogHint})  ·  boxes locked";
                _lblPreviewStatus.ForeColor = UiTheme.Warn;
                if (HasLockedRefine)
                {
                    _lblStatus.Text =
                        $"Detect view updated ({fogHint}). Boxes kept — Re-detect to re-run OCR.";
                    _lblStatus.ForeColor = UiTheme.Warn;
                }
                UpdateRefineStatus();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && gen == _liveGeneration)
                {
                    _lblStatus.Text = $"Fog preview failed: {ex.Message}";
                    _lblStatus.ForeColor = UiTheme.Bad;
                }
            }
            finally
            {
                try { work?.Dispose(); } catch { /* ignore */ }
                if (!IsDisposed)
                    _progress.EndWork();
            }
        }

        private void ApplyFogUiState()
        {
            bool ready = HasSource && !_speakBusy;
            bool fogOn = ready && _chkFog.Checked;
            bool dynOn = fogOn && _chkDynamicFog.Checked;
            // Dyn ignores the slider — disable so it is not a fake control.
            _chkDynamicFog.Enabled = fogOn;
            _trkFogAmount.Enabled = fogOn && !dynOn;
            _lblFogAmount.Enabled = _trkFogAmount.Enabled;
            _lblFogAmountVal.Enabled = _trkFogAmount.Enabled;
            _trkFogAmount.TabStop = _trkFogAmount.Enabled;
            _lblFogAmount.Text = dynOn ? "Fog strength (auto)" : "Fog strength";
        }

        private void ResetDefaults()
        {
            // Default mode → product defaults (POI off). Comic Book mode → Comic
            // Book stock including POI on (same as first MODE enter / fresh comic path).
            bool comic = AppSettings.Current.ComicBook;
            AppSettings.Current.ResetComicRegionSettingsToDefaults(asComicBookMode: comic);
            LoadFromSettings();
            Persist(writeDiskNow: true);
            _lblStatus.Text = comic
                ? "Restored Comic Book balloon defaults (POI on)."
                : "Restored built-in balloon detect defaults.";
            _lblStatus.ForeColor = UiTheme.Ok;
            if (comic)
            {
                try { _onModeChanged?.Invoke(); } catch { /* host refresh */ }
            }
        }

        private void OpenImageFile()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Open comic panel / page image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All files|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                // Opening a file is always a new page — drop prior refine session.
                ComicRegionOverrideSession.NotifyNewCapture();
                using var tmp = new Bitmap(dlg.FileName);
                SetSourceImage(new Bitmap(tmp), Path.GetFileName(dlg.FileName));
                _lblStatus.Text = $"Loaded {_sourceLabel}. Click Preview.";
                _lblStatus.ForeColor = UiTheme.Ok;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Could not open image: {ex.Message}";
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
        /// Hide chrome → snap active F1–F8 region (same as Enter) → load raw → detect preview.
        /// No Local-LLM / TTS (Preview path only; not Speak).
        /// </summary>
        private async Task SnapActiveRegionAsync()
        {
            if (_snapBusy || _speakBusy || _onCaptureActiveRegion == null)
                return;

            _snapBusy = true;
            ApplyControlsEnabled();
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

                // New page — drop prior refine; raw source then full detect Preview.
                ComicRegionOverrideSession.NotifyNewCapture();
                SetSourceImage(bmp, "region snap");
                // End snap-wait bar before detect (detect has its own BeginWork).
                _progress.EndWork();
                await RunPreviewAsync(fromLive: false, forceRedetect: true).ConfigureAwait(true);
                if (!IsDisposed)
                {
                    _lblStatus.Text = "Region snap loaded — detect preview.";
                    _lblStatus.ForeColor = UiTheme.Ok;
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    _lblStatus.Text = $"Snap failed: {ex.Message}";
                    _lblStatus.ForeColor = UiTheme.Bad;
                }
            }
            finally
            {
                _snapBusy = false;
                if (!IsDisposed)
                {
                    // Safe if already ended after successful snap path.
                    if (_progress.IsBusy)
                        _progress.EndWork();
                    ApplyControlsEnabled();
                }
            }
        }

        /// <summary>Prefer shared Image/Balloons cache so both tabs stay aligned.</summary>
        private void SyncFromSharedCache()
        {
            if (!DevCaptureCache.HasImage)
                return;
            var bmp = DevCaptureCache.CloneOrNull();
            if (bmp == null)
                return;
            // Skip replace if we already hold the same shared frame (avoid re-detect spam).
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
            // Bind identity after source is set (uses LastResult stamp for last capture).
            string capId = CurrentCaptureId();
            ComicRegionOverrideSession.NotifySourceIdentity(capId);

            if (TryRestoreRefineSession())
            {
                // Kept locked regions for this capture id.
            }
            else
            {
                // Fresh source — empty boxes until Preview seeds OCR.
                _refine.SetSeed((Bitmap)bmp.Clone(), Array.Empty<Rectangle>());
                _seededDetectSig = "";
                _lblPreviewStatus.Text =
                    $"Source: {_sourceLabel}  ·  {bmp.Width}×{bmp.Height}  ·  Preview to seed OCR boxes";
                _lblPreviewStatus.ForeColor = UiTheme.FgMuted;
            }

            _txtDetail.Text = string.Equals(label, DevCaptureCache.SampleLabel, StringComparison.Ordinal)
                ? "Sample panel — Open image… or Use last capture for a real page."
                : "Adjust knobs for live preview, or Preview / Speak. Refined boxes lock until next capture.";
            ApplyControlsEnabled();
            if (!_loading && !_refine.IsDirty && !ComicRegionOverrideSession.IsActive)
                ScheduleLivePreview();
        }

        private void DisposeSource()
        {
            if (_sourceImage != null)
            {
                try { _sourceImage.Dispose(); } catch { /* ignore */ }
                _sourceImage = null;
            }
        }

        /// <summary>
        /// Cancel in-flight preview/speak. Do not dispose the CTS until form close —
        /// disposing while a task still observes the token can throw ObjectDisposedException.
        /// </summary>
        private void CancelWork(bool disposeCts = false)
        {
            try { _workCts?.Cancel(); } catch { /* ignore */ }
            if (disposeCts)
            {
                try { _workCts?.Dispose(); } catch { /* ignore */ }
                _workCts = null;
            }
            // Invalidate any in-flight UI application of results
            _liveGeneration++;
            if (!IsDisposed && !_speakBusy)
            {
                _lblStatus.Text = "Stopping…";
                _lblStatus.ForeColor = UiTheme.FgMuted;
            }
        }

        private CancellationToken BeginWorkToken()
        {
            try { _workCts?.Cancel(); } catch { /* ignore */ }
            // Leave old CTS for GC — avoid Dispose while OCR/TTS still observes it.
            _workCts = new CancellationTokenSource();
            return _workCts.Token;
        }

        private bool EnsureSourceImage()
        {
            if (_sourceImage != null)
                return true;
            UseLastCapture();
            if (_sourceImage != null)
                return true;
            _lblStatus.Text = "Open an image or speak a region first.";
            _lblStatus.ForeColor = UiTheme.Warn;
            return false;
        }

        private void RefreshStopEnabled()
        {
            if (IsDisposed) return;
            _btnStop.Enabled = _speakBusy || _previewInFlight > 0;
        }

        private void SetSpeakBusy(bool busy)
        {
            _speakBusy = busy;
            ApplyControlsEnabled();
            ApplyKeyboardTabOrder();
        }

        private async Task RunPreviewAsync(bool fromLive = false, bool forceRedetect = false)
        {
            if (_speakBusy)
                return;
            if (!EnsureSourceImage())
                return;

            // Locked refine: never wipe on Preview / live unless Re-detect.
            if (HasLockedRefine && !forceRedetect)
            {
                if (!fromLive)
                {
                    _lblStatus.Text =
                        $"Keeping {_refine.RegionCount} refined region(s). Re-detect to re-run OCR · Speak uses your boxes.";
                    _lblStatus.ForeColor = UiTheme.Warn;
                    UpdateRefineStatus();
                }
                return;
            }

            Persist(writeDiskNow: !fromLive);
            AppSettings.Current.NormalizeImagePrepSettings();
            AppSettings.Current.NormalizeComicRegionSettings();
            _appliedPrepGeneration = DevCaptureCache.PrepGeneration;
            _appliedPrepSig = DevCaptureCache.PrepSettingsSignature();

            var token = BeginWorkToken();
            int gen = ++_liveGeneration;

            _previewInFlight++;
            _progress.BeginWork();
            RefreshStopEnabled();
            bool prepOn = AppSettings.Current.ImagePrepEnabled;
            bool grayOn = prepOn && AppSettings.Current.ImageGrayscale;
            string prepHint =
                $"prep={(prepOn ? "on" : "off")}" +
                $" gray={(grayOn ? "on" : "off")}" +
                $" long={AppSettings.Current.ImageUpscaleLongSide}";
            _lblPreviewStatus.Text = fromLive
                ? $"Live preview — detecting… ({prepHint})"
                : $"Running detect… ({prepHint})";
            _lblPreviewStatus.ForeColor = UiTheme.Warn;
            if (!fromLive)
            {
                _lblStatus.Text = $"Detecting balloons ({prepHint})…";
                _lblStatus.ForeColor = UiTheme.FgMuted;
            }

            try
            {
                Bitmap work;
                try { work = new Bitmap(_sourceImage!); }
                catch (Exception ex)
                {
                    if (!IsDisposed && gen == _liveGeneration)
                    {
                        _lblStatus.Text = $"Preview source error: {ex.Message}";
                        _lblStatus.ForeColor = UiTheme.Bad;
                    }
                    return;
                }

                ComicRegionPreviewResult result;
                try
                {
                    result = await Task.Run(
                        () => OcrProcessor.PreviewComicRegionsAsync(work, token),
                        token).ConfigureAwait(true);
                }
                finally
                {
                    try { work.Dispose(); } catch { /* ignore */ }
                }

                if (IsDisposed || gen != _liveGeneration) { result.Dispose(); return; }

                // Race fix: user may have refined while detect was running.
                if (HasLockedRefine && !forceRedetect)
                {
                    try { result.BaseImage?.Dispose(); } catch { /* ignore */ }
                    result.BaseImage = null;
                    try { result.Overlay?.Dispose(); } catch { /* ignore */ }
                    result.Overlay = null;
                    result.Dispose();
                    if (!fromLive)
                    {
                        _lblStatus.Text =
                            $"Keeping {_refine.RegionCount} refined region(s) (detect result discarded).";
                        _lblStatus.ForeColor = UiTheme.Warn;
                        UpdateRefineStatus();
                    }
                    return;
                }

                int regionCount = result.RegionCount;
                string detailText = result.Detail ?? "";
                float fogUsed = result.FogAmountUsed;
                bool dynFog = result.DynamicFogSearched;
                float fogStart = result.FogAmountStart;

                Bitmap? baseImg = result.BaseImage;
                result.BaseImage = null;
                // Regions from PreviewComicRegionsAsync are already display-final
                // (grow + crop pad). Sort reading order only — do NOT pad again.
                var displayBoxes = OcrProcessor.SmokeSortComicReadingOrder(
                    result.Regions ?? Array.Empty<Rectangle>());
                try { result.Overlay?.Dispose(); } catch { /* ignore */ }
                result.Overlay = null;
                result.Dispose();

                if (baseImg != null)
                    _refine.SetSeed(baseImg, displayBoxes);
                else if (_sourceImage != null)
                    _refine.SetSeed((Bitmap)_sourceImage.Clone(), displayBoxes);

                // Preview boxes now match live detect knobs (grow/cluster/pad/…)
                MarkSeedMatchesLiveDetectSettings();
                // Re-apply POI guide (same DrawRegionGuides as live).
                ApplyPoiPreviewUi();

                _txtDetail.Text = UiTheme.SanitizeUiEngineNames(detailText);
                UpdateRefineStatus();

                string fogStatus;
                if (!_chkFog.Checked)
                    fogStatus = "fog=off";
                else if (dynFog)
                    fogStatus = $"dyn-fog auto {fogStart:0.00}→{fogUsed:0.00}";
                else
                    fogStatus = $"fog={fogUsed:0.00}";

                _lblPreviewStatus.Text =
                    $"Source: {_sourceLabel}  ·  {regionCount} region" +
                    (regionCount == 1 ? "" : "s") +
                    $"  ·  {fogStatus}";

                if (regionCount > 0)
                {
                    if (_chkPoiMarkers.Checked)
                    {
                        _lblStatus.Text =
                            $"OCR seeded {regionCount} island(s) · {fogStatus}. " +
                            "POI guide = live pipe (green boxes" +
                            (_chkPoiFogOutside.Checked ? " + outside fog" : "") + ").";
                    }
                    else
                    {
                        _lblStatus.Text =
                            $"OCR seeded {regionCount} island(s) · {fogStatus}. " +
                            "Edit boxes to lock · Speak uses your list.";
                    }
                    _lblStatus.ForeColor = UiTheme.Ok;
                }
                else
                {
                    _lblStatus.Text =
                        $"No OCR islands · {fogStatus} — click+drag to add boxes, then Speak.";
                    _lblStatus.ForeColor = UiTheme.Warn;
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && gen == _liveGeneration && !fromLive)
                {
                    _lblStatus.Text = "Preview cancelled.";
                    _lblStatus.ForeColor = UiTheme.FgMuted;
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!IsDisposed && gen == _liveGeneration)
                {
                    _lblStatus.Text = $"Preview failed: {ex.Message}";
                    _lblStatus.ForeColor = UiTheme.Bad;
                    _txtDetail.Text = ex.ToString();
                }
            }
            finally
            {
                _previewInFlight = Math.Max(0, _previewInFlight - 1);
                if (!IsDisposed)
                {
                    _progress.EndWork();
                    RefreshStopEnabled();
                }
            }
        }

        private async Task RunSpeakAsync()
        {
            if (_speakBusy)
                return;
            if (!EnsureSourceImage())
                return;

            Persist(writeDiskNow: true);
            // Stop live timer + any detect
            try { _liveTimer.Stop(); } catch { /* ignore */ }

            // Grow / crop pad / cluster change solid box size. Re-detect first when
            // knobs moved and boxes are not user-locked so Speak matches the preview.
            if (_refine.RegionCount > 0 &&
                !_refine.HasUserOverride &&
                DetectKnobsChangedSinceSeed())
            {
                _lblStatus.Text = "Box settings changed — re-detecting before Speak…";
                _lblStatus.ForeColor = UiTheme.Warn;
                await RunPreviewAsync(fromLive: false, forceRedetect: true)
                    .ConfigureAwait(true);
                if (IsDisposed)
                    return;
            }

            var token = BeginWorkToken();
            int gen = ++_liveGeneration;

            SetSpeakBusy(true);
            _progress.BeginWork();
            _lblPreviewStatus.Text = "Reading text and speaking…";
            _lblPreviewStatus.ForeColor = UiTheme.Warn;
            _lblStatus.Text = "Reading balloons and speaking… this can take a minute.";
            _lblStatus.ForeColor = UiTheme.FgMuted;

            try
            {
                Bitmap work;
                try { work = new Bitmap(_sourceImage!); }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                    {
                        _lblStatus.Text = $"Speak source error: {ex.Message}";
                        _lblStatus.ForeColor = UiTheme.Bad;
                    }
                    return;
                }

                // Prefer on-screen refine boxes (same solid cores + live crop pad).
                List<Rectangle>? overrideRegions = GetActiveOverrideRegions();
                if (overrideRegions != null && overrideRegions.Count == 0)
                    overrideRegions = null;
                // Manual Speak from Balloons satisfies the refine intent — do not also
                // auto-speak the same list when the overlay is hidden later.
                if (overrideRegions != null && overrideRegions.Count > 0)
                    ComicRegionOverrideSession.DisarmOverlaySpeak();

                Debug.WriteLine(
                    $"[Balloons] Speak override={(overrideRegions == null ? "none" : overrideRegions.Count.ToString())} " +
                    $"dirty={_refine.IsDirty} session={ComicRegionOverrideSession.IsActive} " +
                    $"pad={AppSettings.Current.ComicRegionPadding} " +
                    $"grow={AppSettings.Current.ComicInflateFracX:0.##}/{AppSettings.Current.ComicInflateFracY:0.##}");

                ComicRegionSpeakResult result;
                try
                {
                    result = await Task.Run(
                        () => OcrProcessor.SpeakComicFromBitmapAsync(
                            work, token, overrideRegions),
                        token).ConfigureAwait(true);
                }
                finally
                {
                    try { work.Dispose(); } catch { /* ignore */ }
                }

                if (IsDisposed || gen != _liveGeneration) { result.Dispose(); return; }

                int regionCount = result.RegionCount;
                string detailText = result.Detail ?? "";
                string spoken = result.SpokenText ?? "";
                bool unreadable = result.Unreadable;
                // Keep user's refine list; optional overlay is discarded (we paint live).
                try { result.Overlay?.Dispose(); } catch { /* ignore */ }
                result.Overlay = null;
                result.Dispose();

                string detailUi = UiTheme.SanitizeUiEngineNames(detailText);
                // Normalize to \r\n so multiline TextBox shows unit breaks
                // (bare \n from logs/OCR often smashes lines until paste).
                static string ForTextBox(string s) =>
                    (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                _txtDetail.Text = string.IsNullOrWhiteSpace(spoken)
                    ? ForTextBox(detailUi)
                    : ForTextBox(
                        "=== SPOKEN ===\r\n" +
                        spoken +
                        "\r\n\r\n=== PIPELINE ===\r\n" +
                        detailUi);

                bool usedOverride = overrideRegions != null && overrideRegions.Count > 0;
                string usedRefine = usedOverride
                    ? $"  ·  OVERRIDE {overrideRegions!.Count} box(es)"
                    : "  ·  auto OCR";
                _lblPreviewStatus.Text =
                    $"Source: {_sourceLabel}  ·  {regionCount} region" +
                    (regionCount == 1 ? "" : "s") +
                    usedRefine +
                    (unreadable ? "  ·  unreadable" : "  ·  spoke");
                _lblPreviewStatus.ForeColor = unreadable ? UiTheme.Warn : UiTheme.Ok;
                _lblStatus.Text = unreadable
                    ? "Speak finished — unreadable (check detail log)."
                    : usedOverride
                        ? $"Spoke using your {overrideRegions!.Count} refined region(s)."
                        : $"Spoke {regionCount} auto OCR island(s).";
                _lblStatus.ForeColor = unreadable ? UiTheme.Warn : UiTheme.Ok;
                // Keep refine UI after speak (do not reseed).
                UpdateRefineStatus();
                UpdateRefineButtons();
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && gen == _liveGeneration)
                {
                    _lblStatus.Text = "Speak cancelled.";
                    _lblStatus.ForeColor = UiTheme.FgMuted;
                    _lblPreviewStatus.Text = "Cancelled.";
                    _lblPreviewStatus.ForeColor = UiTheme.FgMuted;
                }
            }
            catch (ObjectDisposedException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                if (!IsDisposed && gen == _liveGeneration)
                {
                    _lblStatus.Text = $"Speak failed: {ex.Message}";
                    _lblStatus.ForeColor = UiTheme.Bad;
                    _txtDetail.Text = ex.ToString();
                }
            }
            finally
            {
                if (!IsDisposed)
                {
                    _progress.EndWork();
                    SetSpeakBusy(false);
                }
            }
        }

        private void RefreshValueLabels()
        {
            var inv = CultureInfo.InvariantCulture;
            _lblFogAmountVal.Text = TickToFog(_trkFogAmount.Value).ToString("0.00", inv);
            _lblClusterXVal.Text = TickToGap(_trkClusterX.Value).ToString("0.00", inv);
            _lblClusterYVal.Text = TickToGap(_trkClusterY.Value).ToString("0.00", inv);
            _lblInflateXVal.Text = TickToInflate(_trkInflateX.Value).ToString("0.00", inv);
            _lblInflateYVal.Text = TickToInflate(_trkInflateY.Value).ToString("0.00", inv);
            _lblPaddingVal.Text = $"{_trkPadding.Value}px";
            _lblDenseCountVal.Text = _trkDenseCount.Value == 0
                ? "off"
                : $"{_trkDenseCount.Value}+";
            _lblOrphanVal.Text = _trkOrphan.Value == 0
                ? "off"
                : _trkOrphan.Value.ToString(inv);
            _lblMinAlnumVal.Text = _trkMinAlnum.Value == 0
                ? "off"
                : _trkMinAlnum.Value.ToString(inv);
            _lblPoiAutoStackGapVal.Text = $"{_trkPoiAutoStackGap.Value}px";
            _lblPoiAutoStackMarginVal.Text = $"{_trkPoiAutoStackMargin.Value}px";
            _lblPoiStackBeefVal.Text = $"+{_trkPoiStackBeef.Value}%";
            _lblPoiStackBottomPadVal.Text = $"{_trkPoiStackBottomPad.Value}% bot";
        }

        private static int FogToTick(float v) =>
            Math.Clamp((int)Math.Round(v * 100f), 0, 100);
        private static float TickToFog(int t) => t / 100f;

        private static int GapToTick(double v) =>
            Math.Clamp((int)Math.Round(v * 100.0), 25, 300);
        private static double TickToGap(int t) => t / 100.0;

        private static int InflateToTick(double v) =>
            Math.Clamp((int)Math.Round(v * 100.0), 0, 80);
        private static double TickToInflate(int t) => t / 100.0;

        private static Label MakeSection(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgHeader,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
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

        private static TrackBar MakeTrack(int min, int max, int value)
        {
            var t = new TrackBar
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
            return t;
        }

        private static Control WrapTrack(TrackBar track, Label value)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = UiTheme.Bg,
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
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

        /// <summary>Compact refine toolbar buttons (full text, no forced narrow Width).</summary>
        private static Button MakeRefineButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(70, 32),
                Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(10, 4, 10, 4),
                Font = new Font("Segoe UI", 8.5f),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }
    }
}
