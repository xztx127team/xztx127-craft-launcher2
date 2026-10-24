namespace XCL2.App.Views;

/// <summary>聊天页、崩溃分析浮层和 AI 设置共用的联网搜索开启确认。</summary>
internal static class AiWebSearchConfirmation
{
    internal const string Notice = "部分模型供应商不支持联网搜索，这里建议不要打开。";

    internal static bool ConfirmEnable()
    {
        // 默认保留“否”；只有明确选择“确认打开”才启用联网搜索。
        return MessageBoxDialog.ShowCustomYesNo(Notice, "确认打开联网搜索", "否", "确认打开",
            rightIsPrimary: false);
    }
}
