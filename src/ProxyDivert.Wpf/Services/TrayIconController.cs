using System;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hardcodet.Wpf.TaskbarNotification;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Services;

/// <summary>
/// The notification-area icon and everything reachable from it. Owns the window's visibility —
/// showing and hiding it is the one thing the tray does that no view model has any business
/// knowing about — and borrows everything else: switching redirection on and off is the main view
/// model's command, used as it stands rather than reimplemented beside it.
/// </summary>
public sealed partial class TrayIconController : ObservableObject, IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Window _window;

    // Shown once, the first time the window disappears rather than closes. Repeating it every time
    // would be nagging; never saying it once leaves the user believing they quit.
    private bool _explainedHiding;

    /// <summary>The window's state, for the menu to bind the engine switch and the admin lock to.</summary>
    public MainViewModel ViewModel { get; }

    [ObservableProperty]
    private string _windowToggleText = string.Empty;

    [ObservableProperty]
    private string _toolTipText = string.Empty;

    public TrayIconController(TaskbarIcon icon, Window window, MainViewModel viewModel)
    {
        _icon = icon ?? throw new ArgumentNullException(nameof(icon));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        _icon.DataContext = this;

        _window.IsVisibleChanged += OnWindowVisibilityChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Both texts are composed in code from two pieces, so a dictionary swap does not reach
        // them on its own — the same reason the theme button rebuilds itself.
        LocalizationManager.LanguageChanged += UpdateTexts;

        UpdateTexts();
    }

    [RelayCommand]
    private void ToggleWindow()
    {
        if (_window.IsVisible) _window.Hide();
        else ShowWindow();
    }

    /// <summary>Brings the window back, whatever it was doing: hidden, minimised, or behind
    /// something else. Double-clicking the icon does this, which is what everything else in the
    /// notification area does.</summary>
    [RelayCommand]
    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    [RelayCommand]
    private void Exit() => App.BeginExit();

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateTexts();

        if (_window.IsVisible || _explainedHiding) return;

        _explainedHiding = true;
        _icon.ShowBalloonTip(
            Loc.S("Str.Tray.HiddenTitle"), Loc.S("Str.Tray.HiddenMessage"), BalloonIcon.Info);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainViewModel.IsRunning)) UpdateTexts();
    }

    private void UpdateTexts()
    {
        WindowToggleText = Loc.S(_window.IsVisible ? "Str.Tray.Hide" : "Str.Tray.Show");
        ToolTipText = Loc.F(
            "Str.Tray.ToolTip",
            Loc.S(ViewModel.IsRunning ? "Str.App.Running" : "Str.App.Stopped"));
    }

    public void Dispose()
    {
        LocalizationManager.LanguageChanged -= UpdateTexts;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _window.IsVisibleChanged -= OnWindowVisibilityChanged;
        // Without this the icon is left in the notification area as a ghost until something makes
        // the shell notice the process is gone.
        _icon.Dispose();
    }
}
