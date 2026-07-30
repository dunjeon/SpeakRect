using System.Drawing;

namespace SpeakRect
{
    /// <summary>
    /// One detected text island (pipeline coordinates).
    /// <see cref="WinOcrText"/> is primarily detect-only (junk filter / debug);
    /// spoken only as last-resort TTS when Local-LLM best-of is empty.
    /// </summary>
    public sealed class DetectedTextRegion
    {
        public Rectangle Bounds { get; init; }

        /// <summary>
        /// OCR detect line text: filter empty/junk boxes, score detect passes,
        /// and last-resort TTS fallback when Local-LLM yields nothing.
        /// </summary>
        public string WinOcrText { get; init; } = "";
    }
}
