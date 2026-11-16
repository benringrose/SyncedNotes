using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace SyncedNotes.Services;

public static class AppInfo
{
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";
}

/// <summary>
/// Builds <c>/sw.js</c>: the service worker source (embedded resource) prefixed with a version hash and the list
/// of static files to precache. The version is a hash of every precached file, so deploying any change produces
/// a byte-different worker, which makes browsers install the update.
/// </summary>
public sealed class ServiceWorkerScript(IWebHostEnvironment environment)
{
    private static readonly HashSet<string> PrecacheExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".css", ".js", ".woff2", ".png", ".svg", ".ico", ".webmanifest" };

    private static readonly string[] ExcludedPrefixes = ["/icons/screenshots/"];

    /// <summary>Anonymous pages that are cached for offline use.</summary>
    private static readonly string[] PrecachePages = ["/offline"];

    private readonly Lock _lock = new();
    private string? _script;

    public string Script
    {
        get
        {
            if (environment.IsDevelopment()) return Build();
            lock (_lock)
            {
                return _script ??= Build();
            }
        }
    }

    private string Build()
    {
        var template = ReadTemplate();
        var files = new List<(string Path, IFileInfo File)>();
        Collect(environment.WebRootFileProvider, "", files);
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(template));
        hash.AppendData(Encoding.UTF8.GetBytes(AppInfo.Version));
        foreach (var (path, file) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            using var stream = file.CreateReadStream();
            hash.AppendData(SHA256.HashData(stream));
        }
        var version = Convert.ToHexStringLower(hash.GetHashAndReset())[..12];

        var precache = files.Select(f => f.Path).Concat(PrecachePages).ToArray();
        var header =
            "/* SyncedNotes service worker (generated - edit ServiceWorker/sw.js) */\n" +
            $"const SN_VERSION = '{version}';\n" +
            $"const SN_DEV = {(environment.IsDevelopment() ? "true" : "false")};\n" +
            $"const SN_PRECACHE = {JsonSerializer.Serialize(precache)};\n";
        return header + template;
    }

    private static void Collect(IFileProvider provider, string path, List<(string, IFileInfo)> files)
    {
        foreach (var item in provider.GetDirectoryContents(path))
        {
            var itemPath = $"{path}/{item.Name}";
            if (item.IsDirectory)
            {
                Collect(provider, itemPath, files);
            }
            else if (PrecacheExtensions.Contains(Path.GetExtension(item.Name)) &&
                     !ExcludedPrefixes.Any(p => itemPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add((itemPath, item));
            }
        }
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(ServiceWorkerScript).Assembly.GetManifestResourceStream("SyncedNotes.ServiceWorker.sw.js")
            ?? throw new InvalidOperationException("The service worker template resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
