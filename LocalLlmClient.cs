using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SpeakRect
{
    /// <summary>
    /// Local-LLM vision HTTP client (OpenAI-compatible chat on localhost).
    /// JSON is built with <see cref="JsonObject"/> indexers only — never anonymous
    /// types (stable property names; easy to unit-test).
    /// Host process lifecycle stays in <see cref="LocalLlmHost"/>.
    /// </summary>
    public static class LocalLlmClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");
            return c;
        }

        /// <summary>OpenAI-compatible base URL from the running host config.</summary>
        public static string ApiBaseUrl => LocalLlmHost.ApiBaseUrl;

        /// <summary>Model id for chat requests (from ocr.kcpps / host).</summary>
        public static string ModelApiId => LocalLlmHost.ModelApiId;

        /// <summary>
        /// OpenAI-vision user content array. Indexer keys are explicit runtime strings.
        /// </summary>
        public static JsonArray BuildUserContent(string dataUrl, string prompt)
        {
            var imagePart = new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = dataUrl
                }
            };

            var content = new JsonArray { imagePart };
            if (!string.IsNullOrEmpty(prompt))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt
                });
            }
            return content;
        }

        /// <summary>
        /// Chat Completions body as <see cref="JsonObject"/> (not anonymous types).
        /// </summary>
        public static string BuildChatRequestJson(
            JsonArray userContent,
            int maxTokens,
            double temperature)
        {
            var payload = new JsonObject
            {
                ["model"] = ModelApiId,
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = userContent
                    }
                }
            };
            return payload.ToJsonString();
        }

        /// <summary>POST /v1/chat/completions; returns message content or empty.</summary>
        public static async Task<string> ChatAsync(
            JsonArray userContent,
            int maxTokens,
            double temperature = 0)
        {
            string json = BuildChatRequestJson(userContent, maxTokens, temperature);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            string url = ApiBaseUrl + "chat/completions";
            var response = await Http.PostAsync(url, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine(
                    $"[LocalLlmClient] HTTP {(int)response.StatusCode}: " +
                    $"{body[..Math.Min(200, body.Length)]}");
                return "";
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var msg = choices[0];
                if (msg.TryGetProperty("message", out var m) &&
                    m.TryGetProperty("content", out var c))
                    return SanitizeModelText(c.GetString());
            }

            return "";
        }

        /// <summary>
        /// First-pass scrub of Local-LLM message content, before CleanForSpeech /
        /// fusion / quality gates. Wire text is UTF-8 JSON; this is not re-encoding —
        /// it maps common typography to ASCII then keeps only:
        /// Latin letters (basic + Latin-1 / Latin Extended-A), digits, whitespace,
        /// and basic punctuation used by English comic OCR / TTS.
        /// Drops CJK, emoji, private-use, exotic scripts, and other junk the VL
        /// sometimes emits on hard crops.
        /// </summary>
        public static string SanitizeModelText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            // Typography → ASCII so curly quotes / dashes survive the allow-list.
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\u2018': // ‘
                    case '\u2019': // ’
                    case '\u201A': // ‚
                    case '\u2032': // ′
                    case '\u02BC': // ʼ
                        sb.Append('\'');
                        continue;
                    case '\u201C': // “
                    case '\u201D': // ”
                    case '\u201E': // „
                        sb.Append('"');
                        continue;
                    case '\u2013': // –
                    case '\u2014': // —
                    case '\u2212': // −
                        sb.Append('-');
                        continue;
                    case '\u2026': // …
                        sb.Append("...");
                        continue;
                    case '\u00A0': // NBSP
                    case '\u202F': // narrow NBSP
                    case '\u2007': // figure space
                        sb.Append(' ');
                        continue;
                    case '\u00AD': // soft hyphen
                    case '\uFEFF': // BOM / ZWNBSP
                    case '\u200B': // zero-width space
                    case '\u200C': // ZWNJ
                    case '\u200D': // ZWJ
                    case '\u2060': // word joiner
                        continue;
                    case '\r':
                        // Normalize CRLF / lone CR later via whitespace collapse path.
                        sb.Append('\n');
                        continue;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            // Allow-list: Latin letters, digits, whitespace, basic punct.
            // Latin ranges match English comics + common Western European names
            // (café, naïve) without opening the door to Linear-B / PUA spam.
            var kept = new StringBuilder(sb.Length);
            char prev = '\0';
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];

                if (c == '\n')
                {
                    // Collapse runs of newlines to a single blank-line break max
                    // is left to CleanForSpeech; here just keep \n and squash \n\n\n+.
                    if (prev == '\n')
                    {
                        // Allow at most one extra \n (blank line) then skip more.
                        if (kept.Length >= 2 && kept[^2] == '\n')
                            continue;
                    }
                    kept.Append('\n');
                    prev = '\n';
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    // Other whitespace → single space (not across newlines).
                    if (prev == ' ' || prev == '\n')
                        continue;
                    kept.Append(' ');
                    prev = ' ';
                    continue;
                }

                if (IsKeptLatinLetter(c) || char.IsAsciiDigit(c) || IsKeptPunctuation(c))
                {
                    kept.Append(c);
                    prev = c;
                }
                // else: drop non-Latin letters, emoji, private-use, controls, etc.
                // No '?' placeholders — those get spoken by TTS.
            }

            return kept.ToString().Trim();
        }

        /// <summary>
        /// Basic Latin + Latin-1 Supplement letters + Latin Extended-A (U+0000–U+024F
        /// letter codepoints). Rejects Greek, Cyrillic, CJK, private-use, etc.
        /// </summary>
        private static bool IsKeptLatinLetter(char c)
        {
            if (!char.IsLetter(c))
                return false;
            // Basic Latin, Latin-1, Latin Extended-A/B through U+024F
            return c <= 0x024F;
        }

        /// <summary>Punctuation Safe for English OCR / later CleanForSpeech pauses.</summary>
        private static bool IsKeptPunctuation(char c) =>
            c is '.' or '!' or '?' or ',' or '\'' or '"'
                or '-' or ':' or ';' or '(' or ')' or '/' or '&';

        /// <summary>
        /// Smoke / post-obfuscation check: payload must contain required wire keys.
        /// </summary>
        public static bool SmokeVerifyJsonShape(out string sampleJson)
        {
            var content = BuildUserContent("data:image/png;base64,AA==", "smoke");
            sampleJson = BuildChatRequestJson(content, 16, 0);
            return sampleJson.Contains("\"model\"", StringComparison.Ordinal)
                && sampleJson.Contains("\"messages\"", StringComparison.Ordinal)
                && sampleJson.Contains("\"image_url\"", StringComparison.Ordinal)
                && sampleJson.Contains("\"content\"", StringComparison.Ordinal)
                && sampleJson.Contains(ModelApiId, StringComparison.Ordinal);
        }
    }
}
