using System.Buffers.Binary;
using System.Text;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
byte[] Device(ushort handle = 1, ushort size = 16384, ushort rated = 6000, ushort configured = 5600, string? manufacturer = "Example Memory", int length = 0x5C)
{
    var data = new byte[length]; data[0] = 17; data[1] = (byte)length;
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), handle);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0C), size); data[0x12] = 0x22;
    if (length >= 0x17) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x15), rated);
    if (length >= 0x18) data[0x17] = manufacturer == null ? (byte)0 : (byte)1;
    if (length >= 0x22) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x20), configured);
    return data.Concat(manufacturer == null ? new byte[] { 0, 0 } : Encoding.ASCII.GetBytes(manufacturer + "\0\0")).ToArray();
}
byte[] Raw(params byte[][] devices)
{
    var table = devices.SelectMany(item => item).Concat(new byte[] { 127, 4, 0xFF, 0xFF, 0, 0 }).ToArray();
    var raw = new byte[8 + table.Length]; raw[1] = 3; raw[2] = 9;
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)table.Length); table.CopyTo(raw, 8); return raw;
}
var normal = PerformanceMemoryMetadata.Parse(Raw(Device(), Device(handle: 2)));
Check(normal["Manufacturer"] == "Example Memory", "Manufacturer deduplicates.");
Check(normal["StandardRate"] == "6000 MT/s", "Rated speed uses MT/s, not MHz.");
Check(normal["Speed"] == "5600 MT/s", "Configured speed stays distinct from rated speed.");
Check(normal["MemoryCapacity"] == "32 GiB", "Installed capacity sums physical modules.");
Check(!normal.ContainsKey("MemoryChannels"), "Two modules never imply dual channel.");
Check(PerformanceMemoryMetadata.Parse(Raw(Device(size: 0))).Count == 0, "Empty sockets must not invent installed memory.");
var unknown = PerformanceMemoryMetadata.Parse(Raw(Device(size: ushort.MaxValue, rated: 0, configured: 0, manufacturer: "Unknown")));
Check(unknown.Count == 0, "Unknown fields are absent, not zero.");
var extended = Device(size: 0x7FFF, rated: ushort.MaxValue, configured: ushort.MaxValue);
BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(0x1C), 65536);
BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(0x54), 70000);
BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(0x58), 66000);
var extendedResult = PerformanceMemoryMetadata.Parse(Raw(extended));
Check(extendedResult["MemoryCapacity"] == "64 GiB", "Extended capacity uses MiB.");
Check(extendedResult["StandardRate"] == "70000 MT/s" && extendedResult["Speed"] == "66000 MT/s", "Extended rates are consumed only for FFFF sentinels.");
BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(0x54), 0x80000001);
Check(!PerformanceMemoryMetadata.Parse(Raw(extended)).ContainsKey("StandardRate"), "Reserved extended-rate bit rejects invalid data.");
var legacy = PerformanceMemoryMetadata.Parse(Raw(Device(length: 0x15)));
Check(!legacy.ContainsKey("StandardRate") && !legacy.ContainsKey("Speed"), "Older short records do not read beyond formatted length.");
var mixed = PerformanceMemoryMetadata.Parse(Raw(Device(), Device(handle: 2, size: ushort.MaxValue, rated: 0, configured: 0, manufacturer: null)));
Check(!mixed.ContainsKey("MemoryCapacity"), "A partial capacity sum must not pretend to be the full installed capacity.");
Check(mixed["StandardRate"].Contains("部分模块未报告"), "Mixed unknown rates are clearly qualified.");
var logical = Device(handle: 2, size: ushort.MaxValue); logical[0x12] = 0x1F;
Check(PerformanceMemoryMetadata.Parse(Raw(Device(), logical))["MemoryCapacity"] == "16 GiB", "Logical nonvolatile entries are not physical modules.");
Check(PerformanceMemoryMetadata.Parse(Raw(Device(), Device())).Count == 0, "Duplicate handles must not double-count installed capacity.");
var valid = Raw(Device());
for (var i = 0; i < valid.Length; i++)
    Check(PerformanceMemoryMetadata.Parse(valid[..i]).Count == 0, "Every truncated complete-buffer prefix is rejected.");
var malformed = Raw(Device()); malformed[9] = 3;
Check(PerformanceMemoryMetadata.Parse(malformed).Count == 0, "Malformed record length is rejected.");
var missingTerminator = Raw(Device()); missingTerminator[^1] = 1;
Check(PerformanceMemoryMetadata.Parse(missingTerminator).Count == 0, "Missing terminator does not expose partial data.");
Check(PerformanceMemoryMetadata.Parse(null).Count == 0, "Null buffer is unavailable.");
Console.WriteLine($"Memory metadata regression passed: {checks} checks.");
