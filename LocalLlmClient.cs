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
                    return c.GetString() ?? "";
            }

            return "";
        }

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
