using System;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// Width rules of the task pane, free of Office types. Office takes widths in system-DPI pixels, so a width given at
/// 96 DPI is scaled by the system DPI (never below 96). While the splitter is dragged the page keeps its own minimum
/// (<c>html { min-width: 340px }</c> with horizontal scroll); Office is written to only after the drag.
/// </summary>
internal static class PaneWidthPolicy
{
    public const int DefaultWidth96 = 380;
    public const int MinimumWidth96 = 360;
    public const int MaxAttempts = 4;

    public static int Scale(int width96, int systemDpi) => width96 * Math.Max(96, systemDpi) / 96;

    public static int DefaultWidth(int systemDpi) => Scale(DefaultWidth96, systemDpi);

    public static int MinimumWidth(int systemDpi) => Scale(MinimumWidth96, systemDpi);
}
