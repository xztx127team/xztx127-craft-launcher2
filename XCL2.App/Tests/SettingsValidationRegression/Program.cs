using System.Globalization;
using XCL2.App.Services;

var checks = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
foreach (var culture in new[] { "zh-CN", "en-US", "de-DE" })
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
    Check(SettingsInputValidation.TryNumber("0", 0, 100, true, out var zero) && zero == 0, "Documented zero sentinel remains valid");
    Check(SettingsInputValidation.TryNumber("1.5", .5, 2, false, out var decimalValue) && decimalValue == 1.5, "Invariant decimal remains accepted");
    Check(!SettingsInputValidation.TryNumber("1.5", 1, 64, true, out _), "Fractional thread count is rejected");
    Check(!SettingsInputValidation.TryNumber("NaN", 0, 100, false, out _), "NaN is rejected");
    Check(!SettingsInputValidation.TryNumber("Infinity", 0, 100, false, out _), "Infinity is rejected");
    Check(!SettingsInputValidation.TryNumber("101", 0, 100, true, out _), "Out of range rejected without clamping");
    Check(!SettingsInputValidation.TryNumber("1,5", 1, 64, true, out _), "Fractional comma input is not accepted as an integer");
}
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Check(SettingsInputValidation.TryNumber("-10000", -10000, 10000, false, out var left) && left == -10000, "Search offset lower boundary is valid");
Check(SettingsInputValidation.TryNumber("10000", -10000, 10000, false, out var right) && right == 10000, "Search offset upper boundary is valid");
Check(!SettingsInputValidation.TryNumber("10000.01", -10000, 10000, false, out _), "Search offset over limit is rejected instead of silently clamped");
Check(!SettingsInputValidation.TryNumber("invalid", -10000, 10000, false, out _), "Search offset invalid draft is rejected instead of saving an earlier preview");
Check(SettingsInputValidation.TryClock("8:30", out var clock) && clock == "08:30", "Clock accepts one-digit hour");
Check(SettingsInputValidation.TryClock("23:59", out _), "Last minute valid");
Check(!SettingsInputValidation.TryClock("24:00", out _), "Invalid clock rejected");
Check(!SettingsInputValidation.TryClock("01:90", out _), "Invalid minute rejected");
var validManual = SettingsInputValidation.ValidateMemory(false, false, true, "2GB", "4096", "bad", "bad", "bad", "bad", "bad");
Check(validManual.Count == 0, "Inactive dynamic fields cannot block manual allocation");
var fixedOverride = SettingsInputValidation.ValidateMemory(true, true, true, "bad", "bad", "bad", "bad", "500", "0", "2GB");
Check(fixedOverride.Count == 0, "Valid fixed override ignores inactive ranges and percentages");
var percentMode = SettingsInputValidation.ValidateMemory(true, true, false, "bad", "bad", "bad", "bad", "5", "70", "");
Check(percentMode.Count == 0, "Valid percentage mode ignores inactive MB fields");
var reversed = SettingsInputValidation.ValidateMemory(false, false, true, "4GB", "2GB", "", "", "", "", "");
Check(reversed.Count == 2 && reversed.ContainsKey("MinMemBox") && reversed.ContainsKey("MaxMemBox"), "Reversed bounds mark both active fields");
var invalidFixed = SettingsInputValidation.ValidateMemory(true, false, false, "", "", "500", "8192", "5", "70", "abc");
Check(invalidFixed.Count == 1 && invalidFixed.ContainsKey("FixedMemoryMbBox"), "Invalid override marks exactly its input");
var categories = new[]
{
    ("HttpRequestException: 401 Unauthorized", "AUTH-001"),
    ("HTTP 429 Too Many Requests", "NET-004"),
    ("System.UnauthorizedAccessException: access is denied", "FILE-001"),
    ("SocketException: No such host", "NET-001"),
    ("download timed out", "NET-002"),
    ("TLS certificate validation failed", "NET-003"),
    ("FileNotFoundException", "FILE-002"),
    ("System.Text.Json.JsonException", "DATA-001"),
    ("java.lang.OutOfMemoryError", "JVM-004"),
    ("UnsupportedClassVersionError java", "JVM-002"),
    ("mod dependency missing", "MOD-001"),
    ("HTTP 500", "NET-006"),
    ("IOException disk full", "FILE-003"),
    ("unknown operation failed", "APP-999")
};
foreach (var (evidence, expected) in categories)
{
    var (category, guidance) = ErrorReport.Classify(evidence);
    Check(category == expected && !string.IsNullOrWhiteSpace(guidance), expected + " gets precise category and next steps");
}
var a = ErrorReport.Create("网络请求失败", "HTTP 500\nAuthorization: Bearer my_secret_value\nC:\\Users\\PrivatePerson\\a.txt", "请求失败");
var b = ErrorReport.Create("网络请求失败", "HTTP 500", "请求失败");
Check(a.CategoryCode == b.CategoryCode && a.Code != b.Code, "The category is stable while distinct evidence has its own fingerprint");
Check(a.IncidentId != b.IncidentId, "Every occurrence has a different report ID");
Check(!a.ToText().Contains("my_secret_value") && !a.ToText().Contains("PrivatePerson"), "Copy/export report removes credentials and user paths");
Check(a.ToText().Contains(a.IncidentId) && a.ToText().Contains(a.Code) && a.ToText().Contains("实际结果"), "Report includes correlation and reproducibility guidance");
Console.WriteLine($"Settings validation and error reports: {checks} checks passed.");
