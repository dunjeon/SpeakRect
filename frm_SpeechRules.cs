using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Speech tab (Settings): user name substitutions, pipeline text rules (regex),
    /// and OCR prompts. Profile-backed with reset-to-defaults.
    /// </summary>
    public sealed class frm_SpeechRules : Form
    {
        private readonly ThemedTabControl _innerTabs;
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;

        // ---- Names (user substitutions) ----
        private readonly ListView _listNames;
        private readonly Button _btnNameAdd;
        private readonly Button _btnNameEdit;
        private readonly Button _btnNameDelete;
        private readonly Button _btnNameToggle;
        private readonly Button _btnNameUp;
        private readonly Button _btnNameDown;

        // ---- Text rules (pipeline regex) ----
        private readonly ListView _listText;
        private readonly Button _btnTextAdd;
        private readonly Button _btnTextEdit;
        private readonly Button _btnTextDelete;
        private readonly Button _btnTextToggle;
        private readonly Button _btnTextUp;
        private readonly Button _btnTextDown;
        private readonly Button _btnTextResetOne;
        private readonly Button _btnTextResetAll;
        private readonly ComboBox _cmbTextStageFilter;

        // ---- Prompts ----
        private readonly ComboBox _cmbPromptKey;
        private readonly TextBox _txtPrompt;
        private readonly Label _lblPromptDefault;
        private readonly Button _btnPromptResetOne;
        private readonly Button _btnPromptResetAll;
        private readonly Button _btnPromptSave;
        private bool _promptLoading;
        private bool _promptDirty;

        // ---- Pipeline options (shared) ----
        private readonly CheckBox _chkTitleCaseAllCaps;
        private readonly CheckBox _chkForceLowercase;

        // ---- Shared test / status ----
        private readonly TextBox _txtTestIn;
        private readonly TextBox _txtTestOut;
        private readonly Button _btnTest;
        private readonly Button _btnSpeak;
        private readonly Label _lblStatus;
        private readonly Button? _btnClose;

        private bool _loading;
        private bool _dirty;
        private string _lastPreviewSpeak = "";
        /// <summary>Full ordered text-rule list (filter only affects ListView visibility).</summary>
        private List<SpeechTextRule> _textRules = new();

        private static readonly (string Key, string Label)[] PromptChoices =
        {
            ("FullPrompt", "Comic full panel"),
            ("CropPrompt", "Comic balloon crop"),
            ("SimplePrompt", "Default mode (simple)"),
            ("RecoveryPrompt", "Recovery (fallback)"),
        };

        public frm_SpeechRules(bool embedded = false, Action? onRequestClose = null)
        {
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Speech";
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
                MinimumSize = new Size(640, 620);
                ClientSize = new Size(760, 720);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            UiTheme.ApplyForm(this);
            Font = new Font("Segoe UI", 9f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10, 8, 10, 8),
                BackColor = UiTheme.Bg,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62f)); // inner tabs
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38f)); // test
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f)); // status bar

            _innerTabs = new ThemedTabControl
            {
                Dock = DockStyle.Fill,
                ItemSize = new Size(110, 28),
            };

            var tabNames = MakeInnerTab("Names");
            var tabText = MakeInnerTab("Text rules");
            var tabPrompts = MakeInnerTab("Prompts");

            // ---- Names tab ----
            BuildNamesTab(tabNames, out _listNames, out _btnNameAdd, out _btnNameEdit,
                out _btnNameDelete, out _btnNameToggle, out _btnNameUp, out _btnNameDown);

            // ---- Text rules tab (includes Title-case ALL CAPS + Force lowercase) ----
            BuildTextRulesTab(tabText, out _listText, out _cmbTextStageFilter,
                out _chkTitleCaseAllCaps, out _chkForceLowercase,
                out _btnTextAdd, out _btnTextEdit, out _btnTextDelete, out _btnTextToggle,
                out _btnTextUp, out _btnTextDown, out _btnTextResetOne, out _btnTextResetAll);

            // ---- Prompts tab ----
            BuildPromptsTab(tabPrompts, out _cmbPromptKey, out _txtPrompt,
                out _lblPromptDefault, out _btnPromptSave, out _btnPromptResetOne,
                out _btnPromptResetAll);

            _innerTabs.TabPages.Add(tabNames);
            _innerTabs.TabPages.Add(tabText);
            _innerTabs.TabPages.Add(tabPrompts);
            root.Controls.Add(_innerTabs, 0, 0);

            // ---- Test panel (shared) ----
            root.Controls.Add(BuildTestPanel(
                out _txtTestIn, out _txtTestOut, out _btnTest, out _btnSpeak), 0, 1);

            // ---- Status + close ----
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiTheme.BgBar,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(4, 0, 8, 0),
                BackColor = UiTheme.BgStatus,
                Text = "Ready.",
            };
            bottom.Controls.Add(_lblStatus, 0, 0);

            if (!_embedded)
            {
                _btnClose = MakeSideButton("Close");
                _btnClose.Click += (_, _) =>
                {
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                };
                bottom.Controls.Add(_btnClose, 1, 0);
            }
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            WireEvents();

            Load += (_, _) => LoadFromSettings();
            Resize += (_, _) =>
            {
                SizeListColumns(_listNames);
                SizeListColumns(_listText);
            };
            FormClosing += (_, _) =>
            {
                CommitPromptIfDirty();
                if (_dirty)
                    PersistAll(saveDisk: true);
            };

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
            };
        }

        public void ReloadFromSettings() => LoadFromSettings();

        /// <summary>Push UI into AppSettings (for profile save before snapshot).</summary>
        public void FlushToSettings()
        {
            CommitPromptIfDirty();
            PersistAll(saveDisk: true);
        }

        // =====================================================================
        // Build UI sections
        // =====================================================================

        private static TabPage MakeInnerTab(string title) => new(title)
        {
            BackColor = UiTheme.Bg,
            Padding = new Padding(4),
            UseVisualStyleBackColor = false,
            ForeColor = UiTheme.Fg,
        };

        private void BuildNamesTab(
            TabPage tab,
            out ListView list,
            out Button btnAdd,
            out Button btnEdit,
            out Button btnDelete,
            out Button btnToggle,
            out Button btnUp,
            out Button btnDown)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = UiTheme.Bg,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var header = new Label
            {
                Dock = DockStyle.Fill,
                Text = "NAME RULES — Find as on screen  ·  blank Say as = never speak  ·  profile-backed",
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            root.Controls.Add(header, 0, 0);
            root.SetColumnSpan(header, 2);

            list = MakeListView();
            list.Columns.Add("On", 40);
            list.Columns.Add("Find", 160);
            list.Columns.Add("Say as", 160);
            list.Columns.Add("How", 70);
            list.DoubleClick += (_, _) => EditNameSelected();
            list.KeyDown += Names_KeyDown;
            root.Controls.Add(list, 0, 1);

            var side = MakeSidePanel();
            btnAdd = MakeSideButton("Add…");
            btnEdit = MakeSideButton("Edit…");
            btnDelete = MakeSideButton("Delete");
            btnToggle = MakeSideButton("On/Off");
            btnUp = MakeSideButton("Move up");
            btnDown = MakeSideButton("Move down");
            btnAdd.Click += (_, _) => AddNameRule();
            btnEdit.Click += (_, _) => EditNameSelected();
            btnDelete.Click += (_, _) => DeleteNameSelected();
            btnToggle.Click += (_, _) => ToggleNameSelected();
            btnUp.Click += (_, _) => MoveNameSelected(-1);
            btnDown.Click += (_, _) => MoveNameSelected(1);
            side.Controls.Add(btnAdd);
            side.Controls.Add(btnEdit);
            side.Controls.Add(btnDelete);
            side.Controls.Add(btnToggle);
            side.Controls.Add(btnUp);
            side.Controls.Add(btnDown);
            root.Controls.Add(side, 1, 1);
            tab.Controls.Add(root);
        }

        private void BuildTextRulesTab(
            TabPage tab,
            out ListView list,
            out ComboBox stageFilter,
            out CheckBox chkTitleCaseAllCaps,
            out CheckBox chkForceLowercase,
            out Button btnAdd,
            out Button btnEdit,
            out Button btnDelete,
            out Button btnToggle,
            out Button btnUp,
            out Button btnDown,
            out Button btnResetOne,
            out Button btnResetAll)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = UiTheme.Bg,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // title-case ALL CAPS
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // force lowercase
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f)); // stage filter
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // list

            // Pipeline options live with Text rules (same clean stage as Abbrev/Noise).
            // Title-case ALL CAPS and Force lowercase are mutually exclusive
            // (either or neither — never both).
            chkTitleCaseAllCaps = new CheckBox
            {
                Dock = DockStyle.Fill,
                Text = "Title-case ALL CAPS words  (HELLO → Hello; mixed case left alone)",
                ForeColor = UiTheme.FgMuted,
                BackColor = UiTheme.Bg,
                Checked = false,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            chkTitleCaseAllCaps.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                if (_chkTitleCaseAllCaps.Checked && _chkForceLowercase.Checked)
                {
                    _loading = true;
                    try { _chkForceLowercase.Checked = false; }
                    finally { _loading = false; }
                }
                AppSettings.Current.SpeechTitleCaseAllCaps = _chkTitleCaseAllCaps.Checked;
                // Setter clears force-lower; keep UI in sync if settings path flipped it.
                if (AppSettings.Current.SpeechForceLowercase != _chkForceLowercase.Checked)
                {
                    _loading = true;
                    try { _chkForceLowercase.Checked = AppSettings.Current.SpeechForceLowercase; }
                    finally { _loading = false; }
                }
                MarkChanged(_chkTitleCaseAllCaps.Checked
                    ? "Title-case ALL CAPS on — force lowercase off."
                    : "Title-case ALL CAPS off — OCR casing kept for speech.");
                if (!string.IsNullOrWhiteSpace(_txtTestIn?.Text))
                    RunPreview(speak: false);
            };
            root.Controls.Add(chkTitleCaseAllCaps, 0, 0);
            root.SetColumnSpan(chkTitleCaseAllCaps, 2);

            chkForceLowercase = new CheckBox
            {
                Dock = DockStyle.Fill,
                Text = "Force lowercase for speech  (on = normalize ALL CAPS; off = keep OCR casing)",
                ForeColor = UiTheme.FgMuted,
                BackColor = UiTheme.Bg,
                Checked = false,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            chkForceLowercase.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                if (_chkForceLowercase.Checked && _chkTitleCaseAllCaps.Checked)
                {
                    _loading = true;
                    try { _chkTitleCaseAllCaps.Checked = false; }
                    finally { _loading = false; }
                }
                AppSettings.Current.SpeechForceLowercase = _chkForceLowercase.Checked;
                if (AppSettings.Current.SpeechTitleCaseAllCaps != _chkTitleCaseAllCaps.Checked)
                {
                    _loading = true;
                    try { _chkTitleCaseAllCaps.Checked = AppSettings.Current.SpeechTitleCaseAllCaps; }
                    finally { _loading = false; }
                }
                MarkChanged(_chkForceLowercase.Checked
                    ? "Force lowercase on — title-case ALL CAPS off."
                    : "Force lowercase off — OCR casing kept for speech.");
                // Re-run test preview when sample text is already loaded so the
                // casing toggle is visible immediately (no extra Preview click).
                if (!string.IsNullOrWhiteSpace(_txtTestIn?.Text))
                    RunPreview(speak: false);
            };
            root.Controls.Add(chkForceLowercase, 0, 1);
            root.SetColumnSpan(chkForceLowercase, 2);

            var filterRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiTheme.Bg,
            };
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var lblFilter = new Label
            {
                Text = "Show",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            stageFilter = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            UiTheme.StyleCombo(stageFilter);
            stageFilter.Items.Add("All stages");
            stageFilter.Items.Add("Noise strip");
            stageFilter.Items.Add("Abbreviations");
            stageFilter.Items.Add("Decorators");
            stageFilter.SelectedIndex = 0;
            stageFilter.SelectedIndexChanged += (_, _) => RefreshTextListFilter();
            filterRow.Controls.Add(lblFilter, 0, 0);
            filterRow.Controls.Add(stageFilter, 1, 0);
            root.Controls.Add(filterRow, 0, 2);

            list = MakeListView();
            list.Columns.Add("On", 36);
            list.Columns.Add("Name", 140);
            list.Columns.Add("Stage", 90);
            list.Columns.Add("Pattern", 200);
            list.Columns.Add("Replace", 100);
            list.DoubleClick += (_, _) => EditTextSelected();
            list.KeyDown += TextRules_KeyDown;
            root.Controls.Add(list, 0, 3);

            var side = MakeSidePanel();
            btnAdd = MakeSideButton("Add…");
            btnEdit = MakeSideButton("Edit…");
            btnDelete = MakeSideButton("Delete");
            btnToggle = MakeSideButton("On/Off");
            btnUp = MakeSideButton("Move up");
            btnDown = MakeSideButton("Move down");
            btnResetOne = MakeSideButton("Reset one");
            btnResetAll = MakeSideButton("Reset all");
            btnAdd.Click += (_, _) => AddTextRule();
            btnEdit.Click += (_, _) => EditTextSelected();
            btnDelete.Click += (_, _) => DeleteTextSelected();
            btnToggle.Click += (_, _) => ToggleTextSelected();
            btnUp.Click += (_, _) => MoveTextSelected(-1);
            btnDown.Click += (_, _) => MoveTextSelected(1);
            btnResetOne.Click += (_, _) => ResetTextSelected();
            btnResetAll.Click += (_, _) => ResetAllTextRules();
            side.Controls.Add(btnAdd);
            side.Controls.Add(btnEdit);
            side.Controls.Add(btnDelete);
            side.Controls.Add(btnToggle);
            side.Controls.Add(btnUp);
            side.Controls.Add(btnDown);
            side.Controls.Add(btnResetOne);
            side.Controls.Add(btnResetAll);
            root.Controls.Add(side, 1, 3);
            tab.Controls.Add(root);
        }

        private void BuildPromptsTab(
            TabPage tab,
            out ComboBox cmbKey,
            out TextBox txtBody,
            out Label lblDefault,
            out Button btnSave,
            out Button btnResetOne,
            out Button btnResetAll)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = UiTheme.Bg,
                Padding = new Padding(2, 2, 2, 2),
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

            var header = new Label
            {
                Dock = DockStyle.Fill,
                Text = "OCR PROMPTS — sent to the local vision model  ·  blank = built-in default",
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            root.Controls.Add(header, 0, 0);

            var pickRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiTheme.Bg,
            };
            pickRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
            pickRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var lblWhich = new Label
            {
                Text = "Prompt",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            cmbKey = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            UiTheme.StyleCombo(cmbKey);
            foreach (var (_, label) in PromptChoices)
                cmbKey.Items.Add(label);
            cmbKey.SelectedIndex = 0;
            cmbKey.SelectedIndexChanged += (_, _) => OnPromptKeyChanged();
            pickRow.Controls.Add(lblWhich, 0, 0);
            pickRow.Controls.Add(cmbKey, 1, 0);
            root.Controls.Add(pickRow, 0, 1);

            lblDefault = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgDim,
                Font = new Font("Segoe UI", 7.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "",
            };
            root.Controls.Add(lblDefault, 0, 2);

            txtBody = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Font = new Font("Consolas", 9f),
            };
            UiTheme.StyleTextBox(txtBody);
            txtBody.TextChanged += (_, _) =>
            {
                if (_promptLoading || _loading)
                    return;
                _promptDirty = true;
            };
            root.Controls.Add(txtBody, 0, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0),
                BackColor = UiTheme.Bg,
            };
            btnSave = MakeSideButton("Apply");
            btnSave.Width = 100;
            UiTheme.StylePrimaryButton(btnSave);
            btnSave.Click += (_, _) => SaveCurrentPrompt();
            btnResetOne = MakeSideButton("Reset this");
            btnResetOne.Width = 100;
            btnResetOne.Click += (_, _) => ResetCurrentPrompt();
            btnResetAll = MakeSideButton("Reset all prompts");
            btnResetAll.Width = 130;
            btnResetAll.Click += (_, _) => ResetAllPrompts();
            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnResetOne);
            buttons.Controls.Add(btnResetAll);
            root.Controls.Add(buttons, 0, 4);

            tab.Controls.Add(root);
        }

        private Control BuildTestPanel(
            out TextBox testIn,
            out TextBox testOut,
            out Button btnTest,
            out Button btnSpeak)
        {
            var testPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 6, 0, 0),
            };
            testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
            testPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));
            testPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            testPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));

            var lblTest = new Label
            {
                Dock = DockStyle.Fill,
                Text = "TEST — paste comic/OCR text  ·  Preview runs clean + rules  ·  Speak uses Voice settings",
                ForeColor = UiTheme.FgHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft,
            };
            testIn = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Font = new Font("Consolas", 9f),
                Text = "Mr. Summers said the X-Men will win. BRAP!",
            };
            UiTheme.StyleTextBox(testIn);
            var testRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0),
                BackColor = UiTheme.Bg,
            };
            btnTest = MakeSideButton("Preview");
            btnTest.Width = 100;
            btnTest.Click += (_, _) => RunPreview(speak: false);
            btnSpeak = MakeSideButton("Speak");
            btnSpeak.Width = 100;
            UiTheme.StylePrimaryButton(btnSpeak);
            btnSpeak.Click += (_, _) => RunPreview(speak: true);
            testRow.Controls.Add(btnTest);
            testRow.Controls.Add(btnSpeak);
            testOut = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
            };
            UiTheme.StyleTextBox(testOut);
            testOut.BackColor = UiTheme.BgDeep;
            testOut.ForeColor = UiTheme.Ok;
            testPanel.Controls.Add(lblTest, 0, 0);
            testPanel.Controls.Add(testIn, 0, 1);
            testPanel.Controls.Add(testRow, 0, 2);
            testPanel.Controls.Add(testOut, 0, 3);
            return testPanel;
        }

        private void WireEvents()
        {
            // nothing else for now
        }

        // =====================================================================
        // Load / persist
        // =====================================================================

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                _chkTitleCaseAllCaps.Checked = AppSettings.Current.SpeechTitleCaseAllCaps;
                _chkForceLowercase.Checked = AppSettings.Current.SpeechForceLowercase;

                // Names
                _listNames.BeginUpdate();
                _listNames.Items.Clear();
                foreach (var r in AppSettings.Current.SpeechRules)
                    _listNames.Items.Add(MakeNameItem(r.Clone()));
                _listNames.EndUpdate();
                SizeListColumns(_listNames);

                // Text rules (full list in Tag of items; filter only hides)
                ReloadTextListFromSettings();
                SizeListColumns(_listText);

                // Prompts
                LoadPromptIntoEditor();

                _dirty = false;
                _promptDirty = false;
                SetStatus(
                    $"{_listNames.Items.Count} name rule(s) · " +
                    $"{AppSettings.Current.SpeechTextRules.Count} text rule(s) · prompts ready.");
            }
            finally
            {
                _loading = false;
                BeginInvoke(new Action(() =>
                {
                    try { UiTheme.ApplyTabOrder(this); } catch { /* ignore */ }
                }));
            }
        }

        private void ReloadTextListFromSettings()
        {
            _textRules = AppSettings.Current.SpeechTextRules
                .Select(r => r.Clone()).ToList();
            RebuildTextListView();
        }

        private void RebuildTextListView()
        {
            _listText.BeginUpdate();
            _listText.Items.Clear();
            _listText.Groups.Clear();
            _listText.ShowGroups = false; // custom stage rows — no system group headers (column bleed)

            SpeechTextRuleStage? filter = _cmbTextStageFilter.SelectedIndex switch
            {
                1 => SpeechTextRuleStage.Noise,
                2 => SpeechTextRuleStage.Abbrev,
                3 => SpeechTextRuleStage.Decorators,
                _ => null,
            };

            // When showing all stages, insert solid section header rows (no column-line bleed).
            bool showSectionHeaders = filter == null;
            SpeechTextRuleStage? lastStage = null;

            // Stable stage order when unfiltered (preserve relative order within each stage).
            IEnumerable<SpeechTextRule> ordered = filter == null
                ? _textRules
                    .Select((r, i) => (r, i))
                    .OrderBy(t => (int)t.r.Stage)
                    .ThenBy(t => t.i)
                    .Select(t => t.r)
                : _textRules;

            foreach (var r in ordered)
            {
                if (filter != null && r.Stage != filter.Value)
                    continue;

                if (showSectionHeaders && lastStage != r.Stage)
                {
                    lastStage = r.Stage;
                    _listText.Items.Add(MakeStageHeaderItem(r.Stage));
                }

                // Clone into Tag so list mutations cannot leak into the wrong row.
                _listText.Items.Add(MakeTextItem(r.Clone()));
            }
            _listText.EndUpdate();
            // Do NOT SizeListColumns here — rebuilds run after toggle/edit and would
            // reset user-dragged column widths (white-gutter flicker + lost layout).
        }

        private void PersistAll(bool saveDisk = true)
        {
            if (_loading)
                return;

            AppSettings.Current.SpeechTitleCaseAllCaps = _chkTitleCaseAllCaps.Checked;
            AppSettings.Current.SpeechForceLowercase = _chkForceLowercase.Checked;
            AppSettings.Current.SetSpeechRules(CollectNameRules());
            SyncTextRulesFromListViewTags();
            AppSettings.Current.SetSpeechTextRules(_textRules);
            // Prompts already on Current via SaveCurrentPrompt / CommitPromptIfDirty.

            if (saveDisk)
            {
                try
                {
                    AppSettings.Current.PersistSpeechRules();
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    SetStatus($"Save failed: {ex.Message}", bad: true);
                    return;
                }
            }
            else
            {
                _dirty = true;
            }
        }

        private void MarkChanged(string? status = null)
        {
            if (_loading)
                return;
            PersistAll(saveDisk: true);
            SetStatus(status ??
                $"Saved · {_listNames.Items.Count} name · " +
                $"{AppSettings.Current.SpeechTextRules.Count} text rules.");
        }

        // =====================================================================
        // Names list
        // =====================================================================

        private static ListViewItem MakeNameItem(SpeechRule rule)
        {
            var item = new ListViewItem(rule.Enabled ? "✓" : "—");
            item.SubItems.Add(rule.Match);
            item.SubItems.Add(string.IsNullOrEmpty(rule.Replace) ? "(never speak)" : rule.Replace);
            item.SubItems.Add(rule.Kind == SpeechMatchKind.Phrase ? "Anywhere" : "Word");
            item.Tag = rule;
            item.ForeColor = rule.Enabled ? UiTheme.Fg : UiTheme.FgDim;
            return item;
        }

        private void RefreshNameItem(ListViewItem item, SpeechRule rule)
        {
            item.Text = rule.Enabled ? "✓" : "—";
            item.SubItems[1].Text = rule.Match;
            item.SubItems[2].Text = string.IsNullOrEmpty(rule.Replace) ? "(never speak)" : rule.Replace;
            item.SubItems[3].Text = rule.Kind == SpeechMatchKind.Phrase ? "Anywhere" : "Word";
            item.Tag = rule;
            item.ForeColor = rule.Enabled ? UiTheme.Fg : UiTheme.FgDim;
        }

        private List<SpeechRule> CollectNameRules()
        {
            var list = new List<SpeechRule>();
            foreach (ListViewItem item in _listNames.Items)
            {
                if (item.Tag is SpeechRule r)
                    list.Add(r.Clone());
            }
            return list;
        }

        private void AddNameRule()
        {
            if (_listNames.Items.Count >= SpeechRule.MaxRules)
            {
                SetStatus($"Maximum {SpeechRule.MaxRules} name rules.", bad: true);
                return;
            }
            if (!EditNameDialog.Show(GetModalOwner(), null, out SpeechRule? rule) || rule == null)
                return;
            _listNames.Items.Add(MakeNameItem(rule));
            _listNames.Items[^1].Selected = true;
            _listNames.EnsureVisible(_listNames.Items.Count - 1);
            MarkChanged();
        }

        private void EditNameSelected()
        {
            if (_listNames.SelectedItems.Count == 0)
            {
                SetStatus("Select a name rule to edit.", bad: true);
                return;
            }
            var item = _listNames.SelectedItems[0];
            if (item.Tag is not SpeechRule existing)
                return;
            if (!EditNameDialog.Show(GetModalOwner(), existing, out SpeechRule? rule) || rule == null)
                return;
            RefreshNameItem(item, rule);
            MarkChanged();
        }

        private void DeleteNameSelected()
        {
            if (_listNames.SelectedItems.Count == 0)
            {
                SetStatus("Select a name rule to delete.", bad: true);
                return;
            }
            int idx = _listNames.SelectedIndices[0];
            _listNames.Items.RemoveAt(idx);
            if (_listNames.Items.Count > 0)
            {
                int next = Math.Min(idx, _listNames.Items.Count - 1);
                _listNames.Items[next].Selected = true;
            }
            MarkChanged();
        }

        private void ToggleNameSelected()
        {
            if (_listNames.SelectedItems.Count == 0)
            {
                SetStatus("Select a name rule to toggle.", bad: true);
                return;
            }
            var item = _listNames.SelectedItems[0];
            if (item.Tag is not SpeechRule r)
                return;
            r.Enabled = !r.Enabled;
            RefreshNameItem(item, r);
            MarkChanged();
        }

        private void MoveNameSelected(int delta)
        {
            if (_listNames.SelectedItems.Count == 0)
                return;
            int idx = _listNames.SelectedIndices[0];
            int dest = idx + delta;
            if (dest < 0 || dest >= _listNames.Items.Count)
                return;
            var item = _listNames.Items[idx];
            _listNames.Items.RemoveAt(idx);
            _listNames.Items.Insert(dest, item);
            item.Selected = true;
            item.EnsureVisible();
            MarkChanged();
        }

        private void Names_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteNameSelected(); }
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; EditNameSelected(); }
            else if (e.KeyCode == Keys.Insert) { e.Handled = true; AddNameRule(); }
        }

        // =====================================================================
        // Text rules list
        // =====================================================================

        private static ListViewItem MakeTextItem(SpeechTextRule rule)
        {
            var item = new ListViewItem(rule.Enabled ? "✓" : "—");
            item.SubItems.Add(rule.Name);
            item.SubItems.Add(SpeechTextRule.StageLabel(rule.Stage));
            item.SubItems.Add(Truncate(rule.Pattern, 80));
            item.SubItems.Add(string.IsNullOrEmpty(rule.Replace) ? "(strip)" : Truncate(rule.Replace, 40));
            item.Tag = rule;
            item.ForeColor = rule.Enabled ? UiTheme.Fg : UiTheme.FgDim;
            return item;
        }

        /// <summary>
        /// Non-interactive stage band. Owner-draw paints a continuous header so
        /// Details column lines do not cut through the stage name.
        /// </summary>
        private static ListViewItem MakeStageHeaderItem(SpeechTextRuleStage stage)
        {
            string label = SpeechTextRule.StageLabel(stage).ToUpperInvariant();
            var item = new ListViewItem(label);
            // Empty subitems keep column count aligned; paint ignores their text.
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.Tag = new ListSectionHeader(label);
            item.ForeColor = UiTheme.FgHeader;
            // Headers are not selectable for edit actions (still can focus for keyboard skip).
            return item;
        }

        private static bool IsStageHeader(ListViewItem? item) =>
            item?.Tag is ListSectionHeader;

        /// <summary>
        /// Push visible ListView item Tags back into <see cref="_textRules"/> by id
        /// so edits/toggles on filtered views are not lost.
        /// </summary>
        private void SyncTextRulesFromListViewTags()
        {
            foreach (ListViewItem item in _listText.Items)
            {
                if (item.Tag is not SpeechTextRule edited)
                    continue;
                int idx = _textRules.FindIndex(r =>
                    r.Id.Equals(edited.Id, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _textRules[idx] = edited.Clone();
                else
                    _textRules.Add(edited.Clone());
            }
        }

        private void RefreshTextListFilter()
        {
            SyncTextRulesFromListViewTags();
            RebuildTextListView();
        }

        private SpeechTextRule? SelectedTextRule()
        {
            if (_listText.SelectedItems.Count == 0)
                return null;
            return _listText.SelectedItems[0].Tag as SpeechTextRule;
        }

        private void AddTextRule()
        {
            if (_textRules.Count >= SpeechTextRule.MaxRules)
            {
                SetStatus($"Maximum {SpeechTextRule.MaxRules} text rules.", bad: true);
                return;
            }
            if (!EditTextRuleDialog.Show(GetModalOwner(), null, out SpeechTextRule? rule) || rule == null)
                return;
            SyncTextRulesFromListViewTags();
            _textRules.Add(rule);
            RebuildTextListView();
            SelectTextRuleById(rule.Id);
            MarkChanged("Added text rule · saved.");
        }

        private void EditTextSelected()
        {
            if (SelectedTextRule() is not SpeechTextRule existing)
            {
                SetStatus(
                    IsStageHeader(_listText.SelectedItems.Count > 0 ? _listText.SelectedItems[0] : null)
                        ? "That row is a stage label — select a rule under it."
                        : "Select a text rule to edit.",
                    bad: true);
                return;
            }
            if (!EditTextRuleDialog.Show(GetModalOwner(), existing, out SpeechTextRule? rule) || rule == null)
                return;
            SyncTextRulesFromListViewTags();
            int idx = _textRules.FindIndex(r =>
                r.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _textRules[idx] = rule;
            RebuildTextListView();
            SelectTextRuleById(rule.Id);
            MarkChanged("Text rule updated · saved.");
        }

        private void DeleteTextSelected()
        {
            if (SelectedTextRule() is not SpeechTextRule r)
            {
                SetStatus("Select a text rule to delete.", bad: true);
                return;
            }
            if (r.IsBuiltIn)
            {
                var dr = UiMessageBox.Show(GetModalOwner(),
                    $"“{r.Name}” is a built-in rule.\n\n" +
                    "Delete removes it from this profile. It stays gone across restarts " +
                    "until you use Reset all (or Reset this on a restored copy).\n\nDelete anyway?",
                    "Delete built-in text rule",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes)
                    return;
            }
            SyncTextRulesFromListViewTags();
            _textRules.RemoveAll(x => x.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
            RebuildTextListView();
            MarkChanged("Text rule deleted · saved.");
        }

        private void ToggleTextSelected()
        {
            if (SelectedTextRule() is not SpeechTextRule r)
            {
                SetStatus("Select a text rule to toggle.", bad: true);
                return;
            }
            r.Enabled = !r.Enabled;
            SyncTextRulesFromListViewTags();
            RebuildTextListView();
            SelectTextRuleById(r.Id);
            MarkChanged(r.Enabled ? "Enabled · saved." : "Disabled · saved.");
        }

        private void MoveTextSelected(int delta)
        {
            if (SelectedTextRule() is not SpeechTextRule sel)
                return;
            SyncTextRulesFromListViewTags();
            int idx = _textRules.FindIndex(r =>
                r.Id.Equals(sel.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                return;

            // Only reorder within the same pipeline stage (Noise/Abbrev/Decorators).
            // Crossing stages looked broken under "All stages" (display is stage-grouped).
            int dest = -1;
            if (delta < 0)
            {
                for (int i = idx - 1; i >= 0; i--)
                {
                    if (_textRules[i].Stage == sel.Stage)
                    {
                        dest = i;
                        break;
                    }
                }
            }
            else
            {
                for (int i = idx + 1; i < _textRules.Count; i++)
                {
                    if (_textRules[i].Stage == sel.Stage)
                    {
                        dest = i;
                        break;
                    }
                }
            }
            if (dest < 0)
                return;

            var rule = _textRules[idx];
            _textRules.RemoveAt(idx);
            _textRules.Insert(dest, rule);
            RebuildTextListView();
            SelectTextRuleById(rule.Id);
            MarkChanged("Order updated · saved.");
        }

        private void ResetTextSelected()
        {
            if (SelectedTextRule() is not SpeechTextRule r)
            {
                SetStatus("Select a built-in rule to reset.", bad: true);
                return;
            }
            var def = SpeechTextRulesCatalog.CreateDefaults()
                .FirstOrDefault(d => d.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
            if (def == null)
            {
                SetStatus("Only built-in rules can be reset to default (custom: edit or delete).", bad: true);
                return;
            }
            SyncTextRulesFromListViewTags();
            int idx = _textRules.FindIndex(x =>
                x.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _textRules[idx] = def.Clone();
            RebuildTextListView();
            SelectTextRuleById(def.Id);
            MarkChanged($"Reset “{def.Name}” to default · saved.");
        }

        private void ResetAllTextRules()
        {
            var dr = UiMessageBox.Show(GetModalOwner(),
                "Reset ALL pipeline text rules to SpeakRect built-ins?\n\n" +
                "Custom rules will be removed. Name rules and prompts are not changed.",
                "Reset text rules",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes)
                return;
            AppSettings.Current.ResetSpeechTextRulesToDefaults();
            ReloadTextListFromSettings();
            try
            {
                AppSettings.Current.PersistSpeechRules();
                _dirty = false;
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}", bad: true);
                return;
            }
            SetStatus($"Reset {AppSettings.Current.SpeechTextRules.Count} text rules to defaults.");
        }

        private void SelectTextRuleById(string id)
        {
            foreach (ListViewItem item in _listText.Items)
            {
                if (item.Tag is SpeechTextRule r &&
                    r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.EnsureVisible();
                    break;
                }
            }
        }

        private void TextRules_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteTextSelected(); }
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; EditTextSelected(); }
            else if (e.KeyCode == Keys.Insert) { e.Handled = true; AddTextRule(); }
        }

        // =====================================================================
        // Prompts
        // =====================================================================

        private string CurrentPromptKey()
        {
            int i = _cmbPromptKey.SelectedIndex;
            if (i < 0 || i >= PromptChoices.Length)
                return PromptChoices[0].Key;
            return PromptChoices[i].Key;
        }

        private void OnPromptKeyChanged()
        {
            if (_promptLoading || _loading)
            {
                LoadPromptIntoEditor();
                return;
            }
            CommitPromptIfDirty();
            LoadPromptIntoEditor();
        }

        private void LoadPromptIntoEditor()
        {
            _promptLoading = true;
            try
            {
                string key = CurrentPromptKey();
                string resolved = AppSettings.Current.GetResolvedPromptByKey(key);
                bool isDefault = AppSettings.Current.IsPromptUsingDefault(key);
                _txtPrompt.Text = resolved;
                _lblPromptDefault.Text = isDefault
                    ? "Using built-in default (edit and Apply to override)."
                    : "Custom override (Reset this restores the built-in default).";
                _lblPromptDefault.ForeColor = isDefault ? UiTheme.FgDim : UiTheme.Warn;
                _promptDirty = false;
            }
            finally
            {
                _promptLoading = false;
            }
        }

        private void CommitPromptIfDirty()
        {
            if (!_promptDirty || _loading || _promptLoading)
                return;
            SaveCurrentPrompt(quiet: true);
        }

        private void SaveCurrentPrompt(bool quiet = false)
        {
            string key = CurrentPromptKey();
            string body = _txtPrompt.Text ?? "";
            AppSettings.Current.SetPromptByKey(key, body);
            try
            {
                AppSettings.Current.PersistSpeechRules();
                _promptDirty = false;
                _dirty = false;
            }
            catch (Exception ex)
            {
                SetStatus($"Prompt save failed: {ex.Message}", bad: true);
                return;
            }
            LoadPromptIntoEditor();
            if (!quiet)
                SetStatus($"Prompt “{key}” saved (profile-aware).");
        }

        private void ResetCurrentPrompt()
        {
            string key = CurrentPromptKey();
            AppSettings.Current.SetPromptByKey(key, "");
            try
            {
                AppSettings.Current.PersistSpeechRules();
                _promptDirty = false;
            }
            catch (Exception ex)
            {
                SetStatus($"Reset failed: {ex.Message}", bad: true);
                return;
            }
            LoadPromptIntoEditor();
            SetStatus($"Prompt “{key}” reset to built-in default.");
        }

        private void ResetAllPrompts()
        {
            var dr = UiMessageBox.Show(GetModalOwner(),
                "Reset ALL OCR prompts to SpeakRect built-in defaults?",
                "Reset prompts",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes)
                return;
            AppSettings.Current.ResetPromptsToDefaults();
            try
            {
                AppSettings.Current.PersistSpeechRules();
                _promptDirty = false;
            }
            catch (Exception ex)
            {
                SetStatus($"Reset failed: {ex.Message}", bad: true);
                return;
            }
            LoadPromptIntoEditor();
            SetStatus("All prompts reset to built-in defaults.");
        }

        // =====================================================================
        // Preview / speak
        // =====================================================================

        private void RunPreview(bool speak)
        {
            CommitPromptIfDirty();
            AppSettings.Current.SetSpeechRules(CollectNameRules());
            SyncTextRulesFromListViewTags();
            AppSettings.Current.SetSpeechTextRules(_textRules);

            string input = _txtTestIn.Text ?? "";
            if (string.IsNullOrWhiteSpace(input))
            {
                _txtTestOut.Text = "";
                _lastPreviewSpeak = "";
                SetStatus("Paste sample text to preview.", bad: true);
                return;
            }

            try
            {
                bool comic = AppSettings.Current.ComicBook;
                string cleaned = OcrProcessor.SmokeCleanForSpeech(input, comicBook: comic);
                var units = OcrProcessor.SmokeSpeakUnits(cleaned);
                bool usable = OcrProcessor.SmokeIsUsableOcrText(cleaned);

                _lastPreviewSpeak = string.Join(". ", units.Where(u => !string.IsNullOrWhiteSpace(u)));

                var lines = new List<string>
                {
                    $"usable: {(usable ? "yes" : "no")}  ·  comic: {comic}  ·  units: {units.Count}  ·  voice: {OcrProcessor.DescribeCurrentVoice()}",
                    "",
                };
                if (units.Count == 0)
                    lines.Add("(nothing left to speak)");
                else
                {
                    for (int i = 0; i < units.Count; i++)
                        lines.Add($"[{i + 1}] {units[i]}");
                }
                _txtTestOut.Text = string.Join(Environment.NewLine, lines);

                if (speak)
                {
                    if (!usable || string.IsNullOrWhiteSpace(_lastPreviewSpeak))
                    {
                        SetStatus("Nothing to speak after clean + rules.", bad: true);
                        return;
                    }
                    OcrProcessor.SpeakAnnouncement(_lastPreviewSpeak);
                    SetStatus($"Speaking with {OcrProcessor.DescribeCurrentVoice()}…");
                }
                else
                {
                    SetStatus(usable
                        ? $"Preview: {units.Count} speak unit(s). Click Speak to hear your voice."
                        : "Preview: cleaned text would be treated as unusable / empty.");
                }
            }
            catch (Exception ex)
            {
                _txtTestOut.Text = "";
                _lastPreviewSpeak = "";
                SetStatus($"Preview failed: {ex.Message}", bad: true);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void SizeListColumns(ListView list)
        {
            if (list.Columns.Count < 2)
                return;
            // Leave the last column for UiTheme.FitListViewLastColumn so the native
            // header never shows a white dead zone past "Replace" / "How".
            int w = Math.Max(200, list.ClientSize.Width - 4);
            if (list.Columns.Count == 4)
            {
                // On | Find | Say as | How(fill)
                int on = 40;
                int mid = Math.Max(120, w - on - 72);
                list.Columns[0].Width = on;
                list.Columns[1].Width = mid / 2;
                list.Columns[2].Width = mid - mid / 2;
                list.Columns[3].Width = 72; // stretched to fill by FitListViewLastColumn
            }
            else if (list.Columns.Count >= 5)
            {
                // On | Name | Stage | Pattern | Replace(fill)
                int on = 36, stage = 90;
                int mid = Math.Max(160, w - on - stage - 80);
                list.Columns[0].Width = on;
                list.Columns[1].Width = Math.Max(80, mid / 3);
                list.Columns[2].Width = stage;
                list.Columns[3].Width = Math.Max(80, mid - list.Columns[1].Width);
                list.Columns[4].Width = 80; // stretched to fill
            }
            UiTheme.FitListViewLastColumn(list);
        }

        private void SetStatus(string text, bool bad = false)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = bad ? UiTheme.Bad : UiTheme.Ok;
        }

        private IWin32Window GetModalOwner()
        {
            Control? c = this;
            Form? topLevel = null;
            while (c != null)
            {
                if (c is Form f && f.TopLevel)
                    topLevel = f;
                c = c.Parent;
            }
            return topLevel ?? FindForm() ?? this;
        }

        private static ListView MakeListView()
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 9f),
                UseCompatibleStateImageBehavior = false,
            };
            UiTheme.StyleListView(list);
            return list;
        }

        private static FlowLayoutPanel MakeSidePanel() => new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = UiTheme.Bg,
            AutoScroll = true,
        };

        private static Button MakeSideButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                Width = 96,
                Height = 28,
                Margin = new Padding(0, 0, 0, 6),
                Font = new Font("Segoe UI", 8.5f),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s ?? "";
            return s[..(max - 1)] + "…";
        }

        // =====================================================================
        // Name rule editor dialog
        // =====================================================================

        private sealed class EditNameDialog : Form
        {
            private readonly TextBox _txtMatch;
            private readonly TextBox _txtReplace;
            private readonly ComboBox _cmbKind;
            private readonly CheckBox _chkEnabled;

            private EditNameDialog(SpeechRule? existing)
            {
                Text = existing == null ? "Add name rule" : "Edit name rule";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                TopMost = true;
                ClientSize = new Size(420, 260);
                UiTheme.ApplyForm(this);
                Font = new Font("Segoe UI", 9f);
                KeyPreview = true;

                var body = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 6,
                    Padding = new Padding(14, 12, 14, 10),
                };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                for (int i = 0; i < 4; i++)
                    body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
                body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

                _txtMatch = FieldBox();
                _txtReplace = FieldBox();
                _cmbKind = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                UiTheme.StyleCombo(_cmbKind);
                _cmbKind.Items.Add("Word (exact word only)");
                _cmbKind.Items.Add("Anywhere (names, multi-word)");
                _cmbKind.SelectedIndex = 0;
                _chkEnabled = new CheckBox
                {
                    Text = "Enabled",
                    Checked = true,
                    ForeColor = UiTheme.Fg,
                    AutoSize = true,
                    Dock = DockStyle.Left,
                };
                var lblHint = new Label
                {
                    Dock = DockStyle.Fill,
                    ForeColor = UiTheme.FgMuted,
                    Font = new Font("Segoe UI", 7.5f),
                    Text = "Type names the way you see them on screen.\n" +
                           "Example: Find  X-Men    Say as  Ex-Men\n" +
                           "Leave “Say as” blank to skip that text (never speak it).",
                };

                body.Controls.Add(MakeLbl("Find"), 0, 0);
                body.Controls.Add(_txtMatch, 1, 0);
                body.Controls.Add(MakeLbl("Say as"), 0, 1);
                body.Controls.Add(_txtReplace, 1, 1);
                body.Controls.Add(MakeLbl("How"), 0, 2);
                body.Controls.Add(_cmbKind, 1, 2);
                body.Controls.Add(new Label(), 0, 3);
                body.Controls.Add(_chkEnabled, 1, 3);
                body.SetColumnSpan(lblHint, 2);
                body.Controls.Add(lblHint, 0, 4);

                var buttons = MakeOkCancel(out var btnOk, out var btnCancel);
                body.SetColumnSpan(buttons, 2);
                body.Controls.Add(buttons, 0, 5);
                Controls.Add(body);
                AcceptButton = btnOk;
                CancelButton = btnCancel;

                if (existing != null)
                {
                    _txtMatch.Text = existing.Match;
                    _txtReplace.Text = existing.Replace;
                    _cmbKind.SelectedIndex = existing.Kind == SpeechMatchKind.Phrase ? 1 : 0;
                    _chkEnabled.Checked = existing.Enabled;
                }

                btnOk.Click += (_, _) =>
                {
                    if (!TryBuild(out _, out string? err))
                    {
                        UiMessageBox.Show(this, err ?? "Invalid rule.", Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                    Close();
                };

                KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Escape)
                    {
                        e.Handled = true;
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }
                };

                Shown += (_, _) =>
                {
                    BringToFront();
                    Activate();
                    _txtMatch.Focus();
                };
            }

            private bool TryBuild(out SpeechRule rule, out string? error)
            {
                var kind = _cmbKind.SelectedIndex == 1
                    ? SpeechMatchKind.Phrase
                    : SpeechMatchKind.Word;
                return SpeechRule.TryNormalize(
                    _txtMatch.Text, _txtReplace.Text, kind, _chkEnabled.Checked,
                    out rule, out error);
            }

            public static bool Show(IWin32Window? owner, SpeechRule? existing, out SpeechRule? rule)
            {
                rule = null;
                using var dlg = new EditNameDialog(existing);
                if (!ShowModal(owner, dlg))
                    return false;
                if (!dlg.TryBuild(out SpeechRule built, out _))
                    return false;
                rule = built;
                return true;
            }
        }

        // =====================================================================
        // Text rule editor dialog
        // =====================================================================

        private sealed class EditTextRuleDialog : Form
        {
            private readonly TextBox _txtName;
            private readonly ComboBox _cmbStage;
            private readonly TextBox _txtPattern;
            private readonly TextBox _txtReplace;
            private readonly CheckBox _chkEnabled;
            private readonly CheckBox _chkIgnoreCase;
            private readonly string _id;
            private readonly bool _isBuiltIn;

            private EditTextRuleDialog(SpeechTextRule? existing)
            {
                Text = existing == null ? "Add text rule" : "Edit text rule";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                TopMost = true;
                ClientSize = new Size(520, 360);
                UiTheme.ApplyForm(this);
                Font = new Font("Segoe UI", 9f);
                KeyPreview = true;

                _id = existing?.Id is { Length: > 0 } eid
                    ? eid
                    : SpeechTextRule.NewCustomId();
                _isBuiltIn = existing?.IsBuiltIn == true;

                var body = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 8,
                    Padding = new Padding(14, 12, 14, 10),
                };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
                body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

                _txtName = FieldBox();
                _cmbStage = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                UiTheme.StyleCombo(_cmbStage);
                _cmbStage.Items.Add("Noise strip (before lowercasing)");
                _cmbStage.Items.Add("Abbreviations (after lowercasing)");
                _cmbStage.Items.Add("Decorators (comic dashes / arrows)");
                _cmbStage.SelectedIndex = 1;
                _txtPattern = FieldBox();
                _txtPattern.Multiline = true;
                _txtPattern.Height = 48;
                _txtPattern.ScrollBars = ScrollBars.Vertical;
                _txtPattern.Font = new Font("Consolas", 9f);
                _txtReplace = FieldBox();
                _txtReplace.Multiline = true;
                _txtReplace.Height = 48;
                _txtReplace.ScrollBars = ScrollBars.Vertical;
                _txtReplace.Font = new Font("Consolas", 9f);
                _chkEnabled = new CheckBox
                {
                    Text = "Enabled",
                    Checked = true,
                    ForeColor = UiTheme.Fg,
                    AutoSize = true,
                    Dock = DockStyle.Left,
                };
                _chkIgnoreCase = new CheckBox
                {
                    Text = "Ignore case",
                    Checked = false,
                    ForeColor = UiTheme.Fg,
                    AutoSize = true,
                    Dock = DockStyle.Left,
                };
                var lblHint = new Label
                {
                    Dock = DockStyle.Fill,
                    ForeColor = UiTheme.FgMuted,
                    Font = new Font("Segoe UI", 7.5f),
                    Text = "Pattern is a .NET regular expression. Use $1 for capture groups.\n" +
                           "Empty Replace strips the match. Test with Preview on the Speech tab.\n" +
                           "Invalid or very expensive patterns are rejected.",
                };

                body.Controls.Add(MakeLbl("Name"), 0, 0);
                body.Controls.Add(_txtName, 1, 0);
                body.Controls.Add(MakeLbl("Stage"), 0, 1);
                body.Controls.Add(_cmbStage, 1, 1);
                body.Controls.Add(MakeLbl("Pattern"), 0, 2);
                body.Controls.Add(_txtPattern, 1, 2);
                body.Controls.Add(MakeLbl("Replace"), 0, 3);
                body.Controls.Add(_txtReplace, 1, 3);
                body.Controls.Add(new Label(), 0, 4);
                body.Controls.Add(_chkEnabled, 1, 4);
                body.Controls.Add(new Label(), 0, 5);
                body.Controls.Add(_chkIgnoreCase, 1, 5);
                body.SetColumnSpan(lblHint, 2);
                body.Controls.Add(lblHint, 0, 6);

                var buttons = MakeOkCancel(out var btnOk, out var btnCancel);
                body.SetColumnSpan(buttons, 2);
                body.Controls.Add(buttons, 0, 7);
                Controls.Add(body);
                AcceptButton = btnOk;
                CancelButton = btnCancel;

                if (existing != null)
                {
                    _txtName.Text = existing.Name;
                    _cmbStage.SelectedIndex = existing.Stage switch
                    {
                        SpeechTextRuleStage.Noise => 0,
                        SpeechTextRuleStage.Decorators => 2,
                        _ => 1,
                    };
                    _txtPattern.Text = existing.Pattern;
                    _txtReplace.Text = existing.Replace;
                    _chkEnabled.Checked = existing.Enabled;
                    _chkIgnoreCase.Checked = existing.IgnoreCase;
                }

                btnOk.Click += (_, _) =>
                {
                    if (!TryBuild(out _, out string? err))
                    {
                        UiMessageBox.Show(this, err ?? "Invalid rule.", Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                    Close();
                };

                KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Escape)
                    {
                        e.Handled = true;
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }
                };

                Shown += (_, _) =>
                {
                    BringToFront();
                    Activate();
                    _txtName.Focus();
                };
            }

            private bool TryBuild(out SpeechTextRule rule, out string? error)
            {
                var stage = _cmbStage.SelectedIndex switch
                {
                    0 => SpeechTextRuleStage.Noise,
                    2 => SpeechTextRuleStage.Decorators,
                    _ => SpeechTextRuleStage.Abbrev,
                };
                return SpeechTextRule.TryNormalize(
                    _id, _txtName.Text, stage, _txtPattern.Text, _txtReplace.Text,
                    _chkEnabled.Checked, _chkIgnoreCase.Checked, _isBuiltIn,
                    out rule, out error);
            }

            public static bool Show(
                IWin32Window? owner, SpeechTextRule? existing, out SpeechTextRule? rule)
            {
                rule = null;
                using var dlg = new EditTextRuleDialog(existing);
                if (!ShowModal(owner, dlg))
                    return false;
                if (!dlg.TryBuild(out SpeechTextRule built, out _))
                    return false;
                rule = built;
                return true;
            }
        }

        // ---- shared dialog helpers ----

        private static bool ShowModal(IWin32Window? owner, Form dlg)
        {
            IWin32Window? modalOwner = owner;
            if (owner is Control oc)
            {
                Control? c = oc;
                Form? top = null;
                while (c != null)
                {
                    if (c is Form f && f.TopLevel)
                        top = f;
                    c = c.Parent;
                }
                modalOwner = top ?? (oc is Form { TopLevel: true } tf ? tf : null);
            }
            DialogResult dr = modalOwner != null
                ? dlg.ShowDialog(modalOwner)
                : dlg.ShowDialog();
            return dr == DialogResult.OK;
        }

        private static FlowLayoutPanel MakeOkCancel(out Button btnOk, out Button btnCancel)
        {
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = UiTheme.Bg,
            };
            btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.None,
                Width = 88,
                Height = 28,
                Margin = new Padding(6, 4, 0, 0),
            };
            UiTheme.StylePrimaryButton(btnOk);
            btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 88,
                Height = 28,
                Margin = new Padding(6, 4, 0, 0),
            };
            UiTheme.StyleButton(btnCancel);
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            return buttons;
        }

        private static Label MakeLbl(string t) => new()
        {
            Text = t,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        private static TextBox FieldBox()
        {
            var t = new TextBox { Dock = DockStyle.Fill };
            UiTheme.StyleTextBox(t);
            return t;
        }
    }
}
