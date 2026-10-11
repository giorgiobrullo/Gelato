using Gelato.Providers;
using Jellyfin.Data;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Syncs a movie's or an episode's streams from the addon when its page opens, or ahead of a
/// pre-probe, once per <see cref="SyncCacheKey"/> until the stream cache expires.
/// </summary>
internal sealed class StreamSyncOnOpen(
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider
)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;
    private readonly KeyLock _lock = new();

    internal static string SyncCacheKey(BaseItem item, Guid userId)
    {
        var syncItemId = (item as Video)?.PrimaryVersionId ?? item.Id;
        var key = $"{syncItemId}:{item.DateCreated.Ticks}";
        return userId != Guid.Empty ? $"{userId}:{key}" : key;
    }

    /// <summary>Whether a sync of the item's streams is under way.</summary>
    internal bool IsSyncing(Guid itemId) => _lock.IsInFlight(itemId);

    /// <summary>
    /// Syncs the item's streams for the user, once at a time per item, and waits for it: the
    /// version list built next lists what the sync wrote.
    /// </summary>
    /// <param name="background">
    /// Not to wait for a sync that is still running: the caller answers without its streams.
    /// </param>
    /// <returns>Whether the sync is still running (only with <paramref name="background"/>).</returns>
    internal bool SyncNow(
        BaseItem item,
        Guid userId,
        StremioUri uri,
        string cacheKey,
        bool background = false
    )
    {
        var manager = _manager.Value;

        // Bug in web UI that calls the detail page twice. So that's why there's a lock.
        var sync = _lock
            .RunSingleFlightAsync(
                item.Id,
                async ct =>
                {
                    _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                    // Prewarm subtitle cache in the background if Gelato Subtitles
                    // is enabled for this library.
                    var libraryOptions = _libraryManager.GetLibraryOptions(item);
                    var subtitlePrewarmEnabled =
                        libraryOptions.SubtitleDownloadLanguages?.Length > 0
                        && !libraryOptions.DisabledSubtitleFetchers.Contains(
                            "Gelato Subtitles",
                            StringComparer.OrdinalIgnoreCase
                        );

                    if (subtitlePrewarmEnabled)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _subtitleProvider
                                    .Value.GetSubtitlesAsync(
                                        uri.ExternalId,
                                        uri.MediaType,
                                        CancellationToken.None
                                    )
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                            }
                        });
                    }

                    try
                    {
                        var generation = manager.CacheGeneration;
                        var count = await manager
                            .SyncStreams(item, userId, ct)
                            .ConfigureAwait(false);
                        // No stream: kept for a few minutes only, so the page does not ask
                        // the addons again on every visit.
                        manager.SetStreamSync(
                            cacheKey,
                            generation,
                            count > 0 ? null : GelatoManager.EmptyStreamSyncTtl
                        );
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to sync streams for {Id}", item.Id);
                    }
                }
            );

        if (background && !sync.IsCompleted)
            return true;

        sync.GetAwaiter().GetResult();

        // refresh item
        _libraryManager.GetItemById(item.Id);
        return false;
    }

    /// <summary>The sync <see cref="SyncNow"/> runs, when the item's streams are not synced.</summary>
    internal async Task SyncIfDueAsync(BaseItem item, Guid userId)
    {
        var manager = _manager.Value;

        var syncKey = SyncCacheKey(item, userId);
        var syncItemId = (item as Video)?.PrimaryVersionId ?? item.Id;
        if (!manager.HasStreamSync(syncKey, syncItemId))
        {
            await _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct =>
                    {
                        var generation = manager.CacheGeneration;
                        if (await manager.SyncStreams(item, userId, ct).ConfigureAwait(false) > 0)
                            manager.SetStreamSync(syncKey, generation);
                    }
                )
                .ConfigureAwait(false);
        }
    }
}
