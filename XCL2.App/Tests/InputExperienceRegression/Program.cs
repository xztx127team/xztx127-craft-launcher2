using System.Globalization;
using XCL2.App.Services;

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
foreach (var culture in new[] { "en-US", "zh-CN", "de-DE" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    Check(InputExperiencePolicy.TryParseMemoryMb("2GB", true, out var value) && value == 2048, culture + " GB");
    Check(InputExperiencePolicy.TryParseMemoryMb("1.5GB", true, out value) && value == 1536, culture + " exact fractional GB");
    Check(InputExperiencePolicy.TryParseMemoryMb("500", true, out value) && value == 500, culture + " legacy MB");
    Check(!InputExperiencePolicy.TryParseMemoryMb("1.1GB", true, out _), "no silent fractional MB rounding");
    Check(!InputExperiencePolicy.TryParseMemoryMb("2GB", false, out _), "unit setting is honored");
    foreach (var invalid in new[] { "", "0", "-1GB", "2TB", "NaN", "Infinity", "1; shutdown", "9999999999999999999999999999GB" })
        Check(!InputExperiencePolicy.TryParseMemoryMb(invalid, true, out _), "reject " + invalid);
}
Check(InputExperiencePolicy.FirstInvalidFileNameIndex("生存世界") == -1, "unicode file name");
Check(InputExperiencePolicy.FirstInvalidFileNameIndex("abc:def") == 3, "exact bad position");
Check(InputExperiencePolicy.FirstInvalidFileNameIndex("CON.txt") == 0, "reserved device name");
Check(InputExperiencePolicy.FirstInvalidFileNameIndex("world.") == 5, "trailing dot");
Check(InputExperiencePolicy.FirstInvalidFileNameIndex("world ") == 5, "trailing space");
Check(InputExperiencePolicy.JoinPastedLines("alpha\r\n beta\rgamma\n") == "alpha beta gamma ", "visible line joining policy");

Check(InputExperiencePolicy.TryValidateMemoryPercentRange("5", "70", out var percentMin, out var percentMax) && percentMin == 5 && percentMax == 70, "valid dynamic percentages");
foreach (var pair in new[] { ("abc", "70"), ("NaN", "70"), ("5", "Infinity"), ("0", "70"), ("5", "101"), ("80", "20"), ("", "70") })
    Check(!InputExperiencePolicy.TryValidateMemoryPercentRange(pair.Item1, pair.Item2, out _, out _), "invalid percentage range rejected before save: " + pair);
Check(InputExperiencePolicy.TryValidateMemoryPercentRange("0.1", "100", out _, out _), "documented percent boundaries supported");
Console.WriteLine("Input policies passed: unit conversion, culture, overflow, invalid names, explicit line joining.");
