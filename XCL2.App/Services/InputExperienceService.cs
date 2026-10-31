using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public enum ExperienceInputRole { Auto, PlainText, Path, FileName, Address, Memory, Sensitive, Command }

/// <summary>Scoped input conveniences. No hooks outside WPF; never runs dropped files or reads the clipboard.</summary>
public static class InputExperienceService
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached("Role", typeof(ExperienceInputRole), typeof(InputExperienceService), new PropertyMetadata(ExperienceInputRole.Auto));
    public static readonly DependencyProperty IsAuxiliaryProperty = DependencyProperty.RegisterAttached("IsAuxiliary", typeof(bool), typeof(InputExperienceService), new PropertyMetadata(false));
    public static void SetRole(DependencyObject value, ExperienceInputRole role) => value.SetValue(RoleProperty, role);
    public static ExperienceInputRole GetRole(DependencyObject value) => (ExperienceInputRole)value.GetValue(RoleProperty);
    public static void SetIsAuxiliary(DependencyObject value, bool auxiliary) => value.SetValue(IsAuxiliaryProperty, auxiliary);
    public static bool GetIsAuxiliary(DependencyObject value) => (bool)value.GetValue(IsAuxiliaryProperty);
    private sealed class State
    {
        public string FocusText = "";
        public object? OriginalTooltip;
        public string OriginalHelp = "";
        public bool UnloadedHookInstalled;
        public bool HintActive;
        public SecretRevealAdorner? Secret;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    private static bool _initialized;
    private static AppConfig? Config => ConfigService.Active?.Config;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(TextLoaded), true);
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(TextFocused), true);
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(TextKey), true);
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.TextChangedEvent, new TextChangedEventHandler(TextChanged), true);
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewDragOverEvent, new DragEventHandler(PathDrag), true);
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewDropEvent, new DragEventHandler(PathDrop), true);
        EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(PasswordLoaded), true);
    }

    private static ExperienceInputRole ResolveRole(TextBox box)
    {
        var explicitRole = GetRole(box);
        if (explicitRole != ExperienceInputRole.Auto) return explicitRole;
        if (HasSensitiveAncestor(box)) return ExperienceInputRole.Sensitive;
        var name = box.Name;
        if (new[] { "Password", "Secret", "Token", "KeyBox", "Command", "Shell", "Script", "Code", "Json", "Prompt", "Args", "Argument" }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return ExperienceInputRole.Sensitive;
        if (name.Contains("Search", StringComparison.OrdinalIgnoreCase)) return ExperienceInputRole.PlainText;
        if (name.Contains("Path", StringComparison.OrdinalIgnoreCase) || name.Contains("Directory", StringComparison.OrdinalIgnoreCase) || name.EndsWith("DirBox", StringComparison.OrdinalIgnoreCase)) return ExperienceInputRole.Path;
        if (name.Contains("Address", StringComparison.OrdinalIgnoreCase) || name.Contains("Url", StringComparison.OrdinalIgnoreCase) || (name is "AuthServerRootBox" or "PingHostBox")) return ExperienceInputRole.Address;
        if (name.Contains("FileName", StringComparison.OrdinalIgnoreCase) || name == "InstanceNameBox" || (name == "NameBox" && HasAncestor(box, "NewInstanceNameDialog"))) return ExperienceInputRole.FileName;
        // Only the settings page is a known ordinary editable surface. Unknown/unnamed
        // fields may contain chat, scripts, command cells or credentials: preserve raw input.
        return HasAncestor(box, "SettingsPage") ? ExperienceInputRole.PlainText : ExperienceInputRole.Sensitive;
    }

    private static bool HasSensitiveAncestor(DependencyObject source)
    {
        for (DependencyObject? current = source; current != null; current = current is FrameworkElement element ? element.Parent ?? VisualTreeHelper.GetParent(element) : null)
        {
            var type = current.GetType().Name;
            if (type.StartsWith("Ai", StringComparison.OrdinalIgnoreCase) || type.Contains("Terminal", StringComparison.OrdinalIgnoreCase) || type.Contains("Shell", StringComparison.OrdinalIgnoreCase) || type.Contains("Command", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasAncestor(DependencyObject source, string typeName)
    {
        for (DependencyObject? current = source; current != null; current = current is FrameworkElement element ? element.Parent ?? VisualTreeHelper.GetParent(element) : null)
            if (current.GetType().Name == typeName) return true;
        return false;
    }

    private static bool IsSensitive(TextBox box) => ResolveRole(box) is ExperienceInputRole.Sensitive or ExperienceInputRole.Command;
    private static void TextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(e.OriginalSource, box)) return;
        var state = States.GetValue(box, _ => new State());
        state.FocusText = box.Text;
        if (!box.IsReadOnly && !IsSensitive(box))
        {
            DataObject.RemovePastingHandler(box, Pasting);
            DataObject.AddPastingHandler(box, Pasting);
        }
        if (!box.IsReadOnly && ResolveRole(box) == ExperienceInputRole.Path) box.AllowDrop = true;
        if (ResolveRole(box) == ExperienceInputRole.FileName && !box.IsReadOnly)
        {
            var context = box.ContextMenu ?? new ContextMenu();
            if (!context.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "ExperienceLocateInvalid")))
            {
                if (context.Items.Count == 0)
                    foreach (var (label, command) in new[] { ("撤销", ApplicationCommands.Undo), ("剪切", ApplicationCommands.Cut), ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
                        context.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = box });
                context.Items.Add(new Separator());
                var locate = new MenuItem { Header = "定位无效字符", Tag = "ExperienceLocateInvalid" };
                locate.Click += (_, _) => { var index = InputExperiencePolicy.FirstInvalidFileNameIndex(box.Text); if (index >= 0) { box.Focus(); box.Select(index, Math.Min(1, box.Text.Length - index)); } };
                context.Opened += (_, _) => locate.IsEnabled = Config?.Ux50InputFilenameValidation == true && InputExperiencePolicy.FirstInvalidFileNameIndex(box.Text) >= 0;
                context.Items.Add(locate);
                box.ContextMenu = context;
            }
        }
        UpdateHint(box);
    }

    private static void TextFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ReferenceEquals(e.NewFocus, box)) States.GetValue(box, _ => new State()).FocusText = box.Text;
    }

    private static void TextKey(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box && ReferenceEquals(Keyboard.FocusedElement, box)) TryHandleEscapeEdit(e);
    }

    /// <summary>The window calls this before dismissing an overlay, so editing gets first refusal.</summary>
    public static bool TryHandleEscapeEdit(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox box || box.IsReadOnly || IsSensitive(box) || Config?.Ux50InputEscapeRevert != true || e.Handled || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return false;
        for (DependencyObject? ancestor = box.Parent ?? VisualTreeHelper.GetParent(box); ancestor != null;
            ancestor = ancestor is FrameworkElement element ? element.Parent ?? VisualTreeHelper.GetParent(element) : null)
            if (ancestor is ComboBox { IsDropDownOpen: true }) return false;
        // Search keeps its layered Esc semantics; it is navigation, not a settings edit.
        if (box.Name.Contains("Search", StringComparison.OrdinalIgnoreCase)) return false;
        var state = States.GetValue(box, _ => new State());
        if (box.Text == state.FocusText) return false;
        box.Text = state.FocusText;
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
        return true;
    }

    private static void TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && ReferenceEquals(e.OriginalSource, box)) UpdateHint(box);
    }

    private static void UpdateHint(TextBox box)
    {
        if (box.IsReadOnly || IsSensitive(box)) return;
        var role = ResolveRole(box);
        var hint = "";
        if ((role is ExperienceInputRole.Path or ExperienceInputRole.Address) && Config?.Ux50InputWhitespaceHint == true && box.Text.Length > 0 && box.Text != box.Text.Trim())
            hint = "首尾有空格。请确认是否保留；启动器不会自动删改。";
        if (role == ExperienceInputRole.FileName && Config?.Ux50InputFilenameValidation == true)
        {
            var index = InputExperiencePolicy.FirstInvalidFileNameIndex(box.Text);
            if (index >= 0) hint = $"文件名第 {index + 1} 个字符无效，或名称为系统保留名。请手动修改。";
        }
        if (role == ExperienceInputRole.Memory && Config?.Ux50InputMemoryUnits == true && !string.IsNullOrWhiteSpace(box.Text))
            hint = InputExperiencePolicy.TryParseMemoryMb(box.Text, true, out var mb) ? $"保存时使用 {mb:N0} MB（{mb / 1024d:0.###} GB）。" : "请输入正整数 MB，或可换算为完整 MB 的容量，例如 2048MB、2GB。";
        var state = States.GetValue(box, _ => new State());
        if (hint.Length > 0)
        {
            if (!state.HintActive) { state.OriginalTooltip = box.ToolTip; state.OriginalHelp = AutomationProperties.GetHelpText(box); }
            state.HintActive = true;
            box.ToolTip = hint;
            ToolTipService.SetInitialShowDelay(box, 150);
            AutomationProperties.SetHelpText(box, hint);
        }
        else if (state.HintActive)
        {
            box.ToolTip = state.OriginalTooltip;
            AutomationProperties.SetHelpText(box, state.OriginalHelp);
            state.HintActive = false;
        }
    }

    private static void Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || box.IsReadOnly || box.AcceptsReturn || IsSensitive(box) || Config?.Ux50InputMultilinePastePreview != true) return;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        if (text == null || (!text.Contains('\r') && !text.Contains('\n'))) return;
        e.CancelCommand();
        var replacement = InputExperiencePolicy.JoinPastedLines(text);
        var preview = replacement.Length > 300 ? replacement[..300] + "…" : replacement;
        if (!MessageBoxDialog.ShowConfirm("单行输入框检测到多行内容。将换行替换为空格后粘贴：\n\n" + preview, "多行粘贴预览")) return;
        var start = box.SelectionStart;
        box.SelectedText = replacement;
        box.CaretIndex = start + replacement.Length;
    }

    private static void PathDrag(object sender, DragEventArgs e)
    {
        if (sender is not TextBox box || box.IsReadOnly || ResolveRole(box) != ExperienceInputRole.Path || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = Config?.Ux50InputPathDrop == true && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static void PathDrop(object sender, DragEventArgs e)
    {
        if (sender is not TextBox box || box.IsReadOnly || ResolveRole(box) != ExperienceInputRole.Path || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; // A path field never bubbles a drop into an import/execute action.
        if (Config?.Ux50InputPathDrop != true || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
        try { box.Text = Path.GetFullPath(paths[0]); box.CaretIndex = box.Text.Length; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    /// <summary>Call only after the caller's clipboard write succeeds.</summary>
    public static void ShowCopied(FrameworkElement source)
    {
        if (Config?.Ux50InputLocalCopyFeedback != true) return;
        var tip = new ToolTip { Content = "已复制", PlacementTarget = source, Placement = PlacementMode.Bottom, StaysOpen = true, IsOpen = true };
        var timer = new DispatcherTimer(DispatcherPriority.Background, source.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) => { timer.Stop(); tip.IsOpen = false; source.Unloaded -= OnUnloaded; };
        source.Unloaded += OnUnloaded;
        timer.Start();
        void OnUnloaded(object sender, RoutedEventArgs e) { timer.Stop(); tip.IsOpen = false; source.Unloaded -= OnUnloaded; }
    }

    public static void RefreshSecretControls(DependencyObject root)
    {
        if (root is PasswordBox password) AttachSecret(password);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) RefreshSecretControls(VisualTreeHelper.GetChild(root, i));
    }

    private static void PasswordLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox password && ReferenceEquals(e.OriginalSource, password)) AttachSecret(password);
    }

    private static void AttachSecret(PasswordBox password)
    {
        var state = States.GetValue(password, _ => new State());
        if (Config?.Ux50InputHoldRevealSecret != true)
        {
            state.Secret?.Dispose();
            if (state.Secret != null) AdornerLayer.GetAdornerLayer(password)?.Remove(state.Secret);
            state.Secret = null;
            return;
        }
        if (state.Secret != null || AdornerLayer.GetAdornerLayer(password) is not { } layer) return;
        state.Secret = new SecretRevealAdorner(password);
        layer.Add(state.Secret);
        if (!state.UnloadedHookInstalled)
        {
            state.UnloadedHookInstalled = true;
            password.Unloaded += (_, _) => { state.Secret?.Dispose(); if (state.Secret != null) AdornerLayer.GetAdornerLayer(password)?.Remove(state.Secret); state.Secret = null; };
        }
    }

    private sealed class SecretRevealAdorner : Adorner, IDisposable
    {
        private readonly PasswordBox _password;
        private readonly Button _button;
        private readonly Popup _popup;
        private readonly TextBlock _value;
        private readonly VisualCollection _visuals;
        private readonly Window? _ownerWindow;
        private bool _disposed;
        public SecretRevealAdorner(PasswordBox password) : base(password)
        {
            _password = password;
            _button = new Button { Content = "◉", Width = 22, Height = 22, Padding = new Thickness(0), ToolTip = "按住查看，松开隐藏", Focusable = false, Cursor = Cursors.Hand };
            AutomationProperties.SetName(_button, "按住查看敏感内容");
            _value = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(8), Foreground = SystemColors.InfoTextBrush };
            _popup = new Popup { PlacementTarget = password, Placement = PlacementMode.Bottom, StaysOpen = true, Child = new Border { Background = SystemColors.InfoBrush, BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), Child = _value } };
            _visuals = new VisualCollection(this) { _button };
            _button.PreviewMouseLeftButtonDown += (_, e) => { if (Config?.Ux50InputHoldRevealSecret != true) return; _value.Text = password.Password; _popup.IsOpen = true; _button.CaptureMouse(); e.Handled = true; };
            _button.PreviewMouseLeftButtonUp += (_, e) => { Hide(); e.Handled = true; };
            _button.LostMouseCapture += (_, _) => Hide();
            password.LostKeyboardFocus += PasswordLostFocus;
            _ownerWindow = Window.GetWindow(password);
            if (_ownerWindow != null) WeakEventManager<Window, EventArgs>.AddHandler(_ownerWindow, nameof(Window.Deactivated), OwnerDeactivated);
        }
        private void PasswordLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Hide();
        private void OwnerDeactivated(object? sender, EventArgs e) => Hide();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Hide();
            _password.LostKeyboardFocus -= PasswordLostFocus;
            if (_ownerWindow != null) WeakEventManager<Window, EventArgs>.RemoveHandler(_ownerWindow, nameof(Window.Deactivated), OwnerDeactivated);
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        public void Hide() { _popup.IsOpen = false; _value.Text = ""; if (_button.IsMouseCaptured) _button.ReleaseMouseCapture(); }
        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];
        protected override Size MeasureOverride(Size constraint) { _button.Measure(new Size(22, 22)); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _button.Arrange(new Rect(Math.Max(0, finalSize.Width - 25), Math.Max(0, (finalSize.Height - 22) / 2), 22, 22)); return finalSize; }
    }
}
