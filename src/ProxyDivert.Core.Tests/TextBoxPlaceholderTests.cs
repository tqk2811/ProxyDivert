using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The placeholder is a TextBlock the template lays over the box; the text the user types is drawn
// by the box itself. Give the two the same inset and they still do not line up — a TextBox already
// insets its own text view by Padding, so a template that also sets Padding as a margin applies it
// twice to the text and once to the hint, and then keeps a couple of pixels back for the caret on
// top of that. The caret ends up somewhere the grey hint it is replacing never was.
//
// Measured rather than eyeballed. The correction is a fudge factor, and a fudge factor with no test
// is a number nobody can ever safely change again.
[Collection("WPF")]
public class TextBoxPlaceholderTests
{
    [Fact]
    public void The_placeholder_starts_where_the_caret_does()
    {
        double caretX = 0;
        double placeholderX = 0;

        RunOnStaThread(() =>
        {
            EnsureApplication();

            var box = new TextBox
            {
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "chrome.exe",
                Text = string.Empty,
            };
            using (var window = Show(box))
            {
                TextBlock placeholder = FindVisuals<TextBlock>(box).Single();
                placeholderX = placeholder.TransformToAncestor(box).Transform(new Point(0, 0)).X;

                // Where the first character lands, which is where the caret sits before it is
                // typed. Asked with text in place: an empty box has no character to ask about.
                box.Text = "x";
                box.UpdateLayout();
                caretX = box.GetRectFromCharacterIndex(0).X;
            }
        });

        Assert.True(
            Math.Abs(caretX - placeholderX) < 0.5,
            $"The caret sits at {caretX} and the placeholder at {placeholderX}, "
            + $"{placeholderX - caretX} apart: an empty box shows its hint offset from the caret.");
    }

    // A password box sits directly under a text box on the Outbounds tab, and they are built from
    // two separate templates that made the same mistake. Two boxes in a column whose text starts
    // on different pixels reads as a broken form.
    [Fact]
    public void A_password_box_starts_its_text_where_a_text_box_does()
    {
        double textX = 0;
        double passwordX = 0;

        RunOnStaThread(() =>
        {
            EnsureApplication();

            // Both read as the origin of the text view the template hosts, because a PasswordBox
            // has no character to ask the position of. The two controls inset their own view the
            // same way, so equal origins mean equal ink — which is what the placeholder test
            // above pins down in absolute terms for the TextBox.
            var box = new TextBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            using (Show(box)) textX = ContentHostX(box);

            var secret = new PasswordBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            using (Show(secret)) passwordX = ContentHostX(secret);
        });

        Assert.True(
            Math.Abs(textX - passwordX) < 0.5,
            $"A text box starts its text at {textX} and a password box at {passwordX}.");
    }

    private static double ContentHostX(Control control)
    {
        var host = (FrameworkElement)control.Template.FindName("PART_ContentHost", control);
        return host.TransformToAncestor(control).Transform(new Point(0, 0)).X;
    }

    // The window has to be shown and laid out before anything can be measured, and closed after.
    private static IDisposable Show(FrameworkElement content)
    {
        var window = new Window { Width = 400, Height = 200, Content = content };
        window.Show();
        content.UpdateLayout();
        return new Closer(window);
    }

    private sealed class Closer : IDisposable
    {
        private readonly Window _window;
        public Closer(Window window) => _window = window;
        public void Dispose() => _window.Close();
    }

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

        if (failure != null) throw new Xunit.Sdk.XunitException($"WPF placeholder test failed: {failure}");
    }
}
