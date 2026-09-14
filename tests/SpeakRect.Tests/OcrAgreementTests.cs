using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class OcrAgreementTests
{
    [Fact]
    public void Normalize_clamps_need_to_of()
    {
        var (need, of) = OcrAgreement.NormalizePair(3, 2);
        Assert.Equal(2, of);
        Assert.Equal(2, need);
    }

    [Fact]
    public void Normalize_clamps_range()
    {
        var (need, of) = OcrAgreement.NormalizePair(0, 99);
        Assert.Equal(OcrAgreement.MaxOf, of);
        Assert.InRange(need, OcrAgreement.Min, of);
    }

    [Fact]
    public void Display_1_of_1()
    {
        Assert.Equal("1 of 1", OcrAgreement.ToDisplay(1, 1));
        Assert.Equal("2 of 3", OcrAgreement.ToDisplay(2, 3));
    }

    [Fact]
    public void Early_winner_two_matching()
    {
        Assert.True(OcrAgreement.TryTakeWinner(
            new[] { "Hello world", "Hello  world" }, 2, out string win));
        Assert.Contains("Hello", win, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Early_winner_false_until_need()
    {
        Assert.False(OcrAgreement.TryTakeWinner(
            new[] { "Hello world" }, 2, out _));
        Assert.False(OcrAgreement.TryTakeWinner(
            new[] { "Hello world", "Goodbye moon" }, 2, out _));
        Assert.True(OcrAgreement.TryTakeWinner(
            new[] { "Hello world", "Goodbye moon", "hello WORLD" }, 2, out string win));
        Assert.Contains("Hello", win, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_and_unreadable_do_not_count()
    {
        Assert.False(OcrAgreement.TryTakeWinner(
            new[] { "", "   ", "unreadable", "" }, 1, out _));
        Assert.False(OcrAgreement.TryTakeWinner(
            new[] { "", "" }, 2, out _));
    }

    [Fact]
    public async Task Run_1_of_1_calls_once()
    {
        int n = 0;
        string got = await OcrAgreement.RunAsync(
            () =>
            {
                n++;
                return Task.FromResult("Hello");
            },
            1, 1, null, CancellationToken.None);
        Assert.Equal(1, n);
        Assert.Equal("Hello", got);
    }

    [Fact]
    public async Task Run_2_of_3_stops_when_two_match()
    {
        int n = 0;
        string[] seq = { "Alpha line", "Alpha line", "Beta line" };
        string got = await OcrAgreement.RunAsync(
            () => Task.FromResult(seq[n++]),
            2, 3, null, CancellationToken.None);
        Assert.Equal(2, n);
        Assert.Contains("Alpha", got, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_2_of_3_silent_when_no_pair()
    {
        int n = 0;
        string[] seq =
        {
            "short",
            "A much longer readable sentence here",
            "nope",
        };
        string got = await OcrAgreement.RunAsync(
            () => Task.FromResult(seq[n++]),
            2, 3, null, CancellationToken.None);
        Assert.Equal(3, n);
        Assert.Equal("", got);
    }

    [Fact]
    public void Settings_round_trip_agree_pair()
    {
        var s = AppSettings.Current;
        int prevN = s.OcrAgreeNeed;
        int prevO = s.OcrAgreeOf;
        string path = Path.Combine(
            Path.GetTempPath(), "SpeakRect-ocragree-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            s.OcrAgreeNeed = 2;
            s.OcrAgreeOf = 3;
            s.NormalizeOcrAgreeSettings();
            s.SaveTo(path);
            s.OcrAgreeNeed = 1;
            s.OcrAgreeOf = 1;
            s.LoadFrom(path, resetFirst: false);
            Assert.Equal(2, s.OcrAgreeNeed);
            Assert.Equal(3, s.OcrAgreeOf);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
            s.OcrAgreeNeed = prevN;
            s.OcrAgreeOf = prevO;
        }
    }

    [Fact]
    public void CaptureFromApp_freezes_agree_knobs()
    {
        var s = AppSettings.Current;
        int prevN = s.OcrAgreeNeed;
        int prevO = s.OcrAgreeOf;
        try
        {
            s.OcrAgreeNeed = 2;
            s.OcrAgreeOf = 3;
            var snap = SpeakRunSettings.CaptureFromApp();
            Assert.Equal(2, snap.OcrAgreeNeed);
            Assert.Equal(3, snap.OcrAgreeOf);
        }
        finally
        {
            s.OcrAgreeNeed = prevN;
            s.OcrAgreeOf = prevO;
        }
    }
}
