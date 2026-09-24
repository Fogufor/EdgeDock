using System.Runtime.InteropServices;
using EdgeDock.Core;
using NAudio.CoreAudioApi;

namespace EdgeDock.Modules.Audio;

/// <summary>
/// Аудиоустройства Windows: перечисление и уведомления — через NAudio (Core Audio),
/// смена устройства по умолчанию — через недокументированный, но стабильный IPolicyConfig
/// (им же пользуется панель звука Windows).
/// </summary>
internal sealed class AudioDevices : IDisposable
{
    /// <summary>Устройство меняется сразу для всех ролей: звук, мультимедиа, связь.</summary>
    public static readonly Role[] Roles = [Role.Console, Role.Multimedia, Role.Communications];

    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDeviceNotificationClient? _notifications;

    /// <summary>Устройство подключили, отключили или сменили устройство по умолчанию (в потоке интерфейса).</summary>
    public event Action? Changed;

    /// <summary>Начать следить за устройствами (IMMNotificationClient). События приходят в поток интерфейса.</summary>
    public void Watch()
    {
        if (_notifications != null) return;
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: true);
        _notifications.DeviceStateChanged += (_, _) => Changed?.Invoke();
        _notifications.DeviceAdded += (_, _) => Changed?.Invoke();
        _notifications.DeviceRemoved += (_, _) => Changed?.Invoke();
        _notifications.DefaultDeviceChanged += (_, _) => Changed?.Invoke();
    }

    public void StopWatching()
    {
        _notifications?.Dispose();
        _notifications = null;
    }

    /// <summary>Подключённые устройства: id и название, как в панели звука Windows.</summary>
    public List<(string Id, string Name)> Active(DataFlow flow)
    {
        var result = new List<(string, string)>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add((device.ID, device.FriendlyName));
        }
        return result;
    }

    /// <summary>Первое подключённое устройство, в названии которого есть pattern (без учёта регистра).</summary>
    public string? Find(DataFlow flow, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        return Active(flow).FirstOrDefault(d => d.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)).Id;
    }

    public string? DefaultId(DataFlow flow, Role role)
    {
        if (!_enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device) || device == null) return null;
        using (device) return device.ID;
    }

    /// <summary>Сделать устройство устройством по умолчанию — для одной роли или для всех.</summary>
    public void SetDefault(string id, Role? onlyRole = null)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in onlyRole is Role single ? [single] : Roles)
            {
                int hr = policy.SetDefaultEndpoint(id, (int)role);
                if (hr != 0) Log.Error($"Звук: Windows не дала сменить устройство по умолчанию (0x{hr:X8}).");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public void Dispose()
    {
        StopWatching();
        _enumerator.Dispose();
    }

    // ----- IPolicyConfig (Windows 7+) -----
    // Порядок методов важен: вызывается только SetDefaultEndpoint, остальные нужны, чтобы совпала таблица методов.

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;
}
