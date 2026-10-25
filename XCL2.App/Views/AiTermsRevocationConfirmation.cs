using System.Windows;

namespace XCL2.App.Views;

internal static class AiTermsRevocationConfirmation
{
    internal static bool Confirm(Window owner)
        => Ask(owner, "关闭后将停止 AI 助手的当前请求，并返回首页。要拒绝 AI 助手的用户协议与条款吗？", "关闭 AI 助手 · 第一次确认")
           && Ask(owner, "确认撤回同意？下次打开 AI 助手需要重新阅读并同意协议。已有本地对话历史会保留。", "关闭 AI 助手 · 第二次确认");

    internal static void ShowError(Window owner, string message)
    {
        if (owner is MainWindow)
            MessageBoxDialog.ShowError(message, "关闭 AI 助手");
        else
            OwnedMessageDialog.Show(owner, message, "关闭 AI 助手", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool Ask(Window owner, string message, string title)
    {
        if (owner is MainWindow)
            return MessageBoxDialog.ShowCustomYesNo(message, title, "继续使用", "确认关闭", rightIsPrimary: false);
        // 独立模态设置窗口禁用了主窗口，因此消息仍由当前设置窗口拥有。
        return OwnedMessageDialog.ConfirmCustom(owner, message, title, "继续使用", "确认关闭");
    }
}
