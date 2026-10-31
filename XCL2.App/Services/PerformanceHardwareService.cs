using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>只读 Windows PDH / SetupAPI。GPU 与 NPU 按设备类和 LUID 匹配；驱动未提供数据时保留 null。</summary>
internal sealed class PerformanceHardwareService : IDisposable
{
    private readonly Dictionary<string, IntPtr> _counters = new();
    private QueryHandle? _query;
    private bool _attempted;
    private string? _failure;
    private DateTime _lastDevicesAt;
    private IReadOnlyList<Device> _devices = Array.Empty<Device>();
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _metadata
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    private sealed record Device(string Id, string Name, string Kind, string Manufacturer);

    public PerformanceHardwareSnapshot Sample()
    {
        try
        {
            if (!_attempted) Initialize();
            if (OperatingSystem.IsWindows() && (DateTime.UtcNow - _lastDevicesAt).TotalSeconds > 30)
            {
                _devices = EnumerateDevices();
                var metadata = PerformanceHardwareMetadata.Read();
                foreach (var kind in new[] { "GPU", "NPU" })
                {
                    var vendors = _devices.Where(device => device.Kind == kind && !string.IsNullOrWhiteSpace(device.Manufacturer))
                        .Select(device => device.Name + ": " + device.Manufacturer).Distinct().ToArray();
                    if (vendors.Length > 0) metadata[kind] = new Dictionary<string, string> { ["Manufacturer"] = string.Join(" / ", vendors) };
                }
                _metadata = metadata;
                _lastDevicesAt = DateTime.UtcNow;
            }
            if (_query == null || _query.IsInvalid) return Empty(_failure ?? "Windows 性能计数器不可用。");
            var status = PdhCollectQueryData(_query);
            if (status != 0) return Empty("驱动尚未提供性能计数，等待下次采样。");
            var engines = ReadArray("engine");
            var dedicated = ReadArray("dedicated");
            var shared = ReadArray("shared");
            var samples = _devices.Select(device => new PerformanceDeviceSample(device.Id, device.Name, device.Kind,
                PerformanceHardwareMath.BusiestEngine(engines, device.Id),
                PerformanceHardwareMath.AdapterMemory(dedicated, device.Id),
                PerformanceHardwareMath.AdapterMemory(shared, device.Id),
                engines.Any(item => PerformanceHardwareMath.AdapterId(item.Key) == device.Id) ? null :
                    "驱动未返回此设备的有效引擎计数；首次采样需要等待，空闲或未公开计数的设备可能不可用。")).ToArray();
            return new PerformanceHardwareSnapshot(samples, ReadTotal("diskread"), ReadTotal("diskwrite"),
                SumArray("netreceive"), SumArray("netsend"),
                samples.Any(item => item.Kind == "GPU") ? null : "未发现可匹配性能计数的显卡，可能受系统、驱动或权限限制。",
                samples.Any(item => item.Kind == "NPU") ? null : "未发现可读取 LUID 的 NPU；设备不存在或驱动尚未公开监视接口。",
                _counters.ContainsKey("diskread") && _counters.ContainsKey("netreceive") ? null : "部分磁盘/网卡性能计数器不可用。") { Metadata = _metadata };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception)
        {
            return Empty("当前系统无法提供硬件性能计数：" + ex.Message);
        }
    }

    private PerformanceHardwareSnapshot Empty(string reason) => new(Array.Empty<PerformanceDeviceSample>(),
        null, null, null, null, reason, reason, reason) { Metadata = _metadata };

    private void Initialize()
    {
        _attempted = true;
        if (!OperatingSystem.IsWindows()) { _failure = "硬件监视仅在 Windows 上可用。"; return; }
        if (PdhOpenQuery(null, IntPtr.Zero, out var handle) != 0) { _failure = "无法创建 Windows 性能查询。"; return; }
        _query = handle;
        Add("engine", @"\GPU Engine(*)\Utilization Percentage");
        Add("dedicated", @"\GPU Adapter Memory(*)\Dedicated Usage");
        Add("shared", @"\GPU Adapter Memory(*)\Shared Usage");
        Add("diskread", @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        Add("diskwrite", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        Add("netreceive", @"\Network Interface(*)\Bytes Received/sec");
        Add("netsend", @"\Network Interface(*)\Bytes Sent/sec");
        // 速率计数需要两次采样。此处只建基线，不能把尚未计算的值显示成 0%。
        PdhCollectQueryData(_query);
    }

    private void Add(string key, string path)
    {
        if (PdhAddEnglishCounter(_query!, path, IntPtr.Zero, out var counter) == 0) _counters[key] = counter;
    }

    private double? ReadTotal(string key)
    {
        if (!_counters.TryGetValue(key, out var counter) || PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0
            || value.Status > 1 || !double.IsFinite(value.Value) || value.Value < 0) return null;
        return value.Value;
    }

    private double? SumArray(string key)
    {
        var values = ReadArray(key).Values;
        return values.Count == 0 ? null : values.Sum();
    }

    private Dictionary<string, double> ReadArray(string key)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(key, out var counter)) return result;
        uint bytes = 0, count = 0;
        const uint MoreData = 0x800007d2;
        if (PdhGetFormattedCounterArray(counter, 0x8200, ref bytes, out count, IntPtr.Zero) != MoreData || bytes == 0 || bytes > 16 * 1024 * 1024) return result;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x8200, ref bytes, out count, buffer) != 0) return result;
            var size = Marshal.SizeOf<CounterItem>();
            if ((ulong)count * (uint)size > bytes) return result;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, checked((int)i * size)));
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value) || item.Value.Value < 0) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (!string.IsNullOrEmpty(name)) result[name] = item.Value.Value;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private static IReadOnlyList<Device> EnumerateDevices()
    {
        var result = new List<Device>();
        foreach (var (kind, guid) in new[] {
            ("GPU", new Guid("4d36e968-e325-11ce-bfc1-08002be10318")),
            ("NPU", new Guid("f01a9d53-3ff6-48d2-9f97-c8a7004be10c")) })
        {
            var classGuid = guid;
            var devices = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2);
            if (devices == new IntPtr(-1)) continue;
            try
            {
                for (uint index = 0; index < 128; index++)
                {
                    var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                    if (!SetupDiEnumDeviceInfo(devices, index, ref info)) break;
                    var key = new DevicePropertyKey { FormatId = new Guid("60b193cb-5276-4d0f-96fc-f173abad3ec6"), Id = 2 };
                    var luid = new byte[8];
                    if (!SetupDiGetDeviceProperty(devices, ref info, ref key, out _, luid, 8, out var length, 0) || length != 8) continue;
                    var low = BitConverter.ToUInt32(luid, 0);
                    var high = BitConverter.ToUInt32(luid, 4);
                    var id = PerformanceHardwareMath.Luid(high, low);
                    var name = ReadDeviceName(devices, ref info);
                    result.Add(new Device(id, string.IsNullOrWhiteSpace(name) ? kind + " " + id : name, kind,
                        ReadDeviceProperty(devices, ref info, 11))); // SPDRP_MFG
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }
        return result.DistinctBy(item => item.Id).ToArray();
    }

    private static string ReadDeviceName(IntPtr devices, ref DeviceInfo info)
    {
        foreach (uint property in new uint[] { 12, 0 })
        {
            var value = ReadDeviceProperty(devices, ref info, property);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static string ReadDeviceProperty(IntPtr devices, ref DeviceInfo info, uint property)
    {
        var bytes = new byte[2048];
        if (SetupDiGetDeviceRegistryProperty(devices, ref info, property, out var type, bytes, (uint)bytes.Length, out var size)
            && type == 1 && size > 1 && size <= bytes.Length && (size & 1) == 0)
            return Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0');
        return "";
    }

    public void Dispose() { _query?.Dispose(); _query = null; _counters.Clear(); }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo
    { public uint Size; public Guid ClassGuid; public uint DeviceInstance; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DevicePropertyKey { public Guid FormatId; public uint Id; }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr userData, out QueryHandle query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(QueryHandle query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(QueryHandle query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceProperty(IntPtr devices, ref DeviceInfo info, ref DevicePropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devices, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
