using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>Defers searches only while an IME composition owns the text. Does not rewrite input.</summary>
public sealed class SearchCompositionGate
{
    private readonly TextBox _box;
    private readonly Func<bool> _enabled;
    private readonly Action _completed;
    private bool _composing;
    private int _generation;
    public bool IsComposing => _enabled() && _composing;
    public SearchCompositionGate(TextBox box, Func<bool> enabled, Action started, Action completed)
    {
        _box = box; _enabled = enabled; _completed = completed;
        TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, _) => { _composing = true; ++_generation; if (_enabled()) started(); });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(box, (_, _) => { _composing = true; ++_generation; });
        TextCompositionManager.AddPreviewTextInputHandler(box, (_, _) => CompleteAfterInput());
        box.LostKeyboardFocus += (_, _) => CompleteAfterInput();
        box.Unloaded += (_, _) => { _composing = false; ++_generation; };
    }
    private void CompleteAfterInput()
    {
        if (!_composing) return;
        int generation = ++_generation;
        _ = _box.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (generation != _generation) return;
            _composing = false;
            if (_enabled()) _completed();
        }));
    }
}
