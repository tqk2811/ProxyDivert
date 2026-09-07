using System.Xml.Linq;
using ProxyDivert.Wpf.Services;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The logon task is the one part of this feature that cannot be tried out by running the app: it
// only proves itself at the next sign-in, and getting it wrong is silent — Windows simply does not
// start anything. So the definition is checked here instead, without registering anything.
public class StartupRegistrationTests
{
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void The_logon_task_asks_for_elevation_and_starts_the_window_hidden()
    {
        XDocument document = XDocument.Parse(StartupRegistration.BuildTaskXml());
        XElement task = document.Root!;

        // Without HighestAvailable the task starts unelevated and the engine cannot open the
        // driver; with it, and an administrator account, there is no prompt at sign-in either.
        Assert.Equal("HighestAvailable", task.Descendants(TaskNs + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(TaskNs + "LogonType").Single().Value);
        Assert.Single(task.Descendants(TaskNs + "LogonTrigger"));

        // The flag is what keeps the window off the screen at sign-in; the tray icon is the whole
        // of the user interface until they ask for more.
        Assert.Equal(AppArguments.MinimizedFlag, task.Descendants(TaskNs + "Arguments").Single().Value);
        Assert.NotEmpty(task.Descendants(TaskNs + "Command").Single().Value);
    }

    // Every one of these defaults exists to postpone work that can wait. A network filter cannot:
    // traffic that leaves before it starts leaves unredirected.
    [Theory]
    [InlineData("DisallowStartIfOnBatteries")]
    [InlineData("StopIfGoingOnBatteries")]
    [InlineData("RunOnlyIfIdle")]
    [InlineData("RunOnlyIfNetworkAvailable")]
    public void Nothing_may_hold_the_task_back(string element)
    {
        XDocument document = XDocument.Parse(StartupRegistration.BuildTaskXml());

        Assert.Equal("false", document.Root!.Descendants(TaskNs + element).Single().Value);
    }

    [Theory]
    [InlineData("--minimized")]
    [InlineData("--tray")]
    [InlineData("/minimized")]
    [InlineData("--MINIMIZED")]
    public void The_hidden_start_flag_is_recognised(string flag)
        => Assert.True(AppArguments.Parse([flag]).Minimized);

    // A window started at logon has nowhere to print a complaint, so a typo must not be the
    // difference between running and not running at all.
    [Fact]
    public void An_unknown_argument_is_ignored_rather_than_refused()
    {
        AppArguments parsed = AppArguments.Parse(["--what", "--minimized"]);

        Assert.True(parsed.Minimized);
    }

    [Fact]
    public void No_arguments_means_the_window_shows()
        => Assert.False(AppArguments.Parse([]).Minimized);
}
