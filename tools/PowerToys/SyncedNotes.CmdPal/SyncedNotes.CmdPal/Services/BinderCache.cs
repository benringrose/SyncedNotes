using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncedNotes.CmdPal.Services;

/// <summary>
/// Caches <c>GET /api/v1/quick/binders</c> for the note form's binder dropdown. Fetches are
/// de-duplicated and run in the background, so the form usually opens with the list ready.
/// </summary>
internal sealed class BinderCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly SyncedNotesClient _client;
    private readonly Lock _lock = new();

    private IReadOnlyList<BinderInfo>? _binders;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;
    private Task<IReadOnlyList<BinderInfo>?>? _inflight;
    private int _generation;

    public BinderCache(SyncedNotesClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Returns the cached binders when fresh, otherwise (re)loads them.
    /// The result is <see langword="null"/> when the binders couldn't be loaded.
    /// </summary>
    public Task<IReadOnlyList<BinderInfo>?> GetAsync()
    {
        lock (_lock)
        {
            if (_inflight is not null)
            {
                return _inflight;
            }

            if (DateTimeOffset.UtcNow < _validUntil)
            {
                return Task.FromResult(_binders);
            }

            var generation = _generation;
            var fetch = Task.Run(() => FetchAsync(generation));
            _inflight = fetch;
            return fetch;
        }
    }

    /// <summary>Starts a background refresh if the cache is stale (fire and forget).</summary>
    public void Prefetch() => _ = GetAsync();

    /// <summary>Forgets everything, e.g. after the server URL or token changed.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _binders = null;
            _validUntil = DateTimeOffset.MinValue;
            _inflight = null;
        }
    }

    private async Task<IReadOnlyList<BinderInfo>?> FetchAsync(int generation)
    {
        IReadOnlyList<BinderInfo>? binders = null;
        try
        {
            var result = await _client.GetBindersAsync(CancellationToken.None).ConfigureAwait(false);
            binders = result
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (SyncedNotesException)
        {
            // Already logged by the client. The form falls back to a free-text binder field.
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _inflight = null;
                    if (binders is not null)
                    {
                        _binders = binders;
                        _validUntil = DateTimeOffset.UtcNow + MaxAge;
                    }
                    else
                    {
                        // Keep a previously loaded list, but retry soon.
                        _validUntil = DateTimeOffset.UtcNow + RetryAfterFailure;
                    }
                }
            }
        }

        if (binders is not null)
        {
            return binders;
        }

        lock (_lock)
        {
            return generation == _generation ? _binders : null;
        }
    }
}
