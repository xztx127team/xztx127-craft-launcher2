# 独立窗口触摸板会话回归

运行（.NET 8 SDK，不要求 Windows）：

```powershell
dotnet run --project .\Tests\WindowTouchOverlayRegression
```

本项目链接真实 `WindowTouchOverlayService`、输入路径策略和悬浮层接口，只替换原生窗口查询、WPF dispatcher 和悬浮层 UI 边界。它检查同进程多个窗口的隔离、重复开启、排队取消、失败重试、关闭/复用 HWND、旧关闭回调、新请求、后台关闭、dispatcher 关闭竞态与旧请求取消、总清理和输入路径选择。

测试中的目标关闭由替身模拟现有悬浮层的轮询回调；本测试不能证明真实 Win32/WPF 行为、触摸驱动、子窗口鼠标消息、DPI 定位或权限隔离正常。仍需在 Windows 上完成普通应用和游戏的实际触摸验收。

本次编辑环境没有 .NET SDK，未执行此项目；这份说明不代表测试已通过。
