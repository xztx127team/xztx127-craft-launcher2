namespace XCL2.App.Models;

/// <summary>Explicit allowlist for reversible experience preferences. Never applied to accounts, permissions or task payloads.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExperiencePreferenceAttribute(string group, string label) : Attribute
{
    public string Group { get; } = group;
    public string Label { get; } = label;
    public string Description { get; set; } = "";
    public double Minimum { get; set; } = double.NaN;
    public double Maximum { get; set; } = double.NaN;
    public string Unit { get; set; } = "";
}
