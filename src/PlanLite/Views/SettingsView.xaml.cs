using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using PlanLite.Services;
using PlanLite.ViewModels;

namespace PlanLite.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnHotkeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (IsModifierOnlyKey(key))
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        uint win32Mods = 0;
        var parts = new List<string>();

        if (mods.HasFlag(ModifierKeys.Control))
        {
            win32Mods |= (uint)Win32Interop.HotkeyModifiers.Control;
            parts.Add("Ctrl");
        }
        if (mods.HasFlag(ModifierKeys.Alt))
        {
            win32Mods |= (uint)Win32Interop.HotkeyModifiers.Alt;
            parts.Add("Alt");
        }
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            win32Mods |= (uint)Win32Interop.HotkeyModifiers.Shift;
            parts.Add("Shift");
        }
        if (mods.HasFlag(ModifierKeys.Windows))
        {
            win32Mods |= (uint)Win32Interop.HotkeyModifiers.Win;
            parts.Add("Win");
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (win32Mods == 0 || vk == 0)
        {
            vm.ApplyCapturedHotkey(0, 0, string.Empty);
            return;
        }

        parts.Add(FormatKeyLabel(key));
        var display = string.Join(" + ", parts);
        vm.ApplyCapturedHotkey(win32Mods, vk, display);
    }

    private static bool IsModifierOnlyKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.DeadCharProcessed;

    private static string FormatKeyLabel(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return $"Num{key - Key.NumPad0}";
        }

        return key.ToString();
    }
}
