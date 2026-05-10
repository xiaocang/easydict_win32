using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Easydict.WinUI.Services;

/// <summary>
/// Clamps saved window positions to the currently-attached monitors so a
/// restored window never appears off-screen — e.g. after an external monitor
/// is disconnected (issue #148).
/// </summary>
public static class WindowPositionHelper
{
    /// <summary>
    /// <list type="bullet">
    /// <item>If every corner of the desired rect is inside the union of work areas
    /// (e.g. the user deliberately straddled two monitors), the position is
    /// returned unchanged.</item>
    /// <item>Otherwise the largest-overlap work area is chosen and the position is
    /// shifted so the window fits inside it.</item>
    /// <item>If the desired rect doesn't overlap any work area, returns false so
    /// the caller can fall back to its own default placement.</item>
    /// </list>
    /// </summary>
    /// <param name="workAreas">First entry wins overlap ties (callers should put the primary monitor first).</param>
    public static bool TryClampToVisibleWorkArea(
        PointInt32 desiredTopLeft,
        SizeInt32 windowSize,
        IReadOnlyList<RectInt32> workAreas,
        out PointInt32 clamped)
    {
        clamped = default;
        if (workAreas == null || workAreas.Count == 0)
        {
            return false;
        }

        // If every corner is covered by some work area, the window is fully visible
        // (modulo unusual gapped monitor layouts) — preserve the user's chosen position.
        if (AllCornersCovered(desiredTopLeft, windowSize, workAreas))
        {
            clamped = desiredTopLeft;
            return true;
        }

        long bestOverlap = 0;
        var bestIndex = -1;

        for (var i = 0; i < workAreas.Count; i++)
        {
            var overlap = IntersectionArea(desiredTopLeft, windowSize, workAreas[i]);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        var work = workAreas[bestIndex];

        // Shift (do not resize) so the window fits inside the chosen work area.
        // If the window is wider/taller than the work area, pin to that axis's
        // top-left rather than producing a negative coordinate.
        var maxX = work.X + Math.Max(0, work.Width - windowSize.Width);
        var maxY = work.Y + Math.Max(0, work.Height - windowSize.Height);
        var x = Math.Clamp(desiredTopLeft.X, work.X, maxX);
        var y = Math.Clamp(desiredTopLeft.Y, work.Y, maxY);

        clamped = new PointInt32(x, y);
        return true;
    }

    private static bool AllCornersCovered(
        PointInt32 topLeft,
        SizeInt32 size,
        IReadOnlyList<RectInt32> workAreas)
    {
        // Right/bottom edges are exclusive: the rect (x, y, x+w, y+h) covers
        // pixels in [x, x+w) × [y, y+h), so a corner exactly at the rect's
        // right or bottom edge belongs to the *next* monitor (if any).
        var right = topLeft.X + size.Width;
        var bottom = topLeft.Y + size.Height;
        return CoveredExclusive(topLeft.X, topLeft.Y, workAreas)
            && CoveredExclusive(right - 1, topLeft.Y, workAreas)
            && CoveredExclusive(topLeft.X, bottom - 1, workAreas)
            && CoveredExclusive(right - 1, bottom - 1, workAreas);
    }

    private static bool CoveredExclusive(int x, int y, IReadOnlyList<RectInt32> workAreas)
    {
        for (var i = 0; i < workAreas.Count; i++)
        {
            var w = workAreas[i];
            if (x >= w.X && x < w.X + w.Width && y >= w.Y && y < w.Y + w.Height)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pulls the current monitor work areas (primary first) and forwards to
    /// <see cref="TryClampToVisibleWorkArea"/>.
    /// </summary>
    public static bool TryGetVisiblePosition(
        PointInt32 desiredTopLeft,
        SizeInt32 windowSize,
        out PointInt32 result)
    {
        result = default;

        IReadOnlyList<DisplayArea> displays;
        try
        {
            displays = DisplayArea.FindAll();
        }
        catch (COMException)
        {
            // DisplayArea is a WinRT API; the realistic failure mode is the runtime
            // not being initialized. Fall back to the caller's default placement.
            return false;
        }

        if (displays.Count == 0)
        {
            return false;
        }

        var primary = DisplayArea.Primary;
        var workAreas = new List<RectInt32>(displays.Count);
        if (primary != null)
        {
            workAreas.Add(primary.WorkArea);
        }
        foreach (var display in displays)
        {
            if (primary != null && display.DisplayId.Value == primary.DisplayId.Value)
            {
                continue;
            }
            workAreas.Add(display.WorkArea);
        }

        return TryClampToVisibleWorkArea(desiredTopLeft, windowSize, workAreas, out result);
    }

    private static long IntersectionArea(PointInt32 topLeft, SizeInt32 size, RectInt32 work)
    {
        var left = Math.Max(topLeft.X, work.X);
        var top = Math.Max(topLeft.Y, work.Y);
        var right = Math.Min(topLeft.X + size.Width, work.X + work.Width);
        var bottom = Math.Min(topLeft.Y + size.Height, work.Y + work.Height);
        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }
        return (long)w * h;
    }
}
