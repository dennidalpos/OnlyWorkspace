namespace OnlyWorkspace.Tests;

/// <summary>
/// WinForms tests create forms on their own STA threads; run in parallel they can starve each other's
/// message loops past the per-test timeout, so they share one sequential collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WinFormsCollection
{
    public const string Name = "WinForms";
}
