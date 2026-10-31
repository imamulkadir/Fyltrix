namespace Fyltrix.Core;

/// <summary>Portable baseline approved in Round 8. Never populated from a user's settings file.
/// Other established defaults remain in AppSettings; create a fresh instance for resets.</summary>
public static class ApplicationDefaults
{
    public const ThemeMode Theme = ThemeMode.System;
    public const bool ShowFileOperationLabels = false;
    public const bool ShowSecondaryToolbarLabels = false;
    public static IReadOnlyList<string> HiddenToolbarButtons { get; } = Array.AsReadOnly(new[] { "newfolder", "favourite", "explorer", "terminal" });
    public static IReadOnlyList<string> ToolbarExtras { get; } = Array.AsReadOnly(new[] { "Active shares…" });
    public static IReadOnlyList<string> FileOperationButtons { get; } = Array.AsReadOnly(new[] { "cut", "copy", "paste", "rename", "delete" });

    public static AppSettings Create() => new();
}
