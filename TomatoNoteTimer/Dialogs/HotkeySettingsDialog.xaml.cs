using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TomatoNoteTimer.Models;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TomatoNoteTimer.Dialogs;

public partial class HotkeySettingsDialog : Window
{
    private readonly Dictionary<string, WpfTextBox> _boxes;

    public HotkeySettingsDialog(HotkeySettings current)
    {
        InitializeComponent();

        _boxes = new Dictionary<string, WpfTextBox>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["StartTimer"] = StartTimerTextBox,
            ["PauseTimer"] = PauseTimerTextBox,
            ["ResetTimer"] = ResetTimerTextBox,
            ["ToggleSimpleMode"] = ToggleSimpleModeTextBox,
            ["ToggleTopMost"] = ToggleTopMostTextBox,
            ["ToggleFixedMode"] = ToggleFixedModeTextBox
        };

        Hotkeys = Clone(current);
        Hotkeys.Normalize();
        ApplyHotkeysToInputs(Hotkeys);
    }

    public HotkeySettings Hotkeys { get; private set; }

    private static HotkeySettings Clone(HotkeySettings? source)
    {
        source ??= new HotkeySettings();
        return new HotkeySettings
        {
            StartTimer = source.StartTimer,
            PauseTimer = source.PauseTimer,
            ResetTimer = source.ResetTimer,
            ToggleSimpleMode = source.ToggleSimpleMode,
            ToggleTopMost = source.ToggleTopMost,
            ToggleFixedMode = source.ToggleFixedMode
        };
    }

    private void ApplyHotkeysToInputs(HotkeySettings value)
    {
        StartTimerTextBox.Text = value.StartTimer;
        PauseTimerTextBox.Text = value.PauseTimer;
        ResetTimerTextBox.Text = value.ResetTimer;
        ToggleSimpleModeTextBox.Text = value.ToggleSimpleMode;
        ToggleTopMostTextBox.Text = value.ToggleTopMost;
        ToggleFixedModeTextBox.Text = value.ToggleFixedMode;
    }

    private void HotkeyInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox box)
        {
            box.SelectAll();
        }

        _ = e;
        StatusTextBlock.Text = "按下组合键进行设置；按 Backspace/Delete 可清空当前项。";
    }

    private void HotkeyInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        _ = sender;
        e.Handled = true;
    }

    private void HotkeyInput_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (sender is not WpfTextBox box)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;

        if ((key == Key.Back || key == Key.Delete) && modifiers == ModifierKeys.None)
        {
            box.Text = string.Empty;
            StatusTextBlock.Text = $"已清空：{GetActionTitle(box.Tag as string)}";
            return;
        }

        if (!TryBuildHotkeyFromInput(key, modifiers, out string hotkey, out string error))
        {
            StatusTextBlock.Text = error;
            return;
        }

        box.Text = hotkey;
        StatusTextBlock.Text = $"已设置：{GetActionTitle(box.Tag as string)} = {hotkey}";
    }

    private static bool TryBuildHotkeyFromInput(Key key, ModifierKeys modifiers, out string hotkey, out string error)
    {
        hotkey = string.Empty;
        error = string.Empty;

        if (IsModifierKey(key))
        {
            error = "请在按下修饰键后，再按一个主键。";
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            error = "必须包含 Ctrl/Alt/Shift/Win 中至少一个修饰键。";
            return false;
        }

        if (!TryGetKeyDisplayName(key, out string keyName))
        {
            error = "该按键暂不支持，请更换其他主键。";
            return false;
        }

        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        hotkey = string.Join("+", parts);
        return true;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin;
    }

    private static bool TryGetKeyDisplayName(Key key, out string keyName)
    {
        keyName = string.Empty;

        if (key is >= Key.A and <= Key.Z)
        {
            keyName = key.ToString().ToUpperInvariant();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            keyName = (((int)key) - (int)Key.D0).ToString();
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            keyName = $"F{(((int)key) - (int)Key.F1) + 1}";
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            keyName = key.ToString();
            return true;
        }

        keyName = key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            _ => key.ToString()
        };

        return !string.IsNullOrWhiteSpace(keyName);
    }

    private void ClearItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string actionKey)
        {
            return;
        }

        if (_boxes.TryGetValue(actionKey, out WpfTextBox? box))
        {
            box.Text = string.Empty;
            StatusTextBlock.Text = $"已清空：{GetActionTitle(actionKey)}";
        }

        _ = e;
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (WpfTextBox box in _boxes.Values)
        {
            box.Text = string.Empty;
        }

        StatusTextBlock.Text = "已清空全部快捷键。";
        _ = e;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = new HotkeySettings
        {
            StartTimer = StartTimerTextBox.Text.Trim(),
            PauseTimer = PauseTimerTextBox.Text.Trim(),
            ResetTimer = ResetTimerTextBox.Text.Trim(),
            ToggleSimpleMode = ToggleSimpleModeTextBox.Text.Trim(),
            ToggleTopMost = ToggleTopMostTextBox.Text.Trim(),
            ToggleFixedMode = ToggleFixedModeTextBox.Text.Trim()
        };
        settings.Normalize();

        if (TryFindConflict(settings, out string message))
        {
            System.Windows.MessageBox.Show(this, message, "快捷键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Hotkeys = settings;
        DialogResult = true;
        Close();
        _ = sender;
    }

    private static bool TryFindConflict(HotkeySettings settings, out string message)
    {
        var pairs = new[]
        {
            ("开启倒计时", settings.StartTimer),
            ("暂停倒计时", settings.PauseTimer),
            ("重置倒计时", settings.ResetTimer),
            ("进入/退出简洁模式", settings.ToggleSimpleMode),
            ("窗口置顶开关", settings.ToggleTopMost),
            ("窗口固定开关", settings.ToggleFixedMode)
        };

        string[] duplicates = pairs
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2))
            .GroupBy(x => x.Item2, System.StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}（{string.Join("、", g.Select(x => x.Item1))}）")
            .ToArray();

        if (duplicates.Length == 0)
        {
            message = string.Empty;
            return false;
        }

        message = $"以下快捷键存在重复绑定：{System.Environment.NewLine}{string.Join(System.Environment.NewLine, duplicates)}";
        return true;
    }

    private static string GetActionTitle(string? actionKey)
    {
        return actionKey switch
        {
            "StartTimer" => "开启倒计时",
            "PauseTimer" => "暂停倒计时",
            "ResetTimer" => "重置倒计时",
            "ToggleSimpleMode" => "进入/退出简洁模式",
            "ToggleTopMost" => "窗口置顶开关",
            "ToggleFixedMode" => "窗口固定开关",
            _ => "快捷键"
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
        _ = sender;
    }
}
