using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class LocalLlmClientTests
{
    [Fact]
    public void Vision_json_payload_has_wire_keys()
    {
        Assert.True(LocalLlmClient.SmokeVerifyJsonShape(out string json));
        Assert.Contains("\"model\"", json, StringComparison.Ordinal);
        Assert.Contains("\"messages\"", json, StringComparison.Ordinal);
        Assert.Contains("\"image_url\"", json, StringComparison.Ordinal);
        Assert.Contains("\"content\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserContent_includes_image_and_prompt()
    {
        var arr = LocalLlmClient.BuildUserContent("data:image/png;base64,AA==", "read me");
        string s = arr.ToJsonString();
        Assert.Contains("image_url", s, StringComparison.Ordinal);
        Assert.Contains("read me", s, StringComparison.Ordinal);
    }
}
