namespace XCL2.App.Models;

public partial class AppConfig
{
    public bool DownloadGamesContinuousLoading { get; set; } = true;
    public bool DownloadResourcesContinuousLoading { get; set; }
    public int DownloadListBatchSize { get; set; } = 20;
}
