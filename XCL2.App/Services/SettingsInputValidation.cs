using System.Globalization;

namespace XCL2.App.Services;

/// <summary>Checks only; never repairs the user's draft or adds undocumented limits.</summary>
public static class SettingsInputValidation
{
    public static bool TryNumber(string? text, double minimum, double maximum, bool integer, out double value)
    {
        if (integer)
        {
            var isInteger = int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var whole)
                || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole);
            value = whole;
            return isInteger && value >= minimum && value <= maximum;
        }
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return parsed && double.IsFinite(value) && value >= minimum && value <= maximum
            && (!integer || Math.Truncate(value) == value);
    }

    public static bool TryClock(string? text, out string normalized)
    {
        normalized = "";
        if (!TimeOnly.TryParseExact(text?.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return false;
        normalized = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return true;
    }

    public static IReadOnlyDictionary<string, string> ValidateMemory(bool dynamic, bool percentage, bool units,
        string manualMin, string manualMax, string dynamicMin, string dynamicMax, string percentMin, string percentMax, string fixedMb)
    {
        var errors = new Dictionary<string, string>();
        void CheckMb(string key, string text)
        {
            if (!InputExperiencePolicy.TryParseMemoryMb(text, units, out _))
                errors[key] = units ? "请输入正整数 MB，或可换算为完整 MB 的容量（例如 2048MB、2GB）。" : "请输入大于 0 的整数，单位 MB。";
        }
        if (dynamic && !string.IsNullOrWhiteSpace(fixedMb))
        {
            // A fixed override is the sole active allocation; inactive old ranges must not block saving it.
            CheckMb("FixedMemoryMbBox", fixedMb);
            return errors;
        }
        if (dynamic && percentage)
        {
            if (!InputExperiencePolicy.TryParseMemoryPercent(percentMin, out var min)) errors["DynamicMinPercentBox"] = "请输入 0.1–100 之间的百分比。";
            if (!InputExperiencePolicy.TryParseMemoryPercent(percentMax, out var max)) errors["DynamicMaxPercentBox"] = "请输入 0.1–100 之间的百分比。";
            if (errors.Count == 0 && min > max) errors["DynamicMinPercentBox"] = errors["DynamicMaxPercentBox"] = "最低百分比不能大于最高百分比。";
            return errors;
        }
        var minKey = dynamic ? "DynamicMinMbBox" : "MinMemBox";
        var maxKey = dynamic ? "DynamicMaxMbBox" : "MaxMemBox";
        var minText = dynamic ? dynamicMin : manualMin;
        var maxText = dynamic ? dynamicMax : manualMax;
        CheckMb(minKey, minText); CheckMb(maxKey, maxText);
        if (errors.Count == 0 && InputExperiencePolicy.TryParseMemoryMb(minText, units, out var lower)
            && InputExperiencePolicy.TryParseMemoryMb(maxText, units, out var upper) && lower > upper)
            errors[minKey] = errors[maxKey] = "最低内存不能大于最高内存。";
        return errors;
    }
}
