using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SpeakRect
{
    /// <summary>
    /// Session-only refined balloon regions for the current still page.
    /// Survives Preview / tab switches until <see cref="Clear"/> /
    /// <see cref="NotifyNewCapture"/> (new OCR snap or open image).
    /// List order = reading / crop-stack order. Coords = pipeline (tone) space.
    /// </summary>
    public static class ComicRegionOverrideSession
    {
        private static readonly object Gate = new();
        private static string _captureId = "";
        private static List<Rectangle> _regions = new();
        private static int _pipeW;
        private static int _pipeH;
        private static bool _active;
        private static Bitmap? _basePipeline;
        /// <summary>
        /// One-shot: overlay-hide should speak with overrides only after a real user
        /// edit, and only once until the user edits again.
        /// </summary>
        private static bool _pendingOverlaySpeak;

        /// <summary>True when the user has locked refined/custom regions.</summary>
        public static bool IsActive
        {
            get { lock (Gate) return _active && _regions.Count > 0; }
        }

        /// <summary>True when overlay hide should auto-speak the refined list once.</summary>
        public static bool PendingOverlaySpeak
        {
            get { lock (Gate) return _pendingOverlaySpeak && _active && _regions.Count > 0; }
        }

        public static string CaptureId
        {
            get { lock (Gate) return _captureId; }
        }

        public static int RegionCount
        {
            get { lock (Gate) return _active ? _regions.Count : 0; }
        }

        /// <summary>
        /// Stable id for a still frame (label + size only). Do not include timestamps
        /// that change while the same image is open — that cleared overrides mid-session.
        /// </summary>
        public static string MakeCaptureId(string? label, int width, int height)
        {
            string lab = string.IsNullOrWhiteSpace(label) ? "image" : label.Trim();
            return $"{lab}|{width}x{height}";
        }

        /// <summary>New OCR snap / new page — drop overrides.</summary>
        public static void NotifyNewCapture()
        {
            lock (Gate)
                ClearUnlocked();
        }

        /// <summary>
        /// Source label/size changed. Clears only when the id differs from the
        /// session binding (same page → keep overrides).
        /// </summary>
        public static void NotifySourceIdentity(string captureId)
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(captureId))
                {
                    ClearUnlocked();
                    return;
                }
                if (_active &&
                    !string.Equals(_captureId, captureId, StringComparison.Ordinal))
                    ClearUnlocked();
            }
        }

        /// <summary>
        /// Lock refined regions for this capture. Clones <paramref name="basePipeline"/>.
        /// </summary>
        public static void Set(
            string captureId,
            IEnumerable<Rectangle> regions,
            int pipeW,
            int pipeH,
            Bitmap? basePipeline)
        {
            if (string.IsNullOrEmpty(captureId) || regions == null)
                return;

            var list = new List<Rectangle>();
            foreach (var r in regions)
            {
                if (r.Width >= 4 && r.Height >= 4)
                    list.Add(r);
                if (list.Count >= RegionRefineSurface.MaxRegions)
                    break;
            }

            lock (Gate)
            {
                try { _basePipeline?.Dispose(); } catch { /* ignore */ }
                _basePipeline = null;

                _captureId = captureId;
                _regions = list;
                _pipeW = Math.Max(0, pipeW);
                _pipeH = Math.Max(0, pipeH);
                _active = list.Count > 0;
                if (basePipeline != null && _active)
                {
                    try { _basePipeline = new Bitmap(basePipeline); }
                    catch { _basePipeline = null; }
                }
            }
        }

        /// <summary>
        /// Snapshot for Speak / UI. Pass <paramref name="captureId"/> null to accept
        /// any active session (Settings close). When non-null, must match.
        /// </summary>
        public static bool TryGet(
            string? captureId,
            out List<Rectangle> regions,
            out int pipeW,
            out int pipeH,
            out Bitmap? baseClone)
        {
            lock (Gate)
            {
                regions = new List<Rectangle>();
                pipeW = _pipeW;
                pipeH = _pipeH;
                baseClone = null;
                if (!_active || _regions.Count == 0)
                    return false;
                if (captureId != null &&
                    captureId.Length > 0 &&
                    !string.Equals(_captureId, captureId, StringComparison.Ordinal))
                    return false;

                regions = _regions.ToList();
                if (_basePipeline != null)
                {
                    try { baseClone = new Bitmap(_basePipeline); }
                    catch { baseClone = null; }
                }
                return true;
            }
        }

        public static void Clear()
        {
            lock (Gate)
                ClearUnlocked();
        }

        /// <summary>
        /// Arm one-shot overlay-hide speak after the user edits refine geometry.
        /// No-op if there is no active region list.
        /// </summary>
        public static void ArmOverlaySpeak()
        {
            lock (Gate)
            {
                _pendingOverlaySpeak = _active && _regions.Count > 0;
            }
        }

        /// <summary>Cancel pending auto-speak (e.g. user already spoke from Balloons).</summary>
        public static void DisarmOverlaySpeak()
        {
            lock (Gate)
                _pendingOverlaySpeak = false;
        }

        /// <summary>
        /// If a one-shot overlay speak is pending, copy regions and clear the pending
        /// flag (regions stay locked for Balloons Speak / next edit).
        /// </summary>
        public static bool TryConsumeOverlaySpeak(out List<Rectangle> regions)
        {
            lock (Gate)
            {
                regions = new List<Rectangle>();
                if (!_pendingOverlaySpeak || !_active || _regions.Count == 0)
                    return false;
                _pendingOverlaySpeak = false;
                regions = _regions.ToList();
                return true;
            }
        }

        private static void ClearUnlocked()
        {
            _active = false;
            _pendingOverlaySpeak = false;
            _captureId = "";
            _regions = new List<Rectangle>();
            _pipeW = 0;
            _pipeH = 0;
            try { _basePipeline?.Dispose(); } catch { /* ignore */ }
            _basePipeline = null;
        }
    }
}
