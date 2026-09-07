namespace ProxyDivert.Wpf.Services;

/// <summary>
/// The window's command line. Hand-rolled for the same reason as the console's CliOptions: the
/// whole surface is one flag, and a parser library would be a dependency larger than the thing it
/// parses.
/// </summary>
/// <remarks>
/// Unknown arguments are ignored rather than rejected. The console can afford to refuse a typo
/// because it has a place to print the complaint; a window started at logon has none, and refusing
/// to run would look exactly like the tool being broken.
/// </remarks>
public sealed class AppArguments
{
    /// <summary>
    /// Start without showing the window — only the tray icon. This is what the logon task passes,
    /// so signing in brings the redirect back without a window appearing over whatever the user
    /// was doing.
    /// </summary>
    public bool Minimized { get; private set; }

    /// <summary>The flag as the logon task spells it; also what <see cref="Parse"/> accepts.</summary>
    public const string MinimizedFlag = "--minimized";

    public static AppArguments Parse(string[]? args)
    {
        var parsed = new AppArguments();
        if (args is null) return parsed;

        foreach (string arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                // "--tray" and the slash forms are here because a user editing the task by hand
                // will write one of them sooner or later, and there is no reason to be strict.
                case MinimizedFlag:
                case "--tray":
                case "/minimized":
                case "/tray":
                    parsed.Minimized = true;
                    break;
            }
        }

        return parsed;
    }
}
