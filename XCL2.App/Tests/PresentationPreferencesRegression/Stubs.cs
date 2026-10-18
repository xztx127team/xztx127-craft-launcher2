namespace XCL2.App.Models
{
    public partial class AppConfig
    {
        public bool Ux50FullTimeTooltips { get; set; } = true;
        public bool Ux50UniformNumberSpacing { get; set; } = true;
        public double FeatureSearchWidth { get; set; } = 300;
        public double FeatureSearchHeight { get; set; } = 36;
        public double FeatureSearchOffsetX { get; set; }
        public double FeatureSearchOffsetY { get; set; }
        public string SelectedVersionId { get; set; } = "keep-instance";
        public bool AllowCommands { get; set; }
    }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; }
        public XCL2.App.Models.AppConfig Config { get; } = new();
    }
}
