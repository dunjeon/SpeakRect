using System;
using System.Drawing;

namespace SpeakRect
{
    /// <summary>
    /// Dual comic pipeline bitmaps with a hard invariant:
    /// <list type="bullet">
    /// <item><see cref="Tone"/> — Local-LLM / live OCR input (never fogged).</item>
    /// <item><see cref="Detect"/> — Windows.Media.Ocr balloon detect only
    /// (optional gray fog; same geometry as Tone).</item>
    /// </list>
    /// Fog must never be applied to Tone. Callers dispose via <see cref="Dispose"/>;
    /// Tone is not disposed here (owned by prep stages / caller).
    /// </summary>
    public sealed class ComicDetectTonePair : IDisposable
    {
        /// <summary>Pre-fog tone (or gray/upscale when tone step off). Local-LLM reads this.</summary>
        public Bitmap Tone { get; }

        /// <summary>Detect view: fog bitmap or same instance as <see cref="Tone"/>.</summary>
        public Bitmap Detect { get; }

        /// <summary>True when Detect is a separate fog bitmap owned by this pair.</summary>
        public bool DetectIsSeparateFog { get; }

        private bool _disposed;
        private bool _detectOwnershipReleased;

        private ComicDetectTonePair(Bitmap tone, Bitmap detect, bool detectIsSeparateFog)
        {
            Tone = tone ?? throw new ArgumentNullException(nameof(tone));
            Detect = detect ?? throw new ArgumentNullException(nameof(detect));
            DetectIsSeparateFog = detectIsSeparateFog;
        }

        /// <summary>
        /// Transfer ownership of a separate fog <see cref="Detect"/> to the caller.
        /// After this, <see cref="Dispose"/> will not free Detect.
        /// </summary>
        public Bitmap ReleaseDetect()
        {
            if (!DetectIsSeparateFog)
                throw new InvalidOperationException(
                    "Detect aliases Tone; clone Tone instead of releasing.");
            _detectOwnershipReleased = true;
            return Detect;
        }

        /// <summary>
        /// Build detect vs tone views from shared prep tone.
        /// When fog is off, Detect aliases Tone (no extra bitmap).
        /// </summary>
        /// <param name="toneOrPre">Pipeline tone (or gray/upscale). Not owned by the pair.</param>
        /// <param name="enableDetectFog">Settings → Balloons gray fog for detect only.</param>
        /// <param name="fogAmount">0..1 blend toward gray.</param>
        /// <param name="fogLevel">Target gray level byte.</param>
        /// <param name="applyGrayFog">
        /// Factory: <c>(source, amount, level) => new fog bitmap</c>.
        /// Only invoked when fog is enabled and amount is meaningful.
        /// </param>
        public static ComicDetectTonePair Create(
            Bitmap toneOrPre,
            bool enableDetectFog,
            float fogAmount,
            byte fogLevel,
            Func<Bitmap, float, byte, Bitmap> applyGrayFog)
        {
            if (toneOrPre == null)
                throw new ArgumentNullException(nameof(toneOrPre));
            if (applyGrayFog == null)
                throw new ArgumentNullException(nameof(applyGrayFog));

            if (enableDetectFog && fogAmount > 0.001f)
            {
                Bitmap fog = applyGrayFog(toneOrPre, fogAmount, fogLevel);
                return new ComicDetectTonePair(toneOrPre, fog, detectIsSeparateFog: true);
            }

            return new ComicDetectTonePair(toneOrPre, toneOrPre, detectIsSeparateFog: false);
        }

        /// <summary>Disposes the fog bitmap when separate; never disposes Tone.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (DetectIsSeparateFog &&
                !_detectOwnershipReleased &&
                !ReferenceEquals(Detect, Tone))
            {
                try { Detect.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
