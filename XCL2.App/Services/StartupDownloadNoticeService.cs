using XCL2.App.Views;

namespace XCL2.App.Services;

public static class StartupDownloadNoticeService
{
    private static readonly StartupDownloadNoticePolicy Startup = new();

    /// <summary>Invoke from the startup announcement stage, after language selection and required agreements.</summary>
    public static void ShowIfNeeded(ConfigService configService)
    {
        if (!Startup.TryBegin(LocalizationService.CurrentLanguageCode, configService.Config)) return;
        OverlayDialogService.ShowModal(new StartupDownloadNoticeDialog(configService),
            dismissOnBackgroundClick: true, dismissOnEsc: true);
    }
}
