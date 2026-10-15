using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Zidimi.Uninstaller.Models;
using Zidimi.Uninstaller.Infrastructure.Metadata;

namespace Zidimi.Uninstaller.Platform.Windows;

public static class StartupService
{
    private static RegistryArchitecture ToModelRegistryArchitecture(RegistryView view) => view switch
    {
        RegistryView.Registry64 => RegistryArchitecture.Registry64,
        RegistryView.Registry32 => RegistryArchitecture.Registry32,
        _ => RegistryArchitecture.Default
    };

    private static RegistryDataKind ToModelRegistryDataKind(RegistryValueKind kind)
        => Enum.TryParse<RegistryDataKind>(kind.ToString(), ignoreCase: false, out var mapped)
            ? mapped
            : RegistryDataKind.Unknown;

    private static RegistryView ToNativeRegistryView(RegistryArchitecture architecture) => architecture switch
    {
        RegistryArchitecture.Registry64 => RegistryView.Registry64,
        RegistryArchitecture.Registry32 => RegistryView.Registry32,
        _ => RegistryView.Default
    };

    private static RegistryValueKind ToNativeRegistryValueKind(RegistryDataKind kind)
        => Enum.TryParse<RegistryValueKind>(kind.ToString(), ignoreCase: false, out var mapped)
            ? mapped
            : RegistryValueKind.String;

    private sealed class DisabledRegistryStartup
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public bool IsMachine { get; set; }
        // Preserve the existing JSON field names so older disabled-startup.json files remain readable.
        [JsonPropertyName("RegistryView")]
        public RegistryArchitecture RegistryArchitecture { get; set; } = RegistryArchitecture.Default;

        [JsonPropertyName("RegistryValueKind")]
        public RegistryDataKind RegistryDataKind { get; set; } = RegistryDataKind.String;
    }

    private static readonly object DisabledStoreLock = new();
    private static string DisabledStorePath => AppDataPaths.StartupDisabledFile;
    public static List<StartupEntry> GetEntries()
    {
        var list = new List<StartupEntry>();

        // Registry startup locations. Read each view explicitly so writes target the same view later.
        var nativeView = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        AddRunKey(list, RegistryHive.CurrentUser, nativeView, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
        AddRunKey(list, RegistryHive.CurrentUser, nativeView, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", false);
        AddRunKey(list, RegistryHive.LocalMachine, nativeView, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        AddRunKey(list, RegistryHive.LocalMachine, nativeView, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true);

        if (Environment.Is64BitOperatingSystem)
        {
            AddRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            AddRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true);
        }

        AddManagedDisabledRegistryEntries(list);

        // Folder Startup locations
        AddFolderEntries(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), false);
        AddFolderEntries(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true);

        return list;
    }

    private static void AddRunKey(
        List<StartupEntry> list,
        RegistryHive hive,
        RegistryView view,
        string path,
        bool isMachine)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path);
            if (key == null) return;

            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName)?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value)) continue;

                var entry = new StartupEntry
                {
                    Name = valueName,
                    Command = value,
                    ExecutablePath = ExtractExecutablePath(value),
                    Location = key.Name,
                    IsMachine = isMachine,
                    IsFolderEntry = false,
                    RegistryArchitecture = ToModelRegistryArchitecture(view),
                    RegistryDataKind = ToModelRegistryDataKind(key.GetValueKind(valueName))
                };
                PopulateMetadata(entry);
                list.Add(entry);
            }
        }
        catch
        {
            // Skip unreadable keys.
        }
    }

    private static void AddFolderEntries(List<StartupEntry> list, string folderPath, bool isMachine)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        try
        {
            var files = Directory.GetFiles(folderPath);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue; // ignore desktop.ini

                var isEnabled = !ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(name);

                var entry = new StartupEntry
                {
                    Name = name,
                    Command = file,
                    ExecutablePath = file,
                    Location = folderPath,
                    IsMachine = isMachine,
                    IsFolderEntry = true,
                    IsEnabled = isEnabled
                };
                PopulateMetadata(entry);
                list.Add(entry);
            }
        }
        catch
        {
            // Skip inaccessible directories
        }
    }

    private static void PopulateMetadata(StartupEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath)) return;
        try
        {
            var exp = Environment.ExpandEnvironmentVariables(entry.ExecutablePath.Trim('\"', '\''));
            if (File.Exists(exp))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exp);
                entry.Publisher = vi.CompanyName ?? string.Empty;
                entry.Version = vi.FileVersion ?? string.Empty;
            }
        }
        catch { }
    }

    public static void OpenRegistryKey(string keyPath)
    {
        // Setting Regedit's LastKey is best-effort; failure should not make this UI action crash.
        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", true);
            regKey?.SetValue("LastKey", keyPath);
        }
        catch { }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch { }
    }

    public static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;

        var trimmed = command.Trim();

        // 1. Quoted path: "C:\Program Files\App\app.exe" --arg
        if (trimmed.StartsWith("\""))
        {
            var endQuote = trimmed.IndexOf('\"', 1);
            if (endQuote > 1)
            {
                var candidate = trimmed.Substring(1, endQuote - 1);
                var exp = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(exp)) return exp;
                if (File.Exists(candidate)) return candidate;
                return exp;
            }
        }

        // 2. SeparateArgsFromCommand
        var (file, _) = ProcessTools.SeparateArgsFromCommand(trimmed);
        if (File.Exists(file)) return file;

        var expFile = Environment.ExpandEnvironmentVariables(file);
        if (File.Exists(expFile)) return expFile;

        // 3. Space-delimited prefix check: C:\Program Files (x86)\App\app.exe /arg
        var spaceParts = trimmed.Split(' ');
        var accum = string.Empty;
        foreach (var p in spaceParts)
        {
            accum = string.IsNullOrEmpty(accum) ? p : accum + " " + p;
            var clean = accum.Trim('\"', '\'');
            var expClean = Environment.ExpandEnvironmentVariables(clean);
            if (File.Exists(expClean)) return expClean;
            if (File.Exists(clean)) return clean;
        }

        return expFile;
    }

    public static bool SetEnabled(StartupEntry entry, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(entry.Location) || string.IsNullOrWhiteSpace(entry.Name))
            return false;

        try
        {
            if (entry.IsFolderEntry)
            {
                var filePath = entry.Command;
                if (enabled)
                {
                    if (filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        var newPath = filePath[..^9];
                        if (!File.Exists(filePath) || File.Exists(newPath)) return false;
                        File.Move(filePath, newPath);
                        entry.Command = newPath;
                        entry.ExecutablePath = newPath;
                    }
                }
                else
                {
                    if (!filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        var newPath = filePath + ".disabled";
                        if (!File.Exists(filePath) || File.Exists(newPath)) return false;
                        File.Move(filePath, newPath);
                        entry.Command = newPath;
                        entry.ExecutablePath = newPath;
                    }
                }

                entry.IsEnabled = enabled;
                return true;
            }
            else
            {
                var (hive, subPath) = ParseFullPath(entry.Location);
                using var baseKey = RegistryKey.OpenBaseKey(hive, ToNativeRegistryView(entry.RegistryArchitecture));
                if (baseKey == null) return false;

                using var key = enabled
                    ? baseKey.CreateSubKey(subPath, writable: true)
                    : baseKey.OpenSubKey(subPath, writable: true);

                if (enabled)
                {
                    if (key == null) return false;
                    key.SetValue(entry.Name, entry.Command, ToNativeRegistryValueKind(entry.RegistryDataKind));
                    try { RemoveDisabledRegistryEntry(entry); } catch { }
                }
                else
                {
                    // Save first so a successful disable is always reversible after a rescan or restart.
                    SaveDisabledRegistryEntry(entry);
                    if (key != null)
                    {
                        try
                        {
                            key.DeleteValue(entry.Name, throwOnMissingValue: false);
                        }
                        catch
                        {
                            RemoveDisabledRegistryEntry(entry);
                            throw;
                        }
                    }
                }

                entry.IsEnabled = enabled;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void AddManagedDisabledRegistryEntries(List<StartupEntry> list)
    {
        foreach (var saved in LoadDisabledRegistryEntries())
        {
            if (list.Any(e => !e.IsFolderEntry
                           && e.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)
                           && e.Location.Equals(saved.Location, StringComparison.OrdinalIgnoreCase)
                           && e.RegistryArchitecture == saved.RegistryArchitecture))
                continue;

            var entry = new StartupEntry
            {
                Name = saved.Name,
                Command = saved.Command,
                ExecutablePath = ExtractExecutablePath(saved.Command),
                Location = saved.Location,
                IsMachine = saved.IsMachine,
                IsFolderEntry = false,
                RegistryArchitecture = saved.RegistryArchitecture,
                RegistryDataKind = saved.RegistryDataKind,
                IsEnabled = false
            };
            PopulateMetadata(entry);
            list.Add(entry);
        }
    }

    private static List<DisabledRegistryStartup> LoadDisabledRegistryEntries()
    {
        lock (DisabledStoreLock)
        {
            try
            {
                if (!File.Exists(DisabledStorePath))
                    return new List<DisabledRegistryStartup>();

                return JsonSerializer.Deserialize<List<DisabledRegistryStartup>>(File.ReadAllText(DisabledStorePath))
                       ?? new List<DisabledRegistryStartup>();
            }
            catch
            {
                return new List<DisabledRegistryStartup>();
            }
        }
    }

    private static void SaveDisabledRegistryEntry(StartupEntry entry)
    {
        lock (DisabledStoreLock)
        {
            var entries = LoadDisabledRegistryEntriesUnsafe();
            entries.RemoveAll(x => x.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                                && x.Location.Equals(entry.Location, StringComparison.OrdinalIgnoreCase)
                                && x.RegistryArchitecture == entry.RegistryArchitecture);
            entries.Add(new DisabledRegistryStartup
            {
                Name = entry.Name,
                Command = entry.Command,
                Location = entry.Location,
                IsMachine = entry.IsMachine,
                RegistryArchitecture = entry.RegistryArchitecture,
                RegistryDataKind = entry.RegistryDataKind
            });
            SaveDisabledRegistryEntriesUnsafe(entries);
        }
    }

    private static void RemoveDisabledRegistryEntry(StartupEntry entry)
    {
        lock (DisabledStoreLock)
        {
            var entries = LoadDisabledRegistryEntriesUnsafe();
            if (entries.RemoveAll(x => x.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                                    && x.Location.Equals(entry.Location, StringComparison.OrdinalIgnoreCase)
                                    && x.RegistryArchitecture == entry.RegistryArchitecture) > 0)
                SaveDisabledRegistryEntriesUnsafe(entries);
        }
    }

    private static List<DisabledRegistryStartup> LoadDisabledRegistryEntriesUnsafe()
    {
        try
        {
            if (!File.Exists(DisabledStorePath))
                return new List<DisabledRegistryStartup>();
            return JsonSerializer.Deserialize<List<DisabledRegistryStartup>>(File.ReadAllText(DisabledStorePath))
                   ?? new List<DisabledRegistryStartup>();
        }
        catch
        {
            return new List<DisabledRegistryStartup>();
        }
    }

    private static void SaveDisabledRegistryEntriesUnsafe(List<DisabledRegistryStartup> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DisabledStorePath)!);
        var tempPath = DisabledStorePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, DisabledStorePath, overwrite: true);
    }

    public static bool OpenCommandLocation(StartupEntry entry)
    {
        try
        {
            var targetPath = !string.IsNullOrWhiteSpace(entry.ExecutablePath) ? entry.ExecutablePath : entry.Command;
            var (file, _) = ProcessTools.SeparateArgsFromCommand(targetPath);
            if (string.IsNullOrWhiteSpace(file)) file = targetPath;

            var exp = Environment.ExpandEnvironmentVariables(file.Trim('"', '\''));

            if (Directory.Exists(exp))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exp}\"") { UseShellExecute = true });
                return true;
            }
            if (File.Exists(exp))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{exp}\"") { UseShellExecute = true });
                return true;
            }
        }
        catch { }

        return false;
    }

    private static (RegistryHive Hive, string SubPath) ParseFullPath(string fullPath)
    {
        var hive = fullPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;

        var idx = fullPath.IndexOf('\\');
        var sub = idx >= 0 ? fullPath[(idx + 1)..] : fullPath;
        return (hive, sub);
    }

}