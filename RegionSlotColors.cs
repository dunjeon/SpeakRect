using System.Drawing;

namespace SpeakRect
{
    /// <summary>
    /// Stable palette for the eight fixed region slots (index 0 = R1 / F1).
    /// Shared by the overlay paint path and Settings → Regions map.
    /// </summary>
    public static class RegionSlotColors
    {
        private static readonly Color[] Fills =
        {
            Color.FromArgb(80, 255, 0, 0),     // R1 red
            Color.FromArgb(80, 0, 255, 0),     // R2 green
            Color.FromArgb(80, 0, 0, 255),     // R3 blue
            Color.FromArgb(80, 255, 255, 0),   // R4 yellow
            Color.FromArgb(80, 255, 0, 255),   // R5 magenta
            Color.FromArgb(80, 0, 255, 255),   // R6 cyan
            Color.FromArgb(80, 255, 165, 0),   // R7 orange
            Color.FromArgb(80, 128, 0, 128),   // R8 purple
        };

        private static readonly Color[] Solids =
        {
            Color.FromArgb(255, 0, 0),
            Color.FromArgb(0, 220, 0),
            Color.FromArgb(60, 100, 255),
            Color.FromArgb(230, 210, 0),
            Color.FromArgb(230, 0, 230),
            Color.FromArgb(0, 210, 210),
            Color.FromArgb(255, 165, 0),
            Color.FromArgb(180, 80, 200),
        };

        /// <summary>Semi-transparent fill for overlay / map (alpha 80).</summary>
        public static Color GetFill(int index0to7) =>
            Fills[Math.Clamp(index0to7, 0, 7)];

        /// <summary>Opaque accent for list chips and legend swatches.</summary>
        public static Color GetSolid(int index0to7) =>
            Solids[Math.Clamp(index0to7, 0, 7)];
    }
}
