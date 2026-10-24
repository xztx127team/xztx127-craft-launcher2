using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private NetworkDebugService? _networkDebug;
    private readonly ConcurrentQueue<NetworkDebugEntry> _networkDebugQueue = new();
    private readonly Dictionary<long, (Border Card, DateTime Seen)> _networkDebugCards = new();
    private DispatcherTimer? _networkDebugTimer;

    private void InitializeNetworkDebug()
    {
        _networkDebug = new NetworkDebugService();
        _networkDebug.Entry += entry =>
        {
            // Do not enqueue an unbounded dispatcher operation for every asset request.
            _networkDebugQueue.Enqueue(entry);
            while (_networkDebugQueue.Count > 100) _networkDebugQueue.TryDequeue(out _);
        };
        _networkDebugTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _networkDebugTimer.Tick += (_, _) => RenderNetworkDebug();
        ApplyNetworkDebugPreference();
        Closed += (_, _) => { _networkDebugTimer.Stop(); _networkDebug.Dispose(); };
    }

    private void ApplyNetworkDebugPreference()
    {
        if (_networkDebug == null || _networkDebugTimer == null) return;
        _networkDebug.Enabled = ConfigService.Config.NetworkDebugEnabled || App.StartupArgs.DebugMode;
        if (_networkDebug.Enabled) _networkDebugTimer.Start();
        else
        {
            _networkDebugTimer.Stop();
            _networkDebugQueue.Clear();
            foreach (var item in _networkDebugCards.Values) ActionToastHost.Items.Remove(item.Card);
            _networkDebugCards.Clear();
        }
    }

    private void RenderNetworkDebug()
    {
        while (_networkDebugQueue.TryDequeue(out var entry))
        {
            if (!_networkDebugCards.TryGetValue(entry.Id, out var existing))
            {
                while (_networkDebugCards.Count >= 4)
                {
                    var oldest = _networkDebugCards.OrderBy(x => x.Value.Seen).First();
                    ActionToastHost.Items.Remove(oldest.Value.Card); _networkDebugCards.Remove(oldest.Key);
                }
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var card = new Border { Child = text, MaxWidth = 330, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 5), BorderThickness = new Thickness(1), IsHitTestVisible = false };
                card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                existing = (card, DateTime.UtcNow);
                ActionToastHost.Items.Add(card);
            }
            ((TextBlock)existing.Card.Child).Text = entry.Summary;
            existing.Card.SetResourceReference(Border.BorderBrushProperty, entry.Success == false ? "DangerBrush" : "AccentBrush");
            _networkDebugCards[entry.Id] = (existing.Card, DateTime.UtcNow);
        }
        foreach (var item in _networkDebugCards.Where(x => DateTime.UtcNow - x.Value.Seen > TimeSpan.FromSeconds(6)).ToArray())
        {
            ActionToastHost.Items.Remove(item.Value.Card); _networkDebugCards.Remove(item.Key);
        }
    }
}
