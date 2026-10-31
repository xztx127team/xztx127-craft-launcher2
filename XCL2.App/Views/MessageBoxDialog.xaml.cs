using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 主流程通知类弹窗的分类。跟原来 MessageBoxImage 的用途一样——只影响图标和一点点配色
/// 强调（比如 Error 用 DangerBrush 强调标题），不影响弹窗结构本身。
/// </summary>
public enum XclMessageKind
{
    Info,
    Success,
    Warning,
    Error,
}

public enum XclMessageButtons
{
    OK,
    OKCancel,
    YesNo,
    YesNoCancel,
}

public enum XclMessageResult
{
    Yes,
    No,
    Cancel,
}

/// <summary>关闭按钮"下载/启动进行中"四选一提示专用结果，见 MessageBoxDialog.ShowFourChoice。</summary>
public enum XclFourChoiceResult
{
    Cancel,
    Close,
    Minimize,
    Tray,
}

public enum XclTouchCloseChoiceResult
{
    Cancel,
    CloseAllIncludingGame,
    Tray,
    CloseLauncherAndOverlayOnly,
}

/// <summary>“千万别点那按钮”专用四选一结果：取消 + 三个同名“确定”。</summary>
public enum XclDangerFourChoiceResult
{
    Cancel,
    Confirm1,
    Confirm2,
    Confirm3,
}

public partial class MessageBoxDialog : OverlayDialogControl
{
    /// <summary>用户点击的是哪个按钮（用于 YesNo/OKCancel 场景）。true=Yes/OK，
    /// false=No/Cancel，null=没有点任何按钮就关闭了（比如按 Esc/点遮罩）。</summary>
    public bool? Result { get; private set; }

    public XclMessageResult Result3 { get; private set; } = XclMessageResult.Cancel;

    /// <summary>四选一场景专用结果（目前只有关闭按钮的"下载/启动进行中"提示用到），
    /// 用法跟 Result3 一样：只有走 ShowFourChoice 弹出时才会被设置。</summary>
    public XclFourChoiceResult Result4 { get; private set; } = XclFourChoiceResult.Cancel;

    /// <summary>“千万别点那按钮”专用结果。</summary>
    public XclDangerFourChoiceResult DangerResult4 { get; private set; } = XclDangerFourChoiceResult.Cancel;

    /// <summary>触屏模式关闭确认专用结果，见 ShowTouchModeCloseChoice。</summary>
    public XclTouchCloseChoiceResult TouchCloseResult { get; private set; } = XclTouchCloseChoiceResult.Cancel;

    private MessageBoxDialog(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        InitializeComponent();

        TitleText.Text = kind == XclMessageKind.Error ? DiagnosticRedactionService.Sanitize(title, 256) : title;
        MessageText.Text = kind == XclMessageKind.Error ? DiagnosticRedactionService.Sanitize(message, 16000) : message;
        if (kind == XclMessageKind.Error)
        {
            var report = ErrorReport.Create(message, message, title);
            TitleText.Text = report.Title;
            MessageText.Text = report.Summary + "\n\n错误原因\n" + report.Cause + "\n\n处理方法\n" + report.Guidance;
            ErrorCodeText.Text = "错误代码：" + report.Code + "\n报告编号：" + report.IncidentId;
            ErrorPresenter.TryLogTechnicalDetail(report.ToText());
            ErrorCodeText.Visibility = Visibility.Visible;
            var copy = new Button
            {
                Content = "复制说明", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)FindResource("SecondaryButton")
            };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(report.ToText()); copy.Content = "已复制"; }
                catch { copy.ToolTip = "剪贴板暂时不可用，请重试。"; }
            };
            ButtonPanel.Children.Add(copy);
        }
        IconText.Text = kind switch
        {
            XclMessageKind.Success => "✔",
            XclMessageKind.Warning => "⚠",
            XclMessageKind.Error => "✖",
            _ => "ℹ",
        };
        IconText.Foreground = kind switch
        {
            XclMessageKind.Success => (System.Windows.Media.Brush)FindResource("SuccessTextBrush"),
            XclMessageKind.Warning => (System.Windows.Media.Brush)FindResource("WarningTextBrush"),
            XclMessageKind.Error => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("AccentBrush"),
        };

        BuildButtons(buttons);
    }

    private void BuildButtons(XclMessageButtons buttons)
    {
        switch (buttons)
        {
            case XclMessageButtons.OK:
                ButtonPanel.Children.Add(MakeButton("确定", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.OKCancel:
                ButtonPanel.Children.Add(MakeButton("取消", false, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton("确定", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.YesNo:
                ButtonPanel.Children.Add(MakeButton("否", false, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton("是", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.YesNoCancel:
                // 三个按钮从左到右：取消/否/是——跟原生 MessageBox 在 Windows 上的
                // 从左到右顺序（是/否/取消）刻意不同，这里沿用本项目其它 OKCancel/YesNo
                // 场景"次要操作靠左、主操作靠右且带 PrimaryButton 高亮"的既有布局习惯，
                // 保持该类内部风格统一，而不是逐字照搬系统对话框的布局。
                ButtonPanel.Children.Add(MakeButton3("取消", XclMessageResult.Cancel, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton3("否", XclMessageResult.No, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton3("是", XclMessageResult.Yes, isPrimary: true, isDefault: true));
                break;
        }
    }

    private Button MakeButton(string content, bool result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            Result = result;
            CloseWith(result);
        };
        return btn;
    }

    /// <summary>YesNoCancel 专用的按钮工厂：跟 MakeButton 的差异只在于写入 Result3
    /// （三态）而不是 Result（bool?），并且 CloseWith 统一传 null——YesNoCancel 场景下
    /// 调用方应该读 Result3，不应该继续依赖 bool? 的 IOverlayDialog.RequestClose 语义,
    /// 这里传 null 只是满足 CloseWith 的签名要求，不代表"没有选择"。</summary>
    private Button MakeButton3(string content, XclMessageResult result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            Result3 = result;
            CloseWith(null);
        };
        return btn;
    }

    // ===== 静态入口：签名尽量贴近 System.Windows.MessageBox.Show，方便替换调用点 =====

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)。</summary>
    public static void ShowInfo(string message, string title = "提示") => Show(message, title, XclMessageKind.Info, XclMessageButtons.OK);

    public static Task ShowInfoAsync(string message, string title = "提示")
        => ShowAsync(message, title, XclMessageKind.Info, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)，
    /// 专用于"操作成功完成"类通知（游戏启动成功、下载完成等），图标用 ✔ 而不是 ℹ，
    /// 视觉上更明确地传达"这是好消息"。</summary>
    public static void ShowSuccess(string message, string title = "成功") => Show(message, title, XclMessageKind.Success, XclMessageButtons.OK);

    /// <summary>ShowSuccess 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowSuccessAsync(string message, string title = "成功")
        => ShowAsync(message, title, XclMessageKind.Success, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning)。</summary>
    public static void ShowWarning(string message, string title = "提示") => Show(message, title, XclMessageKind.Warning, XclMessageButtons.OK);

    /// <summary>ShowWarning 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowWarningAsync(string message, string title = "提示")
        => ShowAsync(message, title, XclMessageKind.Warning, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error)。</summary>
    public static void ShowError(string message, string title = "出了点问题") => Show(message, title, XclMessageKind.Error, XclMessageButtons.OK);

    /// <summary>ShowError 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowErrorAsync(string message, string title = "出了点问题")
        => ShowAsync(message, title, XclMessageKind.Error, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)，
    /// 返回 true 表示用户点了"是"。</summary>
    public static bool ShowConfirm(string message, string title = "确认")
        => NativeApprovalService.ShouldUseNative ? NativeApprovalService.Confirm(message, title)
            : Show(message, title, XclMessageKind.Info, XclMessageButtons.YesNo) == true;

    public static Task<bool?> ShowConfirmAsync(string message, string title = "确认", string confirmText = "确定")
        => ShowConfirmationAsync(message, title, confirmText, defaultToCancel: false);

    /// <summary>敏感导出确认：默认选择取消，不因按回车而接受风险。</summary>
    public static Task<bool?> ShowSafetyConfirmAsync(string message, string title, string confirmText)
        => ShowConfirmationAsync(message, title, confirmText, defaultToCancel: true);

    private static Task<bool?> ShowConfirmationAsync(string message, string title, string confirmText, bool defaultToCancel)
    {
        if (NativeApprovalService.ShouldUseNative)
            return Task.FromResult<bool?>(NativeApprovalService.Confirm(message, title));
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Info, XclMessageButtons.OKCancel);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton("取消", false, isPrimary: false, isDefault: defaultToCancel));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton(confirmText, true, isPrimary: true, isDefault: !defaultToCancel));
        return OverlayDialogService.ShowModalAsync(dlg);
    }

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question)，
    /// 返回值直接对应原来的 MessageBoxResult.Yes/No/Cancel 三态，调用方原来怎么判断
    /// choice == MessageBoxResult.Xxx，现在就怎么判断 result == XclMessageResult.Xxx。</summary>
    public static XclMessageResult ShowYesNoCancel(string message, string title = "确认")
    {
        if (NativeApprovalService.ShouldUseNative)
            return NativeApprovalService.Ask(message, title, true) switch
            { 6 => XclMessageResult.Yes, 7 => XclMessageResult.No, _ => XclMessageResult.Cancel };
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Info, XclMessageButtons.YesNoCancel);
        OverlayDialogService.ShowModal(dlg);
        return dlg.Result3;
    }

    /// <summary>
    /// 三选一弹窗，按钮文案完全自定义——ShowYesNoCancel 的按钮文案固定是"取消/否/是"，
    /// 不满足"返回重新确认 / 使用基本模式 / 退出软件"这类每个按钮都需要说清楚具体动作
    /// 的场景（用"是/否/取消"根本看不出来点哪个是什么意思）。三个按钮从左到右依次对应
    /// option1（次要，靠左）/ option2（次要）/ option3（主操作，高亮，靠右，默认按钮），
    /// 布局风格跟 ShowYesNoCancel 保持一致；返回值同样复用 XclMessageResult 三态，
    /// 只是把 Cancel/No/Yes 分别重新解释成 option1/option2/option3，调用方按位置对应即可，
    /// 不需要关心枚举名字面意思跟按钮文案是否匹配。用户按 Esc/点遮罩关闭（没有点任何按钮）
    /// 时归为 Cancel（即 option1 那一档），语义上视为"什么都没选、维持现状"。
    /// </summary>
    public static XclMessageResult ShowThreeChoice(string message, string title, string option1, string option2, string option3)
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Info, XclMessageButtons.OK);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton3(option1, XclMessageResult.Cancel, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton3(option2, XclMessageResult.No, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton3(option3, XclMessageResult.Yes, isPrimary: true, isDefault: true));
        OverlayDialogService.ShowModal(dlg);
        return dlg.Result3;
    }

    /// <summary>
    /// 两按钮自定义文案确认弹窗——ShowConfirm 的按钮文案固定是"否/是"，不满足需要
    /// 明确动作描述（比如"进入/返回"）的场景。rightIsPrimary 控制哪一侧使用高亮的
    /// PrimaryButton 样式并作为默认按钮（Enter 触发）；对于"不建议进入"这类场景，
    /// 调用方应该把更安全的那个按钮（一般是"返回"）设为 primary，避免用户误按 Enter
    /// 就真的进入了有问题的功能。返回 true 表示点了右侧（rightText）按钮。
    /// </summary>
    public static bool ShowCustomYesNo(string message, string title, string leftText, string rightText,
        bool rightIsPrimary = true, XclMessageKind kind = XclMessageKind.Warning)
    {
        var dlg = new MessageBoxDialog(message, title, kind, XclMessageButtons.OK);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton(leftText, false, isPrimary: !rightIsPrimary, isDefault: !rightIsPrimary));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton(rightText, true, isPrimary: rightIsPrimary, isDefault: rightIsPrimary));
        return OverlayDialogService.ShowModal(dlg) == true;
    }

    /// <summary>"返回并取消 / 我已理解并同意"两键确认弹窗：只有点「我已理解并同意」才返回 true，
    /// 点返回、按 Esc 都返回 false。「返回并取消」是默认按钮，避免误按 Enter 就同意。</summary>
    public static bool ShowAgreeOrBack(string message, string title)
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OK);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton("返回并取消", false, isPrimary: true, isDefault: true));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton("我已理解并同意", true, isPrimary: false, isDefault: false));
        return OverlayDialogService.ShowModal(dlg) == true;
    }

    /// <summary>红色危险确认弹窗：标题图标、正文用危险红色，"确认"按钮为红底白字；
    /// 默认焦点在"取消"上，避免只按 Enter 就误确认。返回 true 表示点了确认。</summary>
    public static bool ShowDangerConfirm(string message, string title, string cancelText = "取消", string confirmText = "我已了解风险，继续")
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OK);
        dlg.MessageText.Foreground = (System.Windows.Media.Brush)dlg.FindResource("DangerBrush");
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton(cancelText, false, isPrimary: false, isDefault: true));
        var confirm = dlg.MakeButton(confirmText, true, isPrimary: true, isDefault: false);
        var danger = (System.Windows.Media.Brush)dlg.FindResource("DangerBrush");
        confirm.Background = danger;
        confirm.BorderBrush = danger;
        confirm.Foreground = System.Windows.Media.Brushes.White;
        dlg.ButtonPanel.Children.Add(confirm);
        return OverlayDialogService.ShowModal(dlg) == true;
    }

    /// <summary>四选一专用按钮工厂，用法同 MakeButton3。</summary>
    private Button MakeButton4(string content, XclFourChoiceResult result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(6, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            Result4 = result;
            CloseWith(null);
        };
        return btn;
    }

    /// <summary>关闭按钮"下载/启动进行中"专用四选一提示：取消 / 关闭 / 最小化 / 返回任务栏托盘。
    /// 用户按 Esc/点遮罩关闭时归为 Cancel（什么都不做，维持窗口原样，跟点"取消"等价）。</summary>
    public static XclFourChoiceResult ShowFourChoice(string message, string title,
        string cancelText, string closeText, string minimizeText, string trayText)
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OK);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeButton4(cancelText, XclFourChoiceResult.Cancel, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton4(trayText, XclFourChoiceResult.Tray, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton4(minimizeText, XclFourChoiceResult.Minimize, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeButton4(closeText, XclFourChoiceResult.Close, isPrimary: true, isDefault: true));
        OverlayDialogService.ShowModal(dlg);
        return dlg.Result4;
    }

    /// <summary>触屏模式下点关闭按钮的专用四选一提示：正在用触屏模式玩游戏时关闭启动器，
    /// 悬浮层会跟着启动器一起消失，玩家手上一秒还能戳的虚拟按键下一秒就没了——这跟平时
    /// "点叉号无非是关不关游戏"的心理预期差得比较远，所以单独截住，不走 DefaultCloseAction
    /// 那套默认行为，每次都明确问一遍。
    ///
    /// 四个选项对应四种完全不同的后果：
    /// 取消 —— 什么都不做，回到刚才的状态。
    /// 关闭所有(包括游戏) —— 启动器、悬浮层、游戏进程一起结束，最干净的退出方式。
    /// 返回托盘 —— 启动器、悬浮层、游戏全部继续在后台/前台跑，只是把主窗口藏进系统托盘，
    ///           触屏操作不受任何影响。
    /// 关闭XCL和虚拟按键 —— 只结束启动器进程和悬浮层，游戏本身继续单独运行；代价是虚拟按键
    ///           消失了，触屏没法再操作，只有接了实体键鼠才能继续玩。</summary>
    public static XclTouchCloseChoiceResult ShowTouchModeCloseChoice(string message, string title,
        string cancelText, string closeAllText, string trayText, string closeLauncherOnlyText)
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OK);
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeTouchCloseButton(cancelText, XclTouchCloseChoiceResult.Cancel, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeTouchCloseButton(closeLauncherOnlyText, XclTouchCloseChoiceResult.CloseLauncherAndOverlayOnly, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeTouchCloseButton(trayText, XclTouchCloseChoiceResult.Tray, isPrimary: false, isDefault: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeTouchCloseButton(closeAllText, XclTouchCloseChoiceResult.CloseAllIncludingGame, isPrimary: true, isDefault: true));
        OverlayDialogService.ShowModal(dlg);
        return dlg.TouchCloseResult;
    }

    private Button MakeTouchCloseButton(string content, XclTouchCloseChoiceResult result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(6, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            TouchCloseResult = result;
            CloseWith(null);
        };
        return btn;
    }

    /// <summary>“千万别点那按钮”专用四选一风险提示。前三个按钮（取消、确定、确定）
    /// 使用普通次要按钮样式，最后一个“确定”使用危险红色；提示正文也使用危险红色。
    /// 四个按钮都不设默认按钮，避免用户只按 Enter 就误触。
    /// </summary>
    public static XclDangerFourChoiceResult ShowDangerFourChoice(string message, string title,
        string cancelText = "取消", string confirm1Text = "确定", string confirm2Text = "确定", string confirm3Text = "确定")
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OK);
        dlg.MessageText.Foreground = (System.Windows.Media.Brush)dlg.FindResource("DangerBrush");
        dlg.ButtonPanel.Children.Clear();
        dlg.ButtonPanel.Children.Add(dlg.MakeDangerChoiceButton(cancelText, XclDangerFourChoiceResult.Cancel, isDanger: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeDangerChoiceButton(confirm1Text, XclDangerFourChoiceResult.Confirm1, isDanger: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeDangerChoiceButton(confirm2Text, XclDangerFourChoiceResult.Confirm2, isDanger: false));
        dlg.ButtonPanel.Children.Add(dlg.MakeDangerChoiceButton(confirm3Text, XclDangerFourChoiceResult.Confirm3, isDanger: true));
        OverlayDialogService.ShowModal(dlg);
        return dlg.DangerResult4;
    }

    private Button MakeDangerChoiceButton(string content, XclDangerFourChoiceResult result, bool isDanger)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(6, 0, 0, 0),
            IsDefault = false,
            Style = (Style)FindResource(isDanger ? "PrimaryButton" : "SecondaryButton"),
        };
        if (isDanger)
        {
            var danger = (System.Windows.Media.Brush)FindResource("DangerBrush");
            btn.Background = danger;
            btn.BorderBrush = danger;
            btn.Foreground = System.Windows.Media.Brushes.White;
        }
        btn.Click += (_, _) =>
        {
            DangerResult4 = result;
            CloseWith(null);
        };
        return btn;
    }

    /// <summary>最通用的入口，其它 ShowXxx 静态方法都是对该的语义化包装。</summary>
    public static bool? Show(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        var dlg = new MessageBoxDialog(message, title, kind, buttons);
        return OverlayDialogService.ShowModal(dlg);
    }

    public static Task ShowAsync(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        var dlg = new MessageBoxDialog(message, title, kind, buttons);
        return OverlayDialogService.ShowModalAsync(dlg);
    }

    /// <summary>
    /// 限时确认弹窗：跟 ShowConfirm 语义一样（true=用户点了"确定"），但多了一个
    /// "用户不搭理就自动按默认选项处理"的行为——专门给"要不要联网下载万能皮肤补丁"这类
    /// 场景用：这不是一个必须做的操作（不下载也能启动游戏，只是没有自定义皮肤/皮肤站校验），
    /// 不应该无限期卡住整个启动流程等用户点一下。
    /// timeoutSeconds 秒之后如果用户还没点任何按钮，自动按 defaultResult 关闭弹窗，
    /// 效果等价于用户自己点了对应的按钮；倒计时数字实时显示在消息文本末尾，让用户清楚看到
    /// "再不选就会用默认选项"，不是静默在后台倒数。用户在倒计时结束前正常点击 确定/取消/
    /// Esc/点遮罩 都会立即按各自的语义关闭，不用等满 timeoutSeconds。</summary>
    public static async Task<bool> ShowConfirmWithTimeoutAsync(string message, string title, int timeoutSeconds, bool defaultResult)
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Warning, XclMessageButtons.OKCancel);
        var remaining = timeoutSeconds;
        string ComposeText() => $"{message}\n\n（{remaining} 秒内未选择，将自动按“{(defaultResult ? "确定" : "取消")}”处理）";
        dlg.MessageText.Text = ComposeText();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                dlg.Result = defaultResult;
                dlg.CloseWith(defaultResult); // 等价于用户自己点了默认对应的那个按钮
            }
            else
            {
                dlg.MessageText.Text = ComposeText();
            }
        };
        timer.Start();
        try
        {
            var result = await OverlayDialogService.ShowModalAsync(dlg);
            // 用户主动按 Esc/点遮罩关闭（没点任何按钮）时 result 是 null，同样落到默认选项，
            // 跟"用户没有明确选择"这件事在语义上是一致的，不需要单独判断。
            return result ?? defaultResult;
        }
        finally
        {
            timer.Stop();
        }
    }
}
