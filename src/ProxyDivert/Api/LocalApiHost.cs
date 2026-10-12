using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using ProxyDivert.Api.Extensions;
using ProxyDivert.Api.Interfaces;
using ProxyDivert.Api.Models;
using ProxyDivert.Core.Configuration.Models;

namespace ProxyDivert.Api;

/// <summary>
/// The local HTTP API and its Swagger page, run inside the window's process on a Kestrel of its
/// own. Started, restarted and stopped by <see cref="ApplyAsync"/> whenever the settings change.
/// </summary>
/// <remarks>
/// Owns the web application it builds and nothing else: the backend and the logger provider are
/// the window's, and outlive it.
/// </remarks>
public sealed class LocalApiHost : IAsyncDisposable
{
    private readonly IApiBackend _backend;
    private readonly Func<LocalApiSettings> _settings;
    private readonly ILoggerProvider? _loggerProvider;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private WebApplication? _app;

    /// <param name="settings">
    /// Asked on every request rather than captured: the token and whether it is required change
    /// without a restart.
    /// </param>
    public LocalApiHost(IApiBackend backend, Func<LocalApiSettings> settings, ILoggerProvider? loggerProvider = null)
    {
        _backend = backend;
        _settings = settings;
        _loggerProvider = loggerProvider;
    }

    /// <summary>Where it is listening, the first bound address; null while stopped.</summary>
    public Uri? Address { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// Stops whatever is running and, if <paramref name="settings"/> says so, starts again on its
    /// port. Returns why it could not start (port taken, say), or null.
    /// </summary>
    public async Task<string?> ApplyAsync(LocalApiSettings settings)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (!settings.Enabled) return null;

            WebApplication app = Build(settings);
            try
            {
                await app.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                return ex.Message;
            }

            _app = app;
            string? first = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            Address = first is null ? null : new Uri(first);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        WebApplication? app = _app;
        _app = null;
        Address = null;
        if (app is null) return;
        // Bounded: a request still waiting for the UI thread would otherwise hold the exit, which
        // blocks that same thread, until the host's own (much longer) timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await app.StopAsync(timeout.Token).ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The JSON the API speaks: camelCase, enums by name, an id left out means "assign one".</summary>
    internal static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OptionalIds } };
    }

    // The models mark Id required for the configuration file; a create request may leave it out,
    // and the service fills it in.
    private static void OptionalIds(JsonTypeInfo info)
    {
        foreach (JsonPropertyInfo property in info.Properties)
        {
            if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) property.IsRequired = false;
        }
    }

    private WebApplication Build(LocalApiSettings settings)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LocalApiHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        if (_loggerProvider is not null) builder.Logging.AddProvider(new NonOwningLoggerProvider(_loggerProvider));
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (settings.ListenOnLan)
            {
                kestrel.ListenAnyIP(settings.Port);
            }
            else
            {
                kestrel.Listen(IPAddress.Loopback, settings.Port);
                // Port 0 (tests) would give the second socket a port of its own.
                if (settings.Port != 0 && System.Net.Sockets.Socket.OSSupportsIPv6)
                    kestrel.Listen(IPAddress.IPv6Loopback, settings.Port);
            }
        });

        builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        // The slim builder leaves the regex route constraint out, and the Swagger UI's routes use it.
        builder.Services.Configure<RouteOptions>(o => o.SetParameterPolicy<RegexInlineRouteConstraint>("regex"));
        builder.Services.AddSingleton(_backend);
        builder.Services.AddSingleton<LocalApiService>();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo { Title = "ProxyDivert local API", Version = "v1" });
            var bearer = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Only checked when \"Require token\" is on in Settings.",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            };
            o.AddSecurityDefinition("Bearer", bearer);
            o.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = Array.Empty<string>() });
            o.UseAllOfForInheritance();
            o.UseOneOfForPolymorphism();
        });

        WebApplication app = builder.Build();
        app.Use(HandleErrorsAsync);
        app.Use(CheckTokenAsync);
        app.UseSwagger();
        app.UseSwaggerUI(o => o.DocumentTitle = "ProxyDivert API");
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
        app.MapLocalApi();
        return app;
    }

    // Only /api is guarded: the Swagger page describes routes, it changes nothing, and it is where
    // the token gets typed in.
    private async Task CheckTokenAsync(HttpContext context, RequestDelegate next)
    {
        LocalApiSettings settings = _settings();
        if (settings.RequireToken && context.Request.Path.StartsWithSegments("/api") && !HasToken(context, settings.Token))
        {
            await WriteErrorAsync(context, 401, "UNAUTHORIZED", "Missing or wrong bearer token.").ConfigureAwait(false);
            return;
        }
        await next(context).ConfigureAwait(false);
    }

    private static bool HasToken(HttpContext context, string? expected)
    {
        // Required with no token set refuses everything, rather than accepting an empty one.
        if (string.IsNullOrEmpty(expected)) return false;
        string header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given = Encoding.UTF8.GetBytes(header.Substring(prefix.Length).Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(expected));
    }

    private static async Task HandleErrorsAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Details).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            // A body that is not JSON, or is missing a required field.
            await WriteErrorAsync(context, ex.StatusCode, "INVALID_REQUEST", ex.InnerException?.Message ?? ex.Message)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<LocalApiHost>()
                .LogError(ex, "local API {Method} {Path} failed", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, 500, "INTERNAL_ERROR", ex.Message).ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string message, object? details = null)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Clear();
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ApiError { Code = code, Message = message, Details = details });
    }

    // The builder disposes the providers it was given; the window's must survive every restart.
    private sealed class NonOwningLoggerProvider : ILoggerProvider
    {
        private readonly ILoggerProvider _inner;

        public NonOwningLoggerProvider(ILoggerProvider inner) => _inner = inner;

        public ILogger CreateLogger(string categoryName) => _inner.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
