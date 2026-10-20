// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DIsland.Interop;

namespace DIsland.Services;

public enum Sensor
{
    Microphone,
    Camera,
}

/// <summary>Which apps are using the microphone and camera right now.</summary>
public sealed record SensorUse(IReadOnlyList<string> MicrophoneApps, IReadOnlyList<string> CameraApps)
{
    public static readonly SensorUse None = new(Array.Empty<string>(), Array.Empty<string>());
    public bool MicrophoneInUse => MicrophoneApps.Count > 0;
    public bool CameraInUse => CameraApps.Count > 0;
}

/// <summary>
/// Mic / camera in use, from live sources:
/// <list type="bullet">
/// <item>Microphone: active Core Audio capture sessions on every capture endpoint (the process that
/// owns each one), with session-created and state-changed callbacks.</item>
/// <item>Camera: Media Foundation's sensor activity monitor, which reports the processes streaming
/// from each camera.</item>
/// </list>
/// (The registry's CapabilityAccessManager\ConsentStore usage times were used first, but newer
/// Windows 11 builds stopped updating them; they moved to an admin-only database.)
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _settle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Dictionary<int, string> _names = new();
    private MicrophoneWatcher? _microphone;
    private CameraWatcher? _camera;
    private readonly int _ownPid = Environment.ProcessId;

    public PrivacyService()
    {
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Scan();
        };
    }

    public SensorUse Current { get; private set; } = SensorUse.None;

    /// <summary>Raised on the UI thread with the previous and new state.</summary>
    public event Action<SensorUse, SensorUse>? Changed;

    public void Start()
    {
        if (_microphone is not null || _camera is not null) return;
        // Callbacks arrive on COM worker threads; coalesce and rescan on the UI thread.
        void Poke() => _ui.InvokeAsync(() =>
        {
            _settle.Stop();
            _settle.Start();
        });

        try { _microphone = new MicrophoneWatcher(Poke); }
        catch (Exception ex) { App.LogError(ex); }
        try { _camera = new CameraWatcher(Poke); }
        catch (Exception ex) { App.LogError(ex); }

        Current = Read(); // starting state: shown by the indicator, not announced
    }

    public void Stop()
    {
        _microphone?.Dispose();
        _microphone = null;
        _camera?.Dispose();
        _camera = null;
        _settle.Stop();
        if (Current.MicrophoneInUse || Current.CameraInUse)
        {
            var old = Current;
            Current = SensorUse.None;
            Changed?.Invoke(old, Current);
        }
    }

    public void Dispose() => Stop();

    private void Scan()
    {
        var now = Read();
        var old = Current;
        if (now.MicrophoneApps.SequenceEqual(old.MicrophoneApps) && now.CameraApps.SequenceEqual(old.CameraApps)) return;
        Current = now;
        Changed?.Invoke(old, now);
    }

    private SensorUse Read()
    {
        var use = new SensorUse(
            Names(_microphone?.ActiveProcessIds() ?? Enumerable.Empty<int>()),
            Names(_camera?.ActiveProcessIds() ?? Enumerable.Empty<int>()));
        DebugLog($"read: mic=[{string.Join(", ", use.MicrophoneApps)}] camera=[{string.Join(", ", use.CameraApps)}] " +
                 $"(mic watcher {(_microphone is null ? "off" : "on")}, camera watcher {(_camera is null ? "off" : "on")})");
        return use;
    }

    /// <summary>Debug builds: DISLAND_PRIVACY_LOG=&lt;txt&gt; logs every read and camera report.</summary>
    [Conditional("DEBUG")]
    private static void DebugLog(string line)
    {
        if (Environment.GetEnvironmentVariable("DISLAND_PRIVACY_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
    }

    private List<string> Names(IEnumerable<int> pids) =>
        pids.Where(p => p != 0 && p != _ownPid)
            .Select(ProcessName)
            .Distinct()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// The app's product description ("Discord", "Google Chrome"), else its file name. Apps built on
    /// WebView2 (WhatsApp, Teams, …) capture from a msedgewebview2.exe child; name the app that hosts it.
    /// </summary>
    private string ProcessName(int pid)
    {
        if (_names.TryGetValue(pid, out var cached)) return cached;
        var name = $"Process {pid}";
        var current = pid;
        for (var depth = 0; depth < 4 && ImagePath(current, out var parent) is { } path; depth++)
        {
            if (Path.GetFileName(path).Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) && parent != 0)
            {
                current = parent;
                continue;
            }
            name = Path.GetFileNameWithoutExtension(path);
            try
            {
                if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) name = description;
            }
            catch (Exception) { /* keep the file name */ }
            break;
        }
        if (_names.Count > 256) _names.Clear(); // process ids get reused; keep the cache small
        _names[pid] = name;
        return name;
    }

    /// <summary>A process's executable path and parent process id; null if it can't be opened.</summary>
    private static string? ImagePath(int pid, out int parentPid)
    {
        parentPid = 0;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (NtQueryInformationProcess(handle, 0 /* ProcessBasicInformation */, out var info,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0)
                parentPid = (int)info.InheritedFromUniqueProcessId;
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out PROCESS_BASIC_INFORMATION info,
        int size, out int returned);

    // ---------------------------------------------------------------- microphone

    /// <summary>
    /// Active capture sessions on all capture endpoints. Re-reads on session created / state changed;
    /// a new microphone is picked up at the next change notification from any endpoint.
    /// </summary>
    private sealed class MicrophoneWatcher : IDisposable, IAudioSessionNotification, IMMNotificationClient
    {
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int CLSCTX_ALL = 0x17;

        private readonly Action _changed;
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly object _lock = new();
        private readonly Dictionary<string, IAudioSessionManager2> _managers = new();
        private readonly List<(IAudioSessionControl2 Session, SessionEvents Events)> _sessions = new();
        private bool _disposed;

        public MicrophoneWatcher(Action changed)
        {
            _changed = changed;
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(this);
            Refresh();
        }

        public List<int> ActiveProcessIds()
        {
            Refresh();
            var pids = new List<int>();
            lock (_lock)
            {
                foreach (var (session, _) in _sessions)
                {
                    if (session.GetState(out var state) == 0 && state == 1 /* AudioSessionStateActive */
                        && session.GetProcessId(out var pid) == 0)
                        pids.Add((int)pid);
                }
            }
            return pids;
        }

        /// <summary>Picks up new endpoints and sessions; registers for their notifications.</summary>
        private void Refresh()
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE_ACTIVE, out var collectionPtr) != 0) return;
                var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr);
                Marshal.Release(collectionPtr);
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0 || device is null) continue;
                    device.GetId(out var id);
                    if (!_managers.TryGetValue(id, out var manager))
                    {
                        var iid = typeof(IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0 || obj is not IAudioSessionManager2 m)
                            continue;
                        manager = m;
                        _managers[id] = manager;
                        // Session notifications start only after the session list has been read once.
                        manager.GetSessionEnumerator(out _);
                        manager.RegisterSessionNotification(this);
                    }
                    if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) continue;
                    sessions.GetCount(out var sessionCount);
                    for (var s = 0; s < sessionCount; s++)
                    {
                        if (sessions.GetSession(s, out var control) != 0 || control is not IAudioSessionControl2 session) continue;
                        if (_sessions.Any(t => SameSession(t.Session, session))) continue;
                        var events = new SessionEvents(_changed);
                        session.RegisterAudioSessionNotification(events);
                        _sessions.Add((session, events));
                    }
                }
                // Expired sessions (their process ended) are dropped.
                _sessions.RemoveAll(t =>
                {
                    if (t.Session.GetState(out var state) == 0 && state != 2 /* AudioSessionStateExpired */) return false;
                    t.Session.UnregisterAudioSessionNotification(t.Events);
                    return true;
                });
            }
        }

        private static bool SameSession(IAudioSessionControl2 a, IAudioSessionControl2 b) =>
            a.GetSessionInstanceIdentifier(out var x) == 0 && b.GetSessionInstanceIdentifier(out var y) == 0 && x == y;

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                foreach (var (session, events) in _sessions) session.UnregisterAudioSessionNotification(events);
                _sessions.Clear();
                foreach (var manager in _managers.Values) manager.UnregisterSessionNotification(this);
                _managers.Clear();
            }
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }

        int IAudioSessionNotification.OnSessionCreated(IAudioSessionControl newSession)
        {
            _changed(); // the rescan registers it
            return 0;
        }

        int IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState) { _changed(); return 0; }
        int IMMNotificationClient.OnDeviceAdded(string deviceId) { _changed(); return 0; }
        int IMMNotificationClient.OnDeviceRemoved(string deviceId) { _changed(); return 0; }
        int IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) => 0;
        int IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    /// <summary>Only state changes matter (active / inactive / expired).</summary>
    private sealed class SessionEvents : IAudioSessionEvents
    {
        private readonly Action _changed;
        public SessionEvents(Action changed) => _changed = changed;

        public int OnStateChanged(int newState) { _changed(); return 0; }
        public int OnSessionDisconnected(int reason) { _changed(); return 0; }
        public int OnDisplayNameChanged(string newDisplayName, ref Guid eventContext) => 0;
        public int OnIconPathChanged(string newIconPath, ref Guid eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, int newMute, ref Guid eventContext) => 0;
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, ref Guid eventContext) => 0;
        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => 0;
    }

    // ---------------------------------------------------------------- camera

    /// <summary>Media Foundation's sensor activity monitor: a report whenever camera use changes.</summary>
    private sealed class CameraWatcher : IDisposable, IMFSensorActivitiesReportCallback
    {
        private readonly Action _changed;
        private readonly IMFSensorActivityMonitor _monitor;
        private readonly object _lock = new();
        private List<int> _active = new();

        public CameraWatcher(Action changed)
        {
            _changed = changed;
            Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup");
            Check(MFCreateSensorActivityMonitor(this, out var monitor), "MFCreateSensorActivityMonitor");
            _monitor = monitor!;
            Check(_monitor.Start(), "IMFSensorActivityMonitor.Start");
        }

        public List<int> ActiveProcessIds()
        {
            lock (_lock) return new List<int>(_active);
        }

        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            var active = new List<int>();
            try
            {
                report.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (report.GetActivityReport(i, out var device) != 0 || device is null) continue;
                    device.GetProcessCount(out var processes);
                    for (uint p = 0; p < processes; p++)
                    {
                        if (device.GetProcessActivity(p, out var activity) != 0 || activity is null) continue;
                        if (activity.GetStreamingState(out var streaming) == 0 && streaming
                            && activity.GetProcessId(out var pid) == 0)
                            active.Add((int)pid);
                    }
                }
            }
            catch (Exception)
            {
                return 0; // a malformed report; wait for the next one
            }
            lock (_lock) _active = active;
            DebugLog($"camera report: streaming pids [{string.Join(", ", active)}]");
            _changed();
            return 0;
        }

        public void Dispose()
        {
            _monitor.Stop();
            Marshal.ReleaseComObject(_monitor);
            MFShutdown();
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} failed", hr);
        }

        private const uint MF_VERSION = 0x00020070;
        private const uint MFSTARTUP_LITE = 1;

        [DllImport("mfplat.dll")]
        private static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll")]
        private static extern int MFShutdown();

        [DllImport("mfsensorgroup.dll")]
        private static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback,
            out IMFSensorActivityMonitor? monitor);
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

// ---------------------------------------------------------------- interop (vtable order per SDK headers)

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice? device);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? enumerator);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl? session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
    [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl, repeated: COM interop builds the vtable from this interface's own methods.
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
    [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
}

[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string newDisplayName, ref Guid eventContext);
    [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string newIconPath, ref Guid eventContext);
    [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, ref Guid eventContext);
    [PreserveSig] int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, ref Guid eventContext);
    [PreserveSig] int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext);
    [PreserveSig] int OnStateChanged(int newState);
    [PreserveSig] int OnSessionDisconnected(int disconnectReason);
}

[ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityMonitor
{
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
}

[ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReportCallback
{
    [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
}

[ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReport
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport? report);
    [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string name, out IMFSensorActivityReport? report);
}

[ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityReport
{
    [PreserveSig] int GetFriendlyName(IntPtr name, uint length, out uint written);
    [PreserveSig] int GetSymbolicLink(IntPtr link, uint length, out uint written);
    [PreserveSig] int GetProcessCount(out uint count);
    [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity? activity);
}

[ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorProcessActivity
{
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int GetStreamingState([MarshalAs(UnmanagedType.Bool)] out bool streaming);
    [PreserveSig] int GetStreamingMode(out int mode);
    [PreserveSig] int GetReportTime(out long time);
}
