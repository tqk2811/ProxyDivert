using System;
using System.Collections.Generic;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Interfaces;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// Hands the engine a <see cref="FakeProcessRedirector"/> for every run, and keeps what it was asked
/// for: the options each run described, and the redirector each run got.
/// </summary>
/// <remarks>
/// Registered in a container ahead of <c>AddProxyDivert</c>, which only adds its own factory when
/// none is there, this is what lets a whole engine — tracker, pid queue, routers — start and stop in
/// a test with no driver and no elevation.
/// </remarks>
internal sealed class FakeProcessRedirectorFactory : IProcessRedirectorFactory
{
    public List<RedirectOptions> Options { get; } = new List<RedirectOptions>();

    public List<FakeProcessRedirector> Created { get; } = new List<FakeProcessRedirector>();

    /// <summary>Makes the next redirector refuse to start, once.</summary>
    public Exception? FailNextStart { get; set; }

    public IProcessRedirector Create(RedirectOptions options)
    {
        var redirector = new FakeProcessRedirector { StartFailure = FailNextStart };
        FailNextStart = null;

        lock (Created)
        {
            Options.Add(options);
            Created.Add(redirector);
        }
        return redirector;
    }
}
