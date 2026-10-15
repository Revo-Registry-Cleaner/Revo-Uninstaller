using Zidimi.Uninstaller.Models;
using Zidimi.Uninstaller.Services.Abstractions;

namespace Zidimi.Uninstaller.Services.Leftovers;

public sealed class LeftoversService : ILeftoversService
{
    private readonly ILeftoversProvider _provider;
    public LeftoversService(ILeftoversProvider provider) => _provider = provider;

    public Task<IReadOnlyList<LeftoverItem>> ScanSystemOrphanedLeftoversAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => _provider.ScanSystemOrphanedLeftovers(progress), cancellationToken);

    public Task<DeepCleanResult> CleanLeftoversAsync(
        IEnumerable<LeftoverItem> items,
        bool recycleBin,
        string recoveryTitle,
        string applicationName,
        bool enableRecoveryVault,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => _provider.CleanLeftovers(
                items,
                recycleBin,
                recoveryTitle,
                applicationName,
                createRecoveryPoint: true,
                enableRecoveryVault: enableRecoveryVault,
                operation: "TraceCleaner"),
            cancellationToken);

    public void OpenRegistryKey(string path) => _provider.OpenRegistryKey(path);
    public void OpenInExplorer(string path) => _provider.OpenInExplorer(path);
    public void OpenManagementConsole(string fileName) => _provider.OpenManagementConsole(fileName);
}
