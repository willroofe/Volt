namespace Volt;

/// <summary>
/// Tracks a contiguous inclusive row range that needs redrawing.
/// Not thread-safe — only touched from the UI thread.
/// </summary>
public struct GridRegion
{
    public int MinRow { get; private set; }
    public int MaxRow { get; private set; }
    public bool IsEmpty { get; private set; }

    public GridRegion()
    {
        MinRow = int.MaxValue;
        MaxRow = int.MinValue;
        IsEmpty = true;
    }

    public void MarkDirty(int row)
    {
        if (IsEmpty)
        {
            MinRow = row;
            MaxRow = row;
            IsEmpty = false;
            return;
        }
        if (row < MinRow) MinRow = row;
        if (row > MaxRow) MaxRow = row;
    }

    public void MarkDirtyRange(int startRow, int endRow)
    {
        MarkDirty(startRow);
        MarkDirty(endRow);
    }

    public void Clear()
    {
        MinRow = int.MaxValue;
        MaxRow = int.MinValue;
        IsEmpty = true;
    }
}
