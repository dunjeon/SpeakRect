using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using SpeakRect;

/// <summary>
/// Smoke tests for region slot persistence (Rect / Oval / Lasso on slots 1–8).
/// Exit 0 = all pass; non-zero = failures printed.
/// </summary>
int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
    }
}

Console.WriteLine("=== RegionSlotData unit tests ===");

// --- Rect ---
{
    var slot = new RegionSlotData();
    slot.SetBox("Rect", new Rectangle(10, 20, 300, 150));
    string ini = slot.ToIniString();
    Check("Rect ToIni", ini == "Rect:10,20,300,150", ini);
    var back = RegionSlotData.Parse(ini);
    Check("Rect Parse mode", back.Mode == "Rect", back.Mode);
    Check("Rect Parse geom", back.X == 10 && back.Y == 20 && back.W == 300 && back.H == 150);
    Check("Rect not empty", !back.IsEmpty);
}

// --- Oval ---
{
    var slot = new RegionSlotData();
    slot.SetBox("Oval", new Rectangle(5, 6, 100, 80));
    string ini = slot.ToIniString();
    Check("Oval ToIni", ini == "Oval:5,6,100,80", ini);
    var back = RegionSlotData.Parse(ini);
    Check("Oval Parse mode", back.IsOvalMode, back.Mode);
    Check("Oval Parse geom", back.ToRectangle() == new Rectangle(5, 6, 100, 80));
}

// --- Lasso with pipe separator (canonical) ---
{
    var pts = new List<Point> { new(10, 10), new(50, 20), new(30, 60), new(10, 10) };
    var slot = new RegionSlotData();
    slot.SetLasso(pts);
    string ini = slot.ToIniString();
    Check("Lasso ToIni uses |", ini.StartsWith("Lasso:") && ini.Contains('|') && !ini.Contains(';'), ini);
    Check("Lasso ToIni not empty", !string.IsNullOrEmpty(ini) && !slot.IsEmpty, ini);
    var back = RegionSlotData.Parse(ini);
    Check("Lasso Parse mode", back.IsLassoMode, back.Mode);
    var backPts = back.GetLassoPoints();
    Check("Lasso Parse count", backPts.Count == pts.Count, $"got {backPts.Count}");
    Check("Lasso Parse pts", backPts.SequenceEqual(pts), string.Join("|", backPts));
}

// --- Lasso legacy semicolon body still parses when value is intact ---
{
    var legacy = "Lasso:10,10;50,20;30,60";
    var back = RegionSlotData.Parse(legacy);
    Check("Lasso legacy ';' parse", back.IsLassoMode && back.GetLassoPoints().Count == 3,
        back.ToIniString());
}

// --- Simulate the old ReadIni comment-stripping bug ---
{
    // This is what ReadIni used to do to Slot2 when IsShortSettingKey matched Slot*
    string full = "Lasso:10,10;50,20;30,60";
    int semi = full.IndexOf(';');
    string stripped = semi >= 0 ? full[..semi].Trim() : full;
    var broken = RegionSlotData.Parse(stripped);
    Check("Stripped ';' body is invalid (documents the bug)", broken.IsEmpty,
        $"stripped='{stripped}' mode={broken.Mode} pts={broken.Points}");
}

Console.WriteLine();
Console.WriteLine("=== Full AppSettings SaveTo / LoadFrom round-trip ===");

string tempDir = Path.Combine(Path.GetTempPath(), "SpeakRectRegionSmoke_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
string iniPath = Path.Combine(tempDir, "test_profile.ini");

try
{
    var s = AppSettings.Current;

    // Snapshot nothing we care about — use LoadFrom(reset) later
    s.RegionSlots[0].SetBox("Rect", new Rectangle(100, 200, 300, 400));
    s.RegionSlots[1].SetLasso(new[]
    {
        new Point(10, 10),
        new Point(200, 15),
        new Point(180, 120),
        new Point(40, 140),
        new Point(10, 10),
    });
    s.RegionSlots[2].SetBox("Oval", new Rectangle(50, 60, 70, 80));
    // leave 3 empty
    s.RegionSlots[3].Clear();
    s.RegionSlots[4].SetLasso(new[]
    {
        new Point(1, 1), new Point(2, 3), new Point(4, 5),
    });
    s.RegionSlots[5].Clear();
    s.RegionSlots[6].Clear();
    s.RegionSlots[7].Clear();

    s.ActiveRegionSlot = 1; // slot 2 (1-based)
    s.ShapeMode = "Lasso";
    s.ComicBook = false;

    s.SaveTo(iniPath);
    string written = File.ReadAllText(iniPath);
    Console.WriteLine("--- written [REGIONS] excerpt ---");
    foreach (string line in written.Split('\n'))
    {
        string t = line.TrimEnd('\r');
        if (t.StartsWith("Slot") || t.StartsWith("ActiveSlot") || t.StartsWith("ShapeMode")
            || t.StartsWith("[REGIONS]") || t.StartsWith("ComicBook"))
            Console.WriteLine("  " + t);
    }

    Check("Written Slot1 is Rect", written.Contains("Slot1=Rect:100,200,300,400"));
    Check("Written Slot2 is Lasso with |",
        written.Contains("Slot2=Lasso:") && written.Contains("10,10|200,15"),
        written.Split('\n').FirstOrDefault(l => l.StartsWith("Slot2="))?.Trim() ?? "(missing)");
    Check("Written Slot2 has no ';'",
        !written.Split('\n').Any(l => l.StartsWith("Slot2=") && l.Contains(';')));
    Check("Written Slot3 is Oval", written.Contains("Slot3=Oval:50,60,70,80"));
    Check("Written Slot5 is Lasso", written.Contains("Slot5=Lasso:"));
    Check("Written ActiveSlot=2", written.Contains("ActiveSlot=2"));

    // Wipe memory and reload
    s.LoadFrom(iniPath, resetFirst: true);

    Check("Loaded Slot1 Rect", !s.RegionSlots[0].IsEmpty && s.RegionSlots[0].Mode == "Rect"
        && s.RegionSlots[0].W == 300);
    Check("Loaded Slot2 Lasso", s.RegionSlots[1].IsLassoMode && !s.RegionSlots[1].IsEmpty,
        $"mode={s.RegionSlots[1].Mode} pts={s.RegionSlots[1].Points} empty={s.RegionSlots[1].IsEmpty}");
    Check("Loaded Slot2 point count", s.RegionSlots[1].GetLassoPoints().Count == 5,
        $"count={s.RegionSlots[1].GetLassoPoints().Count}");
    Check("Loaded Slot3 Oval", s.RegionSlots[2].IsOvalMode && s.RegionSlots[2].H == 80);
    Check("Loaded Slot4 empty", s.RegionSlots[3].IsEmpty);
    Check("Loaded Slot5 Lasso 3pts", s.RegionSlots[4].IsLassoMode && s.RegionSlots[4].GetLassoPoints().Count == 3);
    Check("Loaded ActiveRegionSlot", s.ActiveRegionSlot == 1, s.ActiveRegionSlot.ToString());
    Check("Loaded ShapeMode Lasso", s.ShapeMode == "Lasso", s.ShapeMode);

    // Second round-trip (load → save → load again) must not lose Slot2
    string ini2 = Path.Combine(tempDir, "test_profile2.ini");
    s.SaveTo(ini2);
    s.LoadFrom(ini2, resetFirst: true);
    Check("Second round-trip Slot2 Lasso",
        s.RegionSlots[1].IsLassoMode && s.RegionSlots[1].GetLassoPoints().Count == 5,
        s.RegionSlots[1].ToIniString());

    // Profile save/load path (regions + follow size/shape)
    string profileName = "SmokeLasso_" + Guid.NewGuid().ToString("N")[..8];
    // Ensure Profiles dir exists under the assembly location of SpeakRect
    // SaveProfile writes to AppSettings.ProfilesDir next to SpeakRect.dll
    s.RegionSlots[1].SetLasso(new[]
    {
        new Point(9, 9), new Point(19, 19), new Point(29, 9),
    });
    s.ActiveRegionSlot = 1;
    s.FollowWidth = 777;
    s.FollowHeight = 222;
    s.FollowShape = "Ellipse";
    s.FollowOffsetX = 33;
    s.FollowOffsetY = -44;
    // Mode state must round-trip through named profiles (ComicBook on/off).
    s.ComicBook = true;
    if (!s.SaveProfile(profileName, out string? saveErr))
    {
        Check("SaveProfile", false, saveErr ?? "unknown");
    }
    else
    {
        Check("SaveProfile", true);
        // Clear and reload profile
        s.RegionSlots[1].Clear();
        s.FollowWidth = AppSettings.DefaultFollowWidth;
        s.FollowHeight = AppSettings.DefaultFollowHeight;
        s.FollowShape = "Rectangle";
        s.FollowOffsetX = 0;
        s.FollowOffsetY = 0;
        s.ComicBook = false;
        if (!s.LoadProfile(profileName, out string? loadErr))
        {
            Check("LoadProfile", false, loadErr ?? "unknown");
        }
        else
        {
            Check("LoadProfile Slot2 Lasso",
                s.RegionSlots[1].IsLassoMode && s.RegionSlots[1].GetLassoPoints().Count == 3,
                s.RegionSlots[1].ToIniString());
            Check("LoadProfile Follow size",
                s.FollowWidth == 777 && s.FollowHeight == 222,
                $"{s.FollowWidth}x{s.FollowHeight}");
            Check("LoadProfile Follow shape/offset",
                s.FollowIsEllipse && s.FollowOffsetX == 33 && s.FollowOffsetY == -44,
                $"{s.FollowShape} ({s.FollowOffsetX},{s.FollowOffsetY})");
            Check("LoadProfile ComicBook mode",
                s.ComicBook,
                $"Comic={s.ComicBook}");
        }
        // cleanup profile file
        try { File.Delete(AppSettings.GetProfilePath(profileName)); } catch { /* ignore */ }
    }

    // SetFlag / SyncActiveProfileFile keeps the named profile MODE block updated.
    string modeProfile = "SmokeMode_" + Guid.NewGuid().ToString("N")[..8];
    s.ComicBook = false;
    if (s.SaveProfile(modeProfile, out _))
    {
        s.ActiveProfileName = modeProfile;
        s.SetFlag(AppSettings.FlagIndexComicBook, true);
        Check("SetFlag ComicBook live",
            s.ComicBook,
            $"Comic={s.ComicBook}");

        s.ComicBook = false;
        if (s.LoadProfile(modeProfile, out _))
        {
            Check("Active profile auto-saved ComicBook mode",
                s.ComicBook,
                $"Comic={s.ComicBook}");
        }
        else
        {
            Check("Active profile auto-saved ComicBook mode", false, "reload failed");
        }

        // Also verify DEFAULT flag clears ComicBook
        s.SetFlag(AppSettings.FlagIndexDefault, true);
        Check("SetFlag Default clears ComicBook",
            !s.ComicBook,
            $"Comic={s.ComicBook}");

        try { File.Delete(AppSettings.GetProfilePath(modeProfile)); } catch { /* ignore */ }
    }
    else
    {
        Check("Active profile mode sync (setup)", false, "could not create mode profile");
    }

    // Explicit regression: ini line with | must survive ReadIni (via LoadFrom)
    string handPath = Path.Combine(tempDir, "hand.ini");
    File.WriteAllText(handPath, """
        [REGIONS]
        ActiveSlot=2
        ShapeMode=Lasso
        Slot1=
        Slot2=Lasso:11,12|21,22|31,32|11,12
        Slot3=Oval:1,2,3,4
        Slot4=
        Slot5=
        Slot6=
        Slot7=
        Slot8=
        """);
    s.LoadFrom(handPath, resetFirst: true);
    Check("Hand-written Slot2 lasso loads",
        s.RegionSlots[1].IsLassoMode && s.RegionSlots[1].GetLassoPoints().Count == 4,
        s.RegionSlots[1].ToIniString());
    Check("Hand-written Slot3 oval loads",
        s.RegionSlots[2].IsOvalMode && s.RegionSlots[2].ToRectangle() == new Rectangle(1, 2, 3, 4));

    // ----- Custom mouse speed Arg must survive Save / Load / Profile -----
    // Regression: MigrateLegacyMouseSpeedDefault used to force 4/8/10 → 12 on every load,
    // so Key Map speed edits could not stick.
    Console.WriteLine();
    Console.WriteLine("=== Custom mouse speed Arg persistence ===");
    s.ClearCustomHotkeys();
    s.AddCustomHotkey(new CustomHotkeyBinding
    {
        Action = CustomActionKind.MouseMoveUp,
        Arg = "4",
        Label = "",
    });
    s.AddCustomHotkey(new CustomHotkeyBinding
    {
        Action = CustomActionKind.MouseMoveDown,
        Arg = "6.5",
        Label = "",
    });
    s.AddCustomHotkey(new CustomHotkeyBinding
    {
        Action = CustomActionKind.MouseMoveLeft,
        Arg = "14",
        Label = "",
    });
    s.AddCustomHotkey(new CustomHotkeyBinding
    {
        Action = CustomActionKind.MouseMoveRight,
        Arg = "", // blank → default on load
        Label = "",
    });

    string speedIni = Path.Combine(tempDir, "custom_speed.ini");
    s.SaveTo(speedIni);
    string speedText = File.ReadAllText(speedIni);
    Check("Save wrote Arg=4", speedText.Contains("Custom0.Arg=4"));
    Check("Save wrote Arg=6.5", speedText.Contains("Custom0.Arg=6.5") || speedText.Contains("Custom1.Arg=6.5"));
    Check("Save wrote Arg=14", speedText.Contains("Custom2.Arg=14") || speedText.Contains(".Arg=14"));

    s.ClearCustomHotkeys();
    s.LoadFrom(speedIni, resetFirst: true);
    Check("Load count 4 customs", s.CustomHotkeys.Count == 4, s.CustomHotkeys.Count.ToString());
    Check("Load keeps Arg=4 (not forced to 12)",
        s.CustomHotkeys.Count > 0 && s.CustomHotkeys[0].Arg == "4",
        s.CustomHotkeys.Count > 0 ? s.CustomHotkeys[0].Arg : "(none)");
    Check("Load keeps Arg=6.5",
        s.CustomHotkeys.Count > 1 && s.CustomHotkeys[1].Arg is "6.5" or "6.50",
        s.CustomHotkeys.Count > 1 ? s.CustomHotkeys[1].Arg : "(none)");
    Check("Load keeps Arg=14",
        s.CustomHotkeys.Count > 2 && s.CustomHotkeys[2].Arg == "14",
        s.CustomHotkeys.Count > 2 ? s.CustomHotkeys[2].Arg : "(none)");
    Check("Load blank Arg → default 12",
        s.CustomHotkeys.Count > 3 &&
        SystemInput.ParseSpeed(s.CustomHotkeys[3].Arg, -1) == SystemInput.DefaultMouseSpeed,
        s.CustomHotkeys.Count > 3 ? s.CustomHotkeys[3].Arg : "(none)");

    // Profile round-trip
    string speedProfile = "SmokeSpeed_" + Guid.NewGuid().ToString("N")[..8];
    s.CustomHotkeys[0].Arg = "3";
    s.CustomHotkeys[1].Arg = "20";
    if (!s.SaveProfile(speedProfile, out string? spErr))
    {
        Check("SaveProfile custom speeds", false, spErr ?? "fail");
    }
    else
    {
        s.ClearCustomHotkeys();
        if (!s.LoadProfile(speedProfile, out string? lpErr))
        {
            Check("LoadProfile custom speeds", false, lpErr ?? "fail");
        }
        else
        {
            Check("Profile keeps Arg=3",
                s.CustomHotkeys.Count >= 1 && s.CustomHotkeys[0].Arg == "3",
                s.CustomHotkeys.Count >= 1 ? s.CustomHotkeys[0].Arg : "(none)");
            Check("Profile keeps Arg=20",
                s.CustomHotkeys.Count >= 2 && s.CustomHotkeys[1].Arg == "20",
                s.CustomHotkeys.Count >= 2 ? s.CustomHotkeys[1].Arg : "(none)");
        }
        try { File.Delete(AppSettings.GetProfilePath(speedProfile)); } catch { /* ignore */ }
    }

    // Edit-in-place like Key Map: mutate Arg then SaveTo → LoadFrom
    s.ClearCustomHotkeys();
    s.AddCustomHotkey(new CustomHotkeyBinding
    {
        Action = CustomActionKind.MouseMoveUp,
        Arg = SystemInput.FormatSpeed(SystemInput.DefaultMouseSpeed),
    });
    var editLive = s.CustomHotkeys[0];
    editLive.Arg = "7.25";
    s.PersistCustomHotkeys(); // Save + SyncActiveProfile (if any)
    // Direct file round-trip of live SpeakRect-style snapshot
    string editIni = Path.Combine(tempDir, "edit_speed.ini");
    s.SaveTo(editIni);
    s.LoadFrom(editIni, resetFirst: true);
    Check("Edit speed 7.25 survives Persist+Load",
        s.CustomHotkeys.Count == 1 && s.CustomHotkeys[0].Arg == "7.25",
        s.CustomHotkeys.Count == 1 ? s.CustomHotkeys[0].Arg : "(none)");
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine("ALL SMOKE TESTS PASSED");
    return 0;
}

Console.WriteLine($"FAILED: {failed} check(s)");
return 1;
