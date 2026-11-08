using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ProxyDivert.Localization;
using ProxyDivert.Services.Native;
using static ProxyDivert.Services.Native.TrayNativeMethods;

namespace ProxyDivert.Services;

/// <summary>
/// The notification-area icon, through <c>Shell_NotifyIcon</c> directly. It is the whole user
/// interface while the window is closed, so it holds nothing of WPF but a hidden message window:
/// no context-menu styles, no resource dictionaries, no System.Windows.Forms. Double-click opens the
/// window; right-click shows a native menu built per opening (open, the engine switch, exit).
/// Re-adds the icon when Explorer restarts. Owns its message window and icon handle; borrows the
/// engine controller and the two actions.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private const int CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;
    private const uint CommandOpen = 1;
    private const uint CommandEngine = 2;
    private const uint CommandExit = 3;

    private readonly EngineController _engine;
    private readonly Action _openWindow;
    private readonly Action _exit;
    private readonly HwndSource _messageWindow;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly int _taskbarCreatedMessage;
    private bool _disposed;

    public TrayIconController(EngineController engine, Action openWindow, Action exit)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _openWindow = openWindow ?? throw new ArgumentNullException(nameof(openWindow));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        (_icon, _ownsIcon) = LoadSmallIcon();

        // Top-level but never shown: a message-only window does not receive the TaskbarCreated
        // broadcast, and without it the icon would be lost for good when Explorer restarts.
        _messageWindow = new HwndSource(new HwndSourceParameters(nameof(TrayIconController))
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _messageWindow.AddHook(WndProc);
        // The process runs elevated and Explorer does not, so UIPI drops its TaskbarCreated
        // broadcast unless this window lets it through: without it the icon is gone for good after
        // an Explorer restart, and with the window closed that is the only way back in.
        ChangeWindowMessageFilterEx(_messageWindow.Handle, (uint)_taskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);
        AddIcon();

        // The tooltip says whether redirection is on, in the current language.
        _engine.PropertyChanged += OnEnginePropertyChanged;
        LocalizationManager.LanguageChanged += UpdateToolTip;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.PropertyChanged -= OnEnginePropertyChanged;
        LocalizationManager.LanguageChanged -= UpdateToolTip;
        // Without this the icon is left in the notification area as a ghost until something makes
        // the shell notice the process is gone.
        NOTIFYICONDATA data = CreateData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
        if (_ownsIcon) DestroyIcon(_icon);
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(EngineController.State)) UpdateToolTip();
    }

    private void AddIcon()
    {
        NOTIFYICONDATA data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        // Already there when TaskbarCreated raced with startup.
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void UpdateToolTip()
    {
        if (_disposed) return;
        NOTIFYICONDATA data = CreateData(NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _messageWindow.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = ToolTipText(),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private string ToolTipText()
        => Loc.F("Str.Tray.ToolTip", Loc.S(_engine.IsRunning ? "Str.App.Running" : "Str.App.Stopped"));

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage) return IntPtr.Zero;

        handled = true;
        try
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONDBLCLK:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    _openWindow();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
        }
        catch (Exception ex)
        {
            // An exception must not unwind through the window procedure: report it and keep the
            // tray alive, since it is the only way back into the application.
            System.Windows.MessageBox.Show(ex.ToString(), "ProxyDivert",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        return IntPtr.Zero;
    }

    /// <summary>Built per opening, so it always has the current language and engine state.</summary>
    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        uint command;
        try
        {
            AppendMenu(menu, MF_STRING, CommandOpen, Loc.S("Str.Tray.Open"));
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            // The same switch as the one on the title bar. Greyed without Administrator rights,
            // since WinDivert loads a kernel driver, and while the engine is already moving.
            uint engineFlags = MF_STRING
                | (_engine.IsSwitchedOn ? MF_CHECKED : 0)
                | (!_engine.IsElevated || _engine.IsBusy ? MF_GRAYED : 0);
            AppendMenu(menu, engineFlags, CommandEngine, Loc.S("Str.Tray.Engine"));
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CommandExit, Loc.S("Str.Tray.Exit"));
            SetMenuDefaultItem(menu, CommandOpen, 0);

            GetCursorPos(out POINT point);
            // Foreground first and WM_NULL after: the documented way to make the menu close when
            // the user clicks elsewhere.
            SetForegroundWindow(_messageWindow.Handle);
            command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X, point.Y, _messageWindow.Handle, IntPtr.Zero);
            PostMessage(_messageWindow.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }

        switch (command)
        {
            case CommandOpen:
                _openWindow();
                break;
            case CommandEngine:
                _ = ToggleEngineAsync();
                break;
            case CommandExit:
                _exit();
                break;
        }
    }

    // Fire-and-forget from the menu, so nothing may escape: a failed start is already reported on
    // the notice strip by the controller, and a failed stop goes there too.
    private async System.Threading.Tasks.Task ToggleEngineAsync()
    {
        try
        {
            await _engine.ToggleAsync();
        }
        catch (Exception ex)
        {
            _engine.StatusMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>The tray-size frame of the exe's own icon; the stock application icon otherwise.</summary>
    private static (IntPtr Icon, bool Owned) LoadSmallIcon()
    {
        IntPtr[] small = [IntPtr.Zero];
        if (Environment.ProcessPath is { } path
            && ExtractIconEx(path, 0, null, small, 1) > 0
            && small[0] != IntPtr.Zero)
        {
            return (small[0], true);
        }
        return (LoadIcon(IntPtr.Zero, IDI_APPLICATION), false);
    }
}
