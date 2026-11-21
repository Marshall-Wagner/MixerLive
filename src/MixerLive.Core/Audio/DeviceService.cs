using MixerLive.Core.Logging;
using MixerLive.Core.Model;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MixerLive.Core.Audio;

public sealed record DeviceInfo(string Id, string Name, DataFlow Flow);

/// <summary>
/// Enumerates active WASAPI endpoints, resolves <see cref="DeviceRef"/>s (by ID, then by exact or
/// truncated friendly name) and raises a debounced <see cref="DevicesChanged"/> on hot-plug events.
///
/// Locking rule: Windows calls <see cref="NotificationClient"/> while holding locks inside the
/// MMDevice API, so the callbacks must never wait for anything that is held across an MMDevice
/// call; they only bump <see cref="_generation"/> and re-arm the debounce timer. Otherwise a
/// device-list refresh (holding <see cref="_sync"/> inside an MMDevice call) and a notification
/// (waiting for <see cref="_sync"/>) deadlock each other, which froze the mixer when an audio-driver
/// install restarted the Windows audio service.
/// </summary>
public sealed class DeviceService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private MMDeviceEnumerator _enumerator = null!;
    private readonly NotificationClient _notifications;
    /// <summary>Serialises MMDevice calls and guards <see cref="_cache"/>. Never taken by notification callbacks.</summary>
    private readonly object _sync = new();
    private readonly Dictionary<DataFlow, (IReadOnlyList<DeviceInfo> List, DateTime At, int Generation)> _cache = new();
    private readonly Timer _debounce;
    /// <summary>Bumped by every device notification; a cached list from an older generation is stale.</summary>
    private int _generation;
    private volatile bool _disposed;

    public event Action? DevicesChanged;

    // Test hooks for the locking rule above.
    internal object SyncRootForTests => _sync;
    internal IMMNotificationClient NotificationsForTests => _notifications;

    public DeviceService()
    {
        _notifications = new NotificationClient(this);
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        Apartment.RunOnMta(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(_notifications);
            }
            catch (Exception ex)
            {
                Log.Warn($"Device change notifications unavailable: {ex.Message}");
            }
        });
    }

    public IReadOnlyList<DeviceInfo> GetDevices(DataFlow flow) => Apartment.RunOnMta(() => GetDevicesCore(flow));

    /// <summary>
    /// Enumeration with friendly-name lookups costs ~1 s on machines with many endpoints, so the
    /// snapshot is cached until a device-change notification arrives (or the TTL expires).
    /// </summary>
    private IReadOnlyList<DeviceInfo> GetDevicesCore(DataFlow flow)
    {
        lock (_sync)
        {
            int generation = Volatile.Read(ref _generation);
            if (_cache.TryGetValue(flow, out var cached) && cached.Generation == generation && DateTime.UtcNow - cached.At < CacheTtl)
                return cached.List;

            var list = new List<DeviceInfo>();
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    try { list.Add(new DeviceInfo(d.ID, d.FriendlyName, flow)); }
                    catch { /* endpoint vanished mid-enumeration */ }
                    finally { d.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Device enumeration failed", ex);
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            // A notification that arrived during the enumeration makes this list stale already.
            _cache[flow] = (list, DateTime.UtcNow, generation);
            return list;
        }
    }

    /// <summary>Marks the cached device lists stale. Lock-free, so it is safe from any thread.</summary>
    public void InvalidateCache() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Finds the active endpoint for <paramref name="wanted"/>. On success the reference is updated
    /// with the endpoint ID and full friendly name. Caller owns the returned device.
    /// </summary>
    public MMDevice? Resolve(DeviceRef wanted, DataFlow flow)
    {
        if (wanted.IsEmpty) return null;
        return Apartment.RunOnMta(() => ResolveCore(wanted, flow));
    }

    private MMDevice? ResolveCore(DeviceRef wanted, DataFlow flow)
    {
        var list = GetDevicesCore(flow);
        var candidates = new List<(string Id, string Name)>(list.Count);
        foreach (var d in list) candidates.Add((d.Id, d.Name));

        int idx = Match(candidates, wanted);
        if (idx < 0) return null;

        MMDevice? dev = null;
        try
        {
            lock (_sync) dev = _enumerator.GetDevice(list[idx].Id);
            if (dev.State != DeviceState.Active)
            {
                dev.Dispose();
                InvalidateCache();
                return null;
            }
            wanted.Id = list[idx].Id;
            wanted.Name = list[idx].Name;
            return dev;
        }
        catch (Exception ex)
        {
            Log.Warn($"Endpoint '{list[idx].Name}' disappeared: {ex.Message}");
            dev?.Dispose();
            InvalidateCache();
            return null;
        }
    }

    /// <summary>Same matching rules as <see cref="Resolve"/>, without touching COM objects.</summary>
    public static int Match(IReadOnlyList<(string Id, string Name)> candidates, DeviceRef wanted)
    {
        if (!string.IsNullOrEmpty(wanted.Id))
        {
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].Id == wanted.Id) return i;
        }

        if (string.IsNullOrWhiteSpace(wanted.Name)) return -1;
        string n = wanted.Name.Trim();

        for (int i = 0; i < candidates.Count; i++)
            if (string.Equals(candidates[i].Name.Trim(), n, StringComparison.OrdinalIgnoreCase)) return i;

        // Voicemeeter stores at most 31 characters of a device name: prefer the shortest name
        // that starts with the stored prefix.
        int best = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) continue;
            if (best < 0 || candidates[i].Name.Length < candidates[best].Name.Length) best = i;
        }
        return best;
    }

    /// <summary>Called by Windows inside the MMDevice API: must return at once and never block (see class remarks).</summary>
    private void OnChanged()
    {
        if (_disposed) return;
        InvalidateCache();
        try { _debounce.Change(800, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void Fire()
    {
        if (_disposed) return;
        try { DevicesChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("DevicesChanged handler failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Apartment.RunOnMta(() =>
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { /* ignore */ }
            _debounce.Dispose();
            lock (_sync) _enumerator.Dispose();
        });
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceService _owner;
        public NotificationClient(DeviceService owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.OnChanged();
        public void OnDeviceRemoved(string deviceId) => _owner.OnChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.OnChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
