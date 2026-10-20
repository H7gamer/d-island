// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows.Controls;
using System.Windows.Input;

namespace DIsland;

/// <summary>
/// Proportional wheel scrolling for a ScrollViewer. WPF scrolls a fixed 3 lines (48 px) per wheel
/// message whatever its delta, so a precision touchpad, which sends many small-delta messages per
/// swipe, scrolls far too fast. This scrolls by the delta instead: one mouse notch (120) still
/// moves <c>pixelsPerNotch</c>, and touchpad swipes move in proportion.
/// </summary>
public static class WheelScrolling
{
    public static void Attach(ScrollViewer scroller, double pixelsPerNotch = 48)
    {
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (scroller.ScrollableHeight <= 0) return; // nothing to scroll; let parents handle it
            // MouseWheelDeltaForOneLine is the 120 units of one notch.
            var target = scroller.VerticalOffset - (double)e.Delta / Mouse.MouseWheelDeltaForOneLine * pixelsPerNotch;
            target = Math.Clamp(target, 0, scroller.ScrollableHeight);
            if (Math.Abs(target - scroller.VerticalOffset) < 0.01) return; // at the end: let parents handle it
            scroller.ScrollToVerticalOffset(target);
            e.Handled = true;
        };
    }
}
