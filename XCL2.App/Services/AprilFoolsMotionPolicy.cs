namespace XCL2.App.Services;

/// <summary>纯几何策略，坐标单位由调用方统一；不依赖 WPF/显示器或真实鼠标。</summary>
public static class AprilFoolsMotionPolicy
{
    public readonly record struct Point(double X, double Y);
    public readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }
    public static double Distance(Point point, Box box)
    {
        var dx = Math.Max(Math.Max(box.X - point.X, 0), point.X - box.Right);
        var dy = Math.Max(Math.Max(box.Y - point.Y, 0), point.Y - box.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
    public static Box Fit(Box target, Box area)
    {
        var width = Math.Min(Math.Max(0, target.Width), Math.Max(0, area.Width));
        var height = Math.Min(Math.Max(0, target.Height), Math.Max(0, area.Height));
        return new(Math.Clamp(target.X, area.X, area.X + Math.Max(0, area.Width - width)),
            Math.Clamp(target.Y, area.Y, area.Y + Math.Max(0, area.Height - height)), width, height);
    }
    /// <summary>挑选满足避让距离的可见位置；空间不够时挑选离鼠标最远的位置。</summary>
    public static Box Evade(Box current, Box area, Point mouse, double gap, Random random)
    {
        var fitted = Fit(current, area);
        var best = fitted;
        var distance = Distance(mouse, best);
        var bestGoodScore = double.NegativeInfinity;
        for (var i = 0; i < 52; i++)
        {
            var x = i < 4 ? (i % 2 == 0 ? 0d : 1d) : random.NextDouble();
            var y = i < 4 ? (i < 2 ? 0d : 1d) : random.NextDouble();
            var candidate = new Box(area.X + x * Math.Max(0, area.Width - fitted.Width),
                area.Y + y * Math.Max(0, area.Height - fitted.Height), fitted.Width, fitted.Height);
            var away = Distance(mouse, candidate);
            if (away >= gap)
            {
                var score = away * 1000 + Math.Abs(candidate.X - current.X) + Math.Abs(candidate.Y - current.Y);
                if (score > bestGoodScore) { bestGoodScore = score; best = candidate; }
            }
            else if (bestGoodScore == double.NegativeInfinity && away > distance)
            {
                best = candidate;
                distance = away;
            }
        }
        return best;
    }
}
