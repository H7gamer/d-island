// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DIsland.Services;

/// <summary>
/// Do Not Disturb (Focus) on/off. Windows has no public API for it; the shell publishes the state
/// as the WNF state WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED (0 = off, otherwise a profile:
/// 1 = priority only, 2 = alarms only), and ntdll lets any process subscribe to changes.
/// Undocumented, so every failure just disables the feature.
/// </summary>
public sealed class FocusService : IDisposable
{
    private const ulong QuietHoursProfile = 0x0D83063EA3BF1C75;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private WnfCallback? _callback; // must stay alive while subscribed
    private IntPtr _subscription;
    private int? _profile;

    /// <summary>
    /// User Do Not Disturb turned on / off (UI thread). Only profile 1, the one the Do Not Disturb toggle
    /// sets: Windows' automatic rules switch to profile 2 ("alarms only") on their own, e.g. for the
    /// few seconds the Snipping Tool overlay is open after PrtSc, or while a game is full screen, and
    /// those shouldn't announce themselves.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>Any quiet profile is active (Windows is holding notifications back), user or automatic.</summary>
    public bool IsOn => _profile is > 0;

    /// <summary>The user's Do Not Disturb (profile 1).</summary>
    public bool IsUserDnd => _profile == 1;

    public void Start()
    {
        if (_subscription != IntPtr.Zero) return;
        try
        {
            _profile = Query(out var stamp);
            _callback = OnNotify;
            // Subscribing at the current change stamp delivers only later changes.
            var status = RtlSubscribeWnfStateChangeNotification(out _subscription, QuietHoursProfile, stamp,
                _callback, IntPtr.Zero, IntPtr.Zero, 0, 0);
            if (status != 0) throw new InvalidOperationException($"WNF subscribe failed (0x{status:X8})");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _subscription = IntPtr.Zero;
            _callback = null;
        }
    }

    public void Stop()
    {
        if (_subscription == IntPtr.Zero) return;
        RtlUnsubscribeWnfStateChangeNotification(_subscription);
        _subscription = IntPtr.Zero;
        _callback = null;
    }

    public void Dispose() => Stop();

    private int OnNotify(ulong stateName, uint changeStamp, IntPtr typeId, IntPtr context, IntPtr buffer, uint bufferSize)
    {
        // Runs on a thread-pool thread.
        if (bufferSize >= 4)
        {
            var profile = Marshal.ReadInt32(buffer);
            _ui.InvokeAsync(() =>
            {
                var wasOn = IsUserDnd;
                _profile = profile;
                if (IsUserDnd != wasOn) Changed?.Invoke(IsUserDnd);
            });
        }
        return 0;
    }

    private static int? Query(out uint stamp)
    {
        var state = QuietHoursProfile;
        var buffer = new byte[4];
        var size = (uint)buffer.Length;
        var status = NtQueryWnfStateData(ref state, IntPtr.Zero, IntPtr.Zero, out stamp, buffer, ref size);
        if (status != 0) throw new InvalidOperationException($"WNF query failed (0x{status:X8})");
        return size >= 4 ? BitConverter.ToInt32(buffer, 0) : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WnfCallback(ulong stateName, uint changeStamp, IntPtr typeId, IntPtr context, IntPtr buffer, uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
        out uint changeStamp, byte[] buffer, ref uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlSubscribeWnfStateChangeNotification(out IntPtr subscription, ulong stateName,
        uint changeStamp, WnfCallback callback, IntPtr context, IntPtr typeId, uint serializationGroup, uint flags);

    [DllImport("ntdll.dll")]
    private static extern int RtlUnsubscribeWnfStateChangeNotification(IntPtr subscription);
}
