using System.Windows;
using System.Windows.Media.Animation;

namespace XCL2.App.Services;

/// <summary>像素列宽动画；终值由调用者写入基值，时钟结束后不持有布局属性。</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public double From { get; set; }
    public double To { get; set; }
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation { From = From, To = To };

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 1;
        var eased = 1 - Math.Pow(1 - progress, 3);
        return new GridLength(Math.Max(0, From + (To - From) * eased), GridUnitType.Pixel);
    }
}
