namespace ControlPanel.App.Hosting;

internal sealed class SnapInInfo
{
    public required Guid Clsid { get; init; }
    public required string Name { get; init; }
    public string? Provider { get; init; }
    public string? Version { get; init; }
    public bool Standalone { get; init; }
    public Guid? AboutClsid { get; init; }

    public override string ToString() => Name;
}
