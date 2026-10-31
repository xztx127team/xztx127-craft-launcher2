using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private Task? _startupUpdateGate;
    private Task EnsureStartupUpdateGateAsync()
        => _startupUpdateGate ??= UpdateCheckService.EnforceCachedRequiredUpdateAsync();
}
