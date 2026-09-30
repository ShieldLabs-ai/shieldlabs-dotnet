using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShieldLabs.Internal;

internal static class SdkInfo
{
    /// <summary>
    /// Package version, read from the assembly (the <c>Version</c> in the project file), so a
    /// release only changes the project file. Declared before <see cref="UserAgent"/>, which uses it.
    /// </summary>
    internal static readonly string Version = ReadVersion(typeof(SdkInfo).Assembly);

    /// <summary><c>shieldlabs-dotnet/1.0.0 (.NET 8.0.x)</c>.</summary>
    internal static readonly string UserAgent = BuildUserAgent();

    /// <summary>
    /// The informational version without build metadata (<c>1.0.0+abc123</c> is <c>1.0.0</c>), or the
    /// assembly version when the attribute is missing.
    /// </summary>
    internal static string ReadVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational!.IndexOf('+');
            var version = Sanitize(plus >= 0 ? informational.Substring(0, plus) : informational);
            if (version.Length > 0)
            {
                return version;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string BuildUserAgent()
    {
        var runtime = Sanitize(RuntimeInformation.FrameworkDescription);
        return runtime.Length == 0
            ? $"shieldlabs-dotnet/{Version}"
            : $"shieldlabs-dotnet/{Version} ({runtime})";
    }

    private static string Sanitize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in text ?? string.Empty)
        {
            if (c >= 0x20 && c < 0x7F && c != '(' && c != ')' && c != '\\')
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// One process-wide <see cref="HttpClient"/> used when the caller does not supply one, so creating
/// many SDK clients never exhausts sockets. Created lazily: nothing touches the network at load time.
/// </summary>
internal static class DefaultHttp
{
    private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static HttpClient Instance => Shared.Value;

    private static HttpClient Create()
    {
#if NET8_0_OR_GREATER
        HttpMessageHandler handler = SocketsHttpHandler.IsSupported
            ? new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            }
            : new HttpClientHandler();
#else
        HttpMessageHandler handler = new HttpClientHandler();
#endif
        // Per-attempt timeouts are applied by the SDK itself.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
