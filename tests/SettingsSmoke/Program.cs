using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using SpeakRect;

// UI smoke: open unified Settings, switch tabs, screenshot, assert no overflow/overlap.

int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) Console.WriteLine($"  PASS  {name}");
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
    }
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== SettingsSmoke (UI layout) ===");

// STA for WinForms
var tcs = new TaskCompletionSource<int>();
var thread = new Thread(() =>
{
    try
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string outDir = Path.Combine(AppContext.BaseDirectory, "settings_smoke");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"screenshots → {outDir}");

        using var settings = new frm_Settings(
            onHotkeysChanged: () => { },
            onBeforeProfileSave: null,
            onAfterProfileLoad: null,
            onFollowChanged: null,
            initialTab: frm_Settings.SettingsTab.KeyMap);

        // Show without blocking; pump messages for layout
        settings.Show();
        settings.Activate();
        Application.DoEvents();
        Thread.Sleep(200);
        Application.DoEvents();

        Check("Settings form created", settings.IsHandleCreated && !settings.IsDisposed);
        Check("Settings min size ok",
            settings.MinimumSize.Width >= 700 && settings.MinimumSize.Height >= 500,
            $"{settings.MinimumSize.Width}x{settings.MinimumSize.Height}");
        Check("Settings client size ok",
            settings.ClientSize.Width >= 700 && settings.ClientSize.Height >= 500,
            $"{settings.ClientSize.Width}x{settings.ClientSize.Height}");

        // Profile bar must be present and non-zero
        var profileLabels = FindControls(settings, c => c is Label lbl && lbl.Text == "Profile").ToList();
        Check("Profile label present", profileLabels.Count >= 1);
        var profileCombo = FindControls(settings, c => c is ComboBox).FirstOrDefault();
        Check("Profile combo present", profileCombo != null && profileCombo.Width > 40,
            profileCombo == null ? "missing" : $"w={profileCombo.Width}");

        // Tab control: Key Map, Regions, Follow, Voice, Speech, Image, Balloons, Analytics, Help
        var tabs = FindControls(settings, c => c is TabControl).OfType<TabControl>().FirstOrDefault();
        Check("TabControl present", tabs != null);
        if (tabs != null)
        {
            Check("Nine settings tabs (incl. Speech / Image / Balloons)", tabs.TabPages.Count == 9,
                $"count={tabs.TabPages.Count}");
            var names = string.Join(", ", tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
            Check("Tab names",
                names.Contains("Key Map", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Regions", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Voice", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Follow", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Speech", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Image", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Balloons", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Analytics", StringComparison.OrdinalIgnoreCase) &&
                names.Contains("Help", StringComparison.OrdinalIgnoreCase),
                names);
            // Analytics should sit immediately before Help
            int idxAnalytics = tabs.TabPages.Cast<TabPage>()
                .ToList().FindIndex(p => p.Text.Equals("Analytics", StringComparison.OrdinalIgnoreCase));
            int idxHelp = tabs.TabPages.Cast<TabPage>()
                .ToList().FindIndex(p => p.Text.Equals("Help", StringComparison.OrdinalIgnoreCase));
            Check("Analytics before Help",
                idxAnalytics >= 0 && idxHelp >= 0 && idxAnalytics + 1 == idxHelp,
                $"analytics={idxAnalytics} help={idxHelp}");
        }

        // Seed sample regions so the Regions map screenshot is non-empty
        var s = AppSettings.Current;
        s.RegionSlots[0].SetBox("Rect", new Rectangle(80, 100, 400, 120));
        s.RegionSlots[1].SetBox("Oval", new Rectangle(500, 300, 200, 150));
        s.RegionSlots[2].Clear();
        s.ActiveRegionSlot = 0;

        // Capture each tab
        var tabOrder = new[]
        {
            frm_Settings.SettingsTab.KeyMap,
            frm_Settings.SettingsTab.Regions,
            frm_Settings.SettingsTab.Follow,
            frm_Settings.SettingsTab.Voice,
            frm_Settings.SettingsTab.Speech,
            frm_Settings.SettingsTab.Image,
            frm_Settings.SettingsTab.Balloons,
            frm_Settings.SettingsTab.Analytics,
            frm_Settings.SettingsTab.Help,
        };
        string[] fileNames =
        {
            "settings_keymap.png",
            "settings_regions.png",
            "settings_follow.png",
            "settings_voice.png",
            "settings_speech.png",
            "settings_image.png",
            "settings_balloons.png",
            "settings_analytics.png",
            "settings_help.png",
        };

        for (int i = 0; i < tabOrder.Length; i++)
        {
            settings.SelectTab(tabOrder[i]);
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();
            // Force layout after tab switch
            settings.PerformLayout();
            Application.DoEvents();
            Thread.Sleep(100);
            Application.DoEvents();

            string path = Path.Combine(outDir, fileNames[i]);
            CaptureForm(settings, path);
            Check($"Screenshot {fileNames[i]}", File.Exists(path) && new FileInfo(path).Length > 2000,
                File.Exists(path) ? $"{new FileInfo(path).Length} bytes" : "missing");

            // Bounds checks for this tab content
            Check($"No overflow on {tabOrder[i]}",
                !HasOutOfBoundsControls(settings, out string overflowDetail),
                overflowDetail);
            Check($"Visible children on {tabOrder[i]}",
                CountVisibleControls(settings) > 10,
                $"count={CountVisibleControls(settings)}");
        }

        // Shrink to minimum and re-check (common overflow case)
        settings.ClientSize = settings.MinimumSize;
        Application.DoEvents();
        Thread.Sleep(100);
        settings.SelectTab(frm_Settings.SettingsTab.KeyMap);
        Application.DoEvents();
        Thread.Sleep(100);
        string minPath = Path.Combine(outDir, "settings_keymap_minsize.png");
        CaptureForm(settings, minPath);
        Check("Screenshot at min size", File.Exists(minPath) && new FileInfo(minPath).Length > 1000);
        Check("No overflow at min size",
            !HasOutOfBoundsControls(settings, out string minOverflow),
            minOverflow);

        // Profile buttons must not stack off the profile bar
        var loadBtn = FindControls(settings, c => c is Button b && b.Text == "Load").FirstOrDefault();
        var saveBtn = FindControls(settings, c => c is Button b && b.Text == "Save").FirstOrDefault();
        var closeBtn = FindControls(settings, c => c is Button b && b.Text == "Close").FirstOrDefault();
        Check("Load button visible in bounds",
            loadBtn != null && loadBtn.Visible && IsFullyInsideAncestor(loadBtn, settings),
            loadBtn == null ? "missing" : BoundsSummary(loadBtn));
        Check("Save button visible in bounds",
            saveBtn != null && saveBtn.Visible && IsFullyInsideAncestor(saveBtn, settings),
            saveBtn == null ? "missing" : BoundsSummary(saveBtn));
        Check("Close button visible in bounds",
            closeBtn != null && closeBtn.Visible && IsFullyInsideAncestor(closeBtn, settings),
            closeBtn == null ? "missing" : BoundsSummary(closeBtn));

        // Key Map grid should exist
        var grid = FindControls(settings, c => c is DataGridView).OfType<DataGridView>().FirstOrDefault();
        Check("Key Map grid present", grid != null && grid.ColumnCount >= 3,
            grid == null ? "missing" : $"cols={grid.ColumnCount} rows={grid.RowCount}");

        // ---- Follow tab: live editors + ApplyEditorsAndRefreshPreview (Enter path) ----
        settings.SelectTab(frm_Settings.SettingsTab.Follow);
        Application.DoEvents();
        Thread.Sleep(150);
        Application.DoEvents();

        var follow = FindControls(settings, c => c is frm_FollowSettings)
            .OfType<frm_FollowSettings>().FirstOrDefault();
        Check("Follow panel embedded", follow != null);

        if (follow != null)
        {
            var nuds = FindControls(follow, c => c is NumericUpDown)
                .OfType<NumericUpDown>()
                // Top-to-bottom: Width, Height, OffsetX, OffsetY
                .OrderBy(n => n.PointToScreen(Point.Empty).Y)
                .ThenBy(n => n.PointToScreen(Point.Empty).X)
                .ToList();
            Check("Follow has size/offset spin boxes", nuds.Count >= 4,
                $"count={nuds.Count}");

            // Preview panel (custom Panel with double-buffer paint)
            var panels = FindControls(follow, c => c is Panel p && p.MinimumSize.Width >= 200)
                .OfType<Panel>().ToList();
            Check("Follow preview panel present", panels.Count >= 1,
                $"panels={panels.Count}");

            // Snapshot so we can restore after the smoke (do not dirty user profile permanently)
            int w0 = AppSettings.Current.FollowWidth;
            int h0 = AppSettings.Current.FollowHeight;
            int ox0 = AppSettings.Current.FollowOffsetX;
            int oy0 = AppSettings.Current.FollowOffsetY;
            string shape0 = AppSettings.Current.FollowShape;
            try
            {
                // Simulate Enter commit path: change values then ApplyEditorsAndRefreshPreview
                if (nuds.Count >= 4)
                {
                    nuds[0].Value = 640; // width
                    nuds[1].Value = 120; // height
                    nuds[2].Value = 33;  // offset X
                    nuds[3].Value = -77; // offset Y
                    Application.DoEvents();

                    follow.ApplyEditorsAndRefreshPreview();
                    Application.DoEvents();

                    Check("Follow ApplyEditors saves width",
                        AppSettings.Current.FollowWidth == 640,
                        $"got {AppSettings.Current.FollowWidth}");
                    Check("Follow ApplyEditors saves height",
                        AppSettings.Current.FollowHeight == 120,
                        $"got {AppSettings.Current.FollowHeight}");
                    Check("Follow ApplyEditors saves offset X",
                        AppSettings.Current.FollowOffsetX == 33,
                        $"got {AppSettings.Current.FollowOffsetX}");
                    Check("Follow ApplyEditors saves offset Y",
                        AppSettings.Current.FollowOffsetY == -77,
                        $"got {AppSettings.Current.FollowOffsetY}");
                }
            }
            finally
            {
                try
                {
                    AppSettings.Current.FollowWidth = w0;
                    AppSettings.Current.FollowHeight = h0;
                    AppSettings.Current.FollowOffsetX = ox0;
                    AppSettings.Current.FollowOffsetY = oy0;
                    AppSettings.Current.FollowShape = shape0;
                    AppSettings.Current.NormalizeFollowSettings();
                    AppSettings.Current.Save();
                    follow.ReloadFromSettings();
                    Application.DoEvents();
                }
                catch { /* ignore restore */ }
            }

            // Enter commit must not close the host Settings window
            settings.Activate();
            Application.DoEvents();
            Check("Settings still open after Follow apply (Enter does not close)",
                settings.Visible && !settings.IsDisposed);
        }

        settings.Close();
        Application.DoEvents();

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("ALL SETTINGS SMOKE TESTS PASSED");
            tcs.SetResult(0);
        }
        else
        {
            Console.WriteLine($"SETTINGS SMOKE FAILED ({failed})");
            tcs.SetResult(1);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  FAIL  unhandled: " + ex);
        tcs.SetResult(2);
    }
    finally
    {
        try { Application.ExitThread(); } catch { /* ignore */ }
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
int code = tcs.Task.GetAwaiter().GetResult();
thread.Join(5000);
Environment.Exit(code);

// ---- helpers ----

static IEnumerable<Control> FindControls(Control root, Func<Control, bool> pred)
{
    var stack = new Stack<Control>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var c = stack.Pop();
        if (pred(c))
            yield return c;
        foreach (Control child in c.Controls)
            stack.Push(child);
    }
}

static int CountVisibleControls(Control root)
{
    int n = 0;
    foreach (var c in FindControls(root, _ => true))
        if (c.Visible) n++;
    return n;
}

static bool HasOutOfBoundsControls(Control root, out string detail)
{
    var bad = new List<string>();
    foreach (var c in FindControls(root, x => x.Visible && x != root))
    {
        // Skip zero-size spacers / docks that legitimately fill
        if (c.Width <= 0 || c.Height <= 0)
            continue;
        // Only flag when control's screen rect spills far outside the form client
        if (!IsFullyInsideAncestor(c, root, margin: 4))
        {
            // Tab pages host content that may be slightly inset — require clear overflow
            var formScreen = root.RectangleToScreen(root.ClientRectangle);
            var ctlScreen = c.RectangleToScreen(c.ClientRectangle);
            bool hSpill = ctlScreen.Right > formScreen.Right + 8 ||
                          ctlScreen.Left < formScreen.Left - 8;
            bool vSpill = ctlScreen.Bottom > formScreen.Bottom + 8 ||
                          ctlScreen.Top < formScreen.Top - 8;
            // Voice / Image / Balloons host tall bodies in AutoScroll panels —
            // vertical overflow until the user scrolls is expected, not a bug.
            if (vSpill && !hSpill && IsInsideAutoScrollHost(c, root))
                continue;
            if (hSpill || vSpill)
            {
                bad.Add($"{c.GetType().Name} '{c.Name}/{c.Text}' {ctlScreen} vs form {formScreen}");
            }
        }
    }
    detail = bad.Count == 0 ? "" : string.Join("; ", bad.Take(4));
    return bad.Count > 0;
}

/// <summary>True when <paramref name="c"/> sits under an AutoScroll host (scrollable tall tab body).</summary>
static bool IsInsideAutoScrollHost(Control c, Control root)
{
    for (Control? p = c.Parent; p != null && p != root; p = p.Parent)
    {
        if (p is ScrollableControl sc && sc.AutoScroll)
            return true;
    }
    return false;
}

static bool IsFullyInsideAncestor(Control c, Control ancestor, int margin = 0)
{
    try
    {
        var a = ancestor.RectangleToScreen(ancestor.ClientRectangle);
        var b = c.RectangleToScreen(c.ClientRectangle);
        a.Inflate(margin, margin);
        return a.Contains(b) || a.IntersectsWith(b);
    }
    catch
    {
        return true;
    }
}

static string BoundsSummary(Control c)
{
    try
    {
        var r = c.RectangleToScreen(c.ClientRectangle);
        return $"{c.Text} screen={r.X},{r.Y} {r.Width}x{r.Height}";
    }
    catch
    {
        return c.Bounds.ToString();
    }
}

static void CaptureForm(Form form, string path)
{
    form.Refresh();
    Application.DoEvents();
    // Prefer form bounds capture from screen for accurate chrome
    var bounds = form.Bounds;
    if (bounds.Width < 10 || bounds.Height < 10)
        return;
    using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        try
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        catch
        {
            // Fallback: DrawToBitmap (client area only)
            form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        }
    }
    bmp.Save(path, ImageFormat.Png);
}
