using System.Windows;
using System.Windows.Data;

namespace XCL2.App.Services;

/// <summary>Temporary preview layout values, restored with their original
/// dependency-property precedence (unset/local/binding) when preview ends.</summary>
internal sealed class PreviewLayoutOverrides
{
    private sealed record SavedValue(object LocalValue, BindingBase? Binding);
    private readonly Dictionary<(DependencyObject Element, DependencyProperty Property), SavedValue> _values = new();

    internal bool TrySet(DependencyObject element, DependencyProperty property, object value)
    {
        var key = (element, property);
        if (!_values.ContainsKey(key))
        {
            var local = element.ReadLocalValue(property);
            var binding = BindingOperations.GetBindingBase(element, property);
            // Layout values are structs. An opaque DynamicResource expression
            // cannot be recreated through SetValue, so retain its explicit owner.
            if (local != DependencyProperty.UnsetValue && local is not ValueType && binding == null) return false;
            _values[key] = new SavedValue(local, binding);
        }
        element.SetValue(property, value);
        return true;
    }

    internal void Restore()
    {
        foreach (var entry in _values)
        {
            if (entry.Value.Binding != null)
                BindingOperations.SetBinding(entry.Key.Element, entry.Key.Property, entry.Value.Binding);
            else if (entry.Value.LocalValue == DependencyProperty.UnsetValue)
                entry.Key.Element.ClearValue(entry.Key.Property);
            else entry.Key.Element.SetValue(entry.Key.Property, entry.Value.LocalValue);
        }
        _values.Clear();
    }
}
