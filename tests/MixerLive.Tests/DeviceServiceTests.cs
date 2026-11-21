using System.Diagnostics;
using MixerLive.Core.Audio;
using NAudio.CoreAudioApi;
using Xunit;

namespace MixerLive.Tests;

public class DeviceServiceTests
{
    /// <summary>
    /// Regression test for the 2026-11-19 freeze: an audio-driver install restarted the Windows audio
    /// service; Windows delivered "device added" notifications while a device-list refresh was inside
    /// an MMDevice call, and the notification waited for the refresh's lock. Windows' notification
    /// thread then never returned, the refresh never finished, and the mixer hung for good.
    /// Notifications must return at once even while the device lock is held.
    /// </summary>
    [Fact]
    public void Device_notifications_never_wait_for_a_device_list_refresh()
    {
        using var devices = new DeviceService();
        using var fired = new ManualResetEventSlim();
        devices.DevicesChanged += fired.Set;

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var refresh = new Thread(() =>
        {
            lock (devices.SyncRootForTests) // what a refresh holds while it talks to Windows
            {
                holding.Set();
                release.Wait(10_000);
            }
        }) { IsBackground = true };
        refresh.Start();
        Assert.True(holding.Wait(5000));

        var sw = Stopwatch.StartNew();
        var n = devices.NotificationsForTests;
        n.OnDeviceAdded("test-device");
        n.OnDeviceStateChanged("test-device", DeviceState.Active);
        n.OnDeviceRemoved("test-device");
        n.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "test-device");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"notifications blocked for {sw.ElapsedMilliseconds} ms");
        // The debounced change event still arrives (after ~0.8 s), even while the lock is held.
        Assert.True(fired.Wait(5000), "DevicesChanged was not raised");

        release.Set();
        refresh.Join();
    }
}
