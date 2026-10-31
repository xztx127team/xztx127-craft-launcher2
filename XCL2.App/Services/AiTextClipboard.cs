using System.Runtime.InteropServices;
using System.Windows;

namespace XCL2.App.Services;

/// <summary>One explicit plain-text clipboard path for AI output and its code blocks.</summary>
internal static class AiTextClipboard
{
    internal static bool TryCopy(string text, bool notifySuccess = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            ToastService.ShowInfo("请先选择要复制的文字。");
            return false;
        }
        try
        {
            // Use the response/selection itself. Never rebuild text from visual lines or Trim it.
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            if (notifySuccess) ToastService.ShowSuccess("已复制");
            return true;
        }
        catch (ExternalException)
        {
            ToastService.ShowWarning("复制失败：剪贴板正被其他程序占用，请稍后重试或按 Ctrl+C。");
            return false;
        }
    }
}
