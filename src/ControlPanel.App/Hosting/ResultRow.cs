namespace ControlPanel.App.Hosting;

/// <summary>
/// Our console-side record for one result-pane (list) row. Mirrors
/// <see cref="ScopeNode"/> but for HRESULTITEM handles. Column text beyond
/// what the snap-in supplied at insert time is fetched lazily via
/// IComponent.GetDisplayInfo and cached here, the same "virtual list"
/// pattern MMC itself uses.
/// </summary>
internal sealed class ResultRow
{
    public required IntPtr ItemId { get; init; }
    public required IntPtr Cookie { get; init; }
    public required SnapInSession Session { get; init; }

    public int ImageIndex { get; set; } = -1;
    public Dictionary<int, string> ColumnCache { get; } = new();
}
