namespace XCL2.App.Models;

public partial class AppConfig
{
    /// <summary>下载管理容量及传输速度的显示单位；自动沿用启动器现有容量显示偏好。</summary>
    public DownloadDisplayUnit DownloadDisplayUnit { get; set; } = DownloadDisplayUnit.Automatic;
}
