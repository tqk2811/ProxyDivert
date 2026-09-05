using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Renaming a policy happens in the list itself: double-click a row and its text becomes a box.
// Which row that is lives on the view model as one reference, so every row has to compare itself
// against it — through a MultiBinding the compiler never checks. Get that comparison wrong and the
// double-click does nothing at all, or every row turns into a box at once. Only running it says.
[Collection("WPF")]
public class PolicyRenameTests
{
    private sealed class Stub : INotifyPropertyChanged
    {
        public ObservableCollection<object> Policies { get; } = new();
        public ObservableCollection<object> Rules { get; } = new();
        public ObservableCollection<object> Outbounds { get; } = new();
        public Array Matchers { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.HostMatcherType));
        public Array UdpModes { get; } = Enum.GetValues(typeof(ProxyDivert.Core.Routing.Enums.UdpMode));
        public object? SelectedPolicy { get; set; }
        public string PolicyName { get; set; } = string.Empty;

        private RoutingPolicy? _renamingPolicy;
        public RoutingPolicy? RenamingPolicy
        {
            get => _renamingPolicy;
            set
            {
                _renamingPolicy = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RenamingPolicy)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void Only_the_row_being_renamed_turns_into_a_box()
    {
        var boxesBefore = new List<string>();
        var boxesAfter = new List<string>();
        var textsAfter = new List<string>();

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var work = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Work" };
            var games = new RoutingPolicy { Id = Guid.NewGuid(), Name = "Games" };

            var stub = new Stub();
            stub.Policies.Add(work);
            stub.Policies.Add(games);
            stub.PolicyName = "Games";

            var view = new ProxyDivert.Wpf.Views.RulesView { DataContext = stub };
            var window = new Window { Width = 1200, Height = 800, Content = view };
            window.Show();
            view.UpdateLayout();

            ListBox list = FindVisuals<ListBox>(view).First();

            // Nothing is being renamed, so the list is all labels and no boxes.
            boxesBefore.AddRange(VisibleBoxes(list));

            stub.RenamingPolicy = games;
            view.UpdateLayout();

            boxesAfter.AddRange(VisibleBoxes(list));
            textsAfter.AddRange(FindVisuals<TextBlock>(list)
                .Where(text => text.IsVisible)
                .Select(text => text.Text ?? string.Empty));

            window.Close();
        });

        Assert.Empty(boxesBefore);

        // Exactly one box, holding the name being edited — and the row it replaced no longer shows
        // its label, while the other row still does.
        Assert.Equal(new[] { "Games" }, boxesAfter);
        Assert.Contains("Work", textsAfter);
        Assert.DoesNotContain("Games", textsAfter);
    }

    // The half a stub cannot check: that the list actually redraws. A RoutingPolicy is plain data
    // with nothing to raise a change, so the view model has to tell the collection — and assigning
    // the row back over itself does not, because the same reference in and out is not a change as
    // far as WPF is concerned. The name was saved and the list went on showing the old one until
    // the tab was rebuilt, which is exactly what a user sees as "renaming does nothing".
    //
    // Runs the real view model against a real configuration file, so the save is checked too.
    [Fact]
    public void Renaming_shows_the_new_name_in_the_list_straight_away()
    {
        var namesOnScreen = new List<string>();
        string savedFile = string.Empty;

        RunOnStaThread(() =>
        {
            EnsureApplication();

            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ProxyDivertTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "config.json");

            using (var services = new ProxyDivert.Wpf.Services.AppServices(path))
            {
                var model = new ProxyDivert.Wpf.ViewModels.RulesViewModel(services);
                model.AddPolicyCommand.Execute(null);

                var view = new ProxyDivert.Wpf.Views.RulesView { DataContext = model };
                var window = new Window { Width = 1200, Height = 800, Content = view };
                window.Show();
                view.UpdateLayout();

                RoutingPolicy first = model.Policies[0];
                model.BeginRename(first);
                model.PolicyName = "Renamed";
                model.CommitRename();

                view.UpdateLayout();

                namesOnScreen.AddRange(FindVisuals<ListBox>(view)
                    .SelectMany(FindVisuals<TextBlock>)
                    .Where(text => text.IsVisible)
                    .Select(text => text.Text ?? string.Empty));

                window.Close();
                savedFile = System.IO.File.ReadAllText(path);
            }

            System.IO.Directory.Delete(directory, recursive: true);
        });

        Assert.Contains("Renamed", namesOnScreen);
        Assert.DoesNotContain("Default", namesOnScreen);

        // The other policy is untouched, so the redraw is not "rebuild everything and hope".
        Assert.Contains(namesOnScreen, name => name.Contains("Policy"));

        Assert.Contains("Renamed", savedFile);
    }

    private static IEnumerable<string> VisibleBoxes(ListBox list)
        => FindVisuals<TextBox>(list).Where(box => box.IsVisible).Select(box => box.Text ?? string.Empty);

    private static Application EnsureApplication()
    {
        if (Application.Current == null)
        {
            var application = new ProxyDivert.Wpf.App();
            application.InitializeComponent();
        }

        if (Application.Current.CheckAccess())
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        return Application.Current;
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (T deeper in FindVisuals<T>(child)) yield return deeper;
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new Xunit.Sdk.XunitException($"WPF rename test failed: {failure}");
    }
}
