using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace XCL2.App.Services;

/// <summary>
/// Read-only SMBIOS type 17 metadata. Rates are firmware-reported MT/s, not live clock samples.
/// Capacity is physical installed capacity, not the operating system's usable-memory total.
/// Memory ranks, DIMM count and locator names do not establish active channel count.
/// References: Microsoft GetSystemFirmwareTable; DMTF DSP0134 3.9, sections 7.18 and 7.18.14.
/// </summary>
public static class PerformanceMemoryMetadata
{
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>();

    /// <summary>Call from the hardware sampling worker and cache the result; never on the UI thread.</summary>
    public static IReadOnlyDictionary<string, string> Read()
    {
        if (!OperatingSystem.IsWindows()) return Empty();
        try
        {
            const uint RawSmbios = 0x52534D42; // C/C++ multichar literal 'RSMB', as required by the API.
            var size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
            if (size < 8 || size > MaximumTableBytes) return Empty();
            var bytes = new byte[(int)size];
            if (GetSystemFirmwareTable(RawSmbios, 0, bytes, size) != size) return Empty();
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return Empty(); }
    }

    /// <summary>Parses the complete RawSMBIOSData buffer, including its eight-byte Windows header.</summary>
    public static IReadOnlyDictionary<string, string> Parse(byte[]? rawSmbiosData)
    {
        if (rawSmbiosData == null || rawSmbiosData.Length < 8 || rawSmbiosData.Length > MaximumTableBytes) return Empty();
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(rawSmbiosData.AsSpan(4, 4));
        if (declared > rawSmbiosData.Length - 8) return Empty();
        var end = 8 + (int)declared;
        var manufacturers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rated = new SortedSet<uint>();
        var configured = new SortedSet<uint>();
        var handles = new HashSet<ushort>();
        var installed = 0;
        var allCapacityKnown = true;
        var unknownManufacturer = false;
        var unknownRated = false;
        var unknownConfigured = false;
        ulong capacity = 0;
        var position = 8;
        var records = 0;
        while (position < end)
        {
            if (++records > 4096 || end - position < 4) return Empty();
            var type = rawSmbiosData[position];
            var length = rawSmbiosData[position + 1];
            if (length < 4 || length > end - position) return Empty();
            var stringsStart = position + length;
            var stringsEnd = stringsStart;
            while (stringsEnd + 1 < end && (rawSmbiosData[stringsEnd] != 0 || rawSmbiosData[stringsEnd + 1] != 0)) stringsEnd++;
            if (stringsEnd + 1 >= end) return Empty();
            if (type == 127) break;
            if (type == 17)
            {
                if (length < 0x15) return Empty();
                var record = rawSmbiosData.AsSpan(position, length);
                if (!handles.Add(BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(2, 2)))) return Empty();
                var size = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x0C, 2));
                // Logical nonvolatile devices are backed by physical devices: do not double-count them.
                if (size != 0 && record[0x12] != 0x1F)
                {
                    installed++;
                    var bytes = Capacity(record, size);
                    if (bytes.HasValue) capacity += bytes.Value;
                    else allCapacityKnown = false;
                    var manufacturer = length > 0x17 ? ReadString(rawSmbiosData, stringsStart, stringsEnd, record[0x17]) : null;
                    if (manufacturer != null) manufacturers.Add(manufacturer); else unknownManufacturer = true;
                    var standard = Rate(record, 0x15, 0x54);
                    if (standard.HasValue) rated.Add(standard.Value); else unknownRated = true;
                    var speed = Rate(record, 0x20, 0x58);
                    if (speed.HasValue) configured.Add(speed.Value); else unknownConfigured = true;
                }
            }
            position = stringsEnd + 2;
        }
        var result = new Dictionary<string, string>();
        if (installed == 0) return result;
        if (manufacturers.Count > 0) result["Manufacturer"] = string.Join(" / ", manufacturers) + Partial(unknownManufacturer);
        if (rated.Count > 0) result["StandardRate"] = string.Join(" / ", rated) + " MT/s" + Partial(unknownRated);
        if (configured.Count > 0) result["Speed"] = string.Join(" / ", configured) + " MT/s" + Partial(unknownConfigured);
        if (allCapacityKnown && capacity > 0) result["MemoryCapacity"] = FormatCapacity(capacity);
        return result;
    }

    private static string Partial(bool partial) => partial ? "（部分模块未报告）" : "";

    private static ulong? Capacity(ReadOnlySpan<byte> record, ushort size)
    {
        if (size == ushort.MaxValue) return null;
        if (size == 0x7FFF)
        {
            if (record.Length < 0x20) return null;
            var extended = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x1C, 4));
            return extended == 0 || (extended & 0x80000000) != 0 ? null : (ulong)extended * 1024 * 1024;
        }
        var amount = (uint)(size & 0x7FFF);
        return amount == 0 ? null : (ulong)amount * ((size & 0x8000) != 0 ? 1024UL : 1024UL * 1024);
    }

    private static uint? Rate(ReadOnlySpan<byte> record, int offset, int extendedOffset)
    {
        if (record.Length < offset + 2) return null;
        uint rate = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(offset, 2));
        if (rate == ushort.MaxValue)
        {
            if (record.Length < extendedOffset + 4) return null;
            rate = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(extendedOffset, 4));
            if ((rate & 0x80000000) != 0) return null;
        }
        return rate == 0 ? null : rate;
    }

    private static string? ReadString(byte[] data, int start, int end, byte index)
    {
        if (index == 0) return null;
        for (var number = 1; start < end; number++)
        {
            var stop = start;
            while (stop < end && data[stop] != 0) stop++;
            if (number == index)
            {
                var value = Encoding.ASCII.GetString(data, start, Math.Min(stop - start, 128));
                value = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
                return value.Length == 0 || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ? null : value;
            }
            start = stop + 1;
        }
        return null;
    }

    private static string FormatCapacity(ulong bytes)
    {
        var divisor = bytes >= 1024UL * 1024 * 1024 ? 1024UL * 1024 * 1024 : bytes >= 1024 * 1024 ? 1024UL * 1024 : 1024UL;
        var unit = divisor == 1024UL * 1024 * 1024 ? " GiB" : divisor == 1024UL * 1024 ? " MiB" : " KiB";
        return ((decimal)bytes / divisor).ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint bufferSize);
}
