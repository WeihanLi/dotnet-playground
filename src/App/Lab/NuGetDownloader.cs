using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO.Compression;
using System.Text.Encodings.Web;

namespace DotNetInternals.Lab;

internal static class NuGetUtil
{
    public static string GetPackageVersionListUrl(string packageId)
    {
        return $"https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-tools/NuGet/{packageId}/versions";
    }
}

internal sealed class NuGetDownloader
{
    private readonly SourceRepository repository;
    private readonly SourceCacheContext cacheContext;
    private readonly AsyncLazy<FindPackageByIdResource> findPackageById;

    public NuGetDownloader()
    {
        ImmutableArray<Lazy<INuGetResourceProvider>> providers =
        [
            new(() => new RegistrationResourceV3Provider()),
            new(() => new DependencyInfoResourceV3Provider()),
            new(() => new CustomHttpHandlerResourceV3Provider()),
            new(() => new HttpSourceResourceProvider()),
            new(() => new ServiceIndexResourceV3Provider()),
            new(() => new RemoteV3FindPackageByIdResourceProvider()),
        ];
        repository = Repository.CreateSource(
            providers,
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json");
        cacheContext = new SourceCacheContext();
        findPackageById = new(() => repository.GetResourceAsync<FindPackageByIdResource>());
    }

    public NuGetDownloadablePackage GetPackage(string packageId, string version, string folder)
    {
        return new NuGetDownloadablePackage(folder: folder, downloadAsync);

        async Task<MemoryStream> downloadAsync()
        {
            NuGetVersion parsedVersion;
            if (version == "latest")
            {
                var versions = await (await findPackageById).GetAllVersionsAsync(
                    packageId,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None);
                parsedVersion = versions.FirstOrDefault() ??
                    throw new InvalidOperationException($"Package '{packageId}' not found.");
            }
            else
            {
                parsedVersion = NuGetVersion.Parse(version);
            }

            var stream = new MemoryStream();
            var success = await (await findPackageById).CopyNupkgToStreamAsync(
                packageId,
                parsedVersion,
                stream,
                cacheContext,
                NullLogger.Instance,
                CancellationToken.None);

            if (!success)
            {
                throw new InvalidOperationException(
                    $"Failed to download '{packageId}' version '{version}'.");
            }

            return stream;
        }
    }
}

internal sealed class NuGetDownloadablePackage
{
    private readonly string folder;
    private readonly AsyncLazy<MemoryStream> _stream;

    public NuGetDownloadablePackage(string folder, Func<Task<MemoryStream>> streamFactory)
    {
        this.folder = folder;
        _stream = new(streamFactory);
    }

    private async Task<Stream> GetStreamAsync()
    {
        var result = await _stream;
        result.Position = 0;
        return result;
    }

    private async Task<PackageArchiveReader> GetReaderAsync()
    {
        return new(await GetStreamAsync(), leaveStreamOpen: true);
    }

    public async Task<NuGetPackageInfo> GetInfoAsync()
    {
        using var reader = await GetReaderAsync();
        var metadata = reader.NuspecReader.GetRepositoryMetadata();
        return NuGetPackageInfo.Create(
            version: reader.GetIdentity().Version.ToString(),
            commitHash: metadata.Commit,
            repoUrl: metadata.Url);
    }

    public async Task<ImmutableArray<LoadedAssembly>> GetAssembliesAsync()
    {
        const string extension = ".dll";
        using var reader = await GetReaderAsync();
        return reader.GetFiles()
            .Where(file =>
            {
                // Get only DLL files directly in the specified folder
                // and starting with `Microsoft.`.
                return file.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                    file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                    file.LastIndexOf('/') is int lastSlashIndex &&
                    lastSlashIndex == folder.Length &&
                    file.AsSpan(lastSlashIndex + 1).StartsWith("Microsoft.", StringComparison.Ordinal);
            })
            .Select(file =>
            {
                ZipArchiveEntry entry = reader.GetEntry(file);
                using var entryStream = entry.Open();
                var memoryStream = new MemoryStream(new byte[entry.Length]);
                entryStream.CopyTo(memoryStream);
                memoryStream.Position = 0;
                return new LoadedAssembly()
                {
                    Name = entry.Name[..^extension.Length],
                    Data = memoryStream,
                };
            })
            .ToImmutableArray();
    }
}

internal sealed record NuGetPackageInfo
{
    public static NuGetPackageInfo Create(string version, string commitHash, string repoUrl)
    {
        return new()
        {
            Version = version,
            Commit = new()
            {
                ShortHash = commitHash[..7],
                Url = string.IsNullOrEmpty(repoUrl)
                    ? ""
                    : string.IsNullOrEmpty(commitHash)
                        ? repoUrl
                        : $"{repoUrl}/commit/{commitHash}",
            },
        };
    }

    public required string Version { get; init; }
    public required CommitLink Commit { get; init; }
}

internal readonly record struct CommitLink
{
    public required string ShortHash { get; init; }
    public required string Url { get; init; }
}

internal sealed class CustomHttpHandlerResourceV3Provider : ResourceProvider
{
    public CustomHttpHandlerResourceV3Provider()
        : base(typeof(HttpHandlerResource), nameof(CustomHttpHandlerResourceV3Provider))
    {
    }

    public override Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        return Task.FromResult(TryCreate(source));
    }

    private static Tuple<bool, INuGetResource?> TryCreate(SourceRepository source)
    {
        if (source.PackageSource.IsHttp)
        {
            var clientHandler = new CorsClientHandler();
            var messageHandler = new ServerWarningLogHandler(clientHandler);
            return new(true, new HttpHandlerResourceV3(clientHandler, messageHandler));
        }

        return new(false, null);
    }
}

internal sealed class CorsClientHandler : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.RequestUri = new Uri("https://cloudflare-cors-anywhere.knowpicker.workers.dev/?" +
                UrlEncoder.Default.Encode(request.RequestUri.ToString()));
        }

        return base.SendAsync(request, cancellationToken);
    }
}