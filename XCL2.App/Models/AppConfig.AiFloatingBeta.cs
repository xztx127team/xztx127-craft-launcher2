namespace XCL2.App.Models;

public enum AiFloatingBallStyle { Circle, RoundedSquare, Outline }
public enum AiFloatingBallScope { Launcher, Desktop }

/// <summary>Beta 悬浮入口单独保存外观及会话选择，不覆盖普通 AI 页的工作模式。</summary>
public sealed class AiFloatingBetaSettings
{
    public bool Enabled { get; set; }
    public AiFloatingBallStyle Style { get; set; } = AiFloatingBallStyle.Circle;
    public AiFloatingBallScope Scope { get; set; } = AiFloatingBallScope.Launcher;
    public bool BallTopmost { get; set; } = true;
    public bool ChatTopmost { get; set; } = true;
    public string Color { get; set; } = "#3B82F6";
    public string ImagePath { get; set; } = "";
    public AiWorkMode WorkMode { get; set; } = AiWorkMode.Build;
    public AiRoutingMode RoutingMode { get; set; } = AiRoutingMode.Auto;
    public string SelectedModel { get; set; } = "";
    public string SessionId { get; set; } = "";
    public double? DesktopLeft { get; set; }
    public double? DesktopTop { get; set; }

    public AiFloatingBetaSettings Copy() => (AiFloatingBetaSettings)MemberwiseClone();
}

public partial class AppConfig
{
    public AiFloatingBetaSettings AiFloatingBeta { get; set; } = new();
}
