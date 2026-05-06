namespace Volt;

internal readonly record struct FileLoadProgress(
    string Phase,
    long BytesProcessed,
    long TotalBytes,
    double? Percent,
    bool IsComplete)
{
    public static FileLoadProgress Indeterminate(string phase, bool isComplete = false) =>
        new(phase, 0, 0, isComplete ? 100 : null, isComplete);

    public static FileLoadProgress ForBytes(
        string phase,
        long bytesProcessed,
        long totalBytes,
        bool isComplete = false)
    {
        bytesProcessed = Math.Max(0, bytesProcessed);
        totalBytes = Math.Max(0, totalBytes);
        double? percent = totalBytes > 0
            ? Math.Clamp(bytesProcessed * 100.0 / totalBytes, 0, 100)
            : isComplete ? 100 : null;

        return new FileLoadProgress(phase, bytesProcessed, totalBytes, percent, isComplete);
    }

    public static FileLoadProgress Complete(string phase, long totalBytes) =>
        ForBytes(phase, totalBytes, totalBytes, isComplete: true);
}
