using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zidimi.Uninstaller.Models;
public class StartupEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsMachine { get; set; }
    public bool IsFolderEntry { get; set; }
    public RegistryArchitecture RegistryArchitecture { get; set; } = RegistryArchitecture.Default;
    public RegistryDataKind RegistryDataKind { get; set; } = RegistryDataKind.String;

    public string ScopeText => IsMachine ? LanguageManager.T("Lng.Scope.Machine", "Machine") : LanguageManager.T("Lng.Scope.User", "User");
    public string LocationShort => Location.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || Location.Contains("Common", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBadgeVariant));
            }
        }
    }

    public string StatusText => IsEnabled ? LanguageManager.T("Lng.Features.StatusEnabled", "Enabled") : LanguageManager.T("Lng.Features.StatusDisabled", "Disabled");
    public string StatusBadgeVariant => IsEnabled ? "Success" : "Neutral";

    private byte[]? _iconData;
    public byte[]? IconData
    {
        get => _iconData;
        set { _iconData = value; OnPropertyChanged(); }
    }

    public override string ToString() => Name;
}