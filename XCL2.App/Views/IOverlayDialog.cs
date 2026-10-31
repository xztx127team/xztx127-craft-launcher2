using System.Windows.Controls;

namespace XCL2.App.Views;

public interface IOverlayDialog
{
    /// <summary>弹窗请求关闭时触发，携带的 bool? 语义等价于原 Window.DialogResult：
    /// true=确定，false=取消，null=无结果关闭（比如按 Esc 或点遮罩）。</summary>
    event EventHandler<bool?>? RequestClose;
}

/// <summary>可选的用户关闭保护。Esc / 遮罩交给弹窗确认，由弹窗自行发出 RequestClose。</summary>
public interface IOverlayDialogDismissGuard
{
    void RequestDismiss();
}

/// <summary>文件操作尚未完成或清理时，暂缓宿主退出，避免进程结束中断写入。</summary>
public interface IOverlayHostCloseGuard
{
    bool IsHostCloseBlocked { get; }
}

/// <summary>宿主已结束此层的通知，包含 Esc、背景、宿主退出和代码关闭；与请求关闭分开。</summary>
public interface IOverlayDialogLifecycle
{
    void OnOverlayClosed(bool? result);
}

/// <summary>
/// <see cref="IOverlayDialog"/> 的一个方便基类：大多数迁移后的弹窗直接继承该，
/// 只需要在"确定/取消"按钮的 Click 里调用 <see cref="CloseWith"/> 即可，
/// 不用自己声明和触发事件。
/// </summary>
public class OverlayDialogControl : UserControl, IOverlayDialog, IOverlayDialogLifecycle
{
    public OverlayDialogControl() => SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

    public event EventHandler<bool?>? RequestClose;
    public event EventHandler<bool?>? OverlayClosed;

    void IOverlayDialogLifecycle.OnOverlayClosed(bool? result)
    {
        if (OverlayClosed == null) return;
        foreach (EventHandler<bool?> handler in OverlayClosed.GetInvocationList())
        {
            try { handler(this, result); }
            catch (Exception ex) { XCL2.App.Services.ErrorPresenter.LogFallback("清理弹窗关闭订阅失败", ex); }
        }
    }

    /// <summary>请求宿主关闭当前弹窗并带上结果，等价于原来的
    /// "DialogResult = xxx; Close();"两行。</summary>
    protected void CloseWith(bool? result) => RequestClose?.Invoke(this, result);

    public bool? ShowDialog() => OverlayDialogService.ShowModal(this);

    /// <summary>非模态显示，等价于原 Window.Show()。</summary>
    public void Show() => OverlayDialogService.ShowNonModal(this);

    /// <summary>关闭自己，等价于原 Window.Close()（结果为 null）。</summary>
    public void Close() => RequestClose?.Invoke(this, null);
}
