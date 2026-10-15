namespace Zidimi.Uninstaller.Models;

public sealed class HunterWindowTarget
{
    public IntPtr WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
}
