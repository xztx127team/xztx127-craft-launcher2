using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services.Worlds;

public sealed record WorldMapDimension(string Id, string DisplayName, string RegionDirectory);
public enum WorldMapCellKind { Missing, Empty, Surface, Unavailable }
public sealed record WorldMapCell(WorldMapCellKind Kind, int Y = 0, string Block = "");
public sealed record WorldMapPreview(string Dimension, int MinX, int MinZ, int Width, WorldMapCell[] Cells,
    int LoadedChunks, int MissingChunks, int UnavailableChunks, IReadOnlyList<string> Warnings)
{
    public WorldMapCell At(int x, int z) => Cells[(z - MinZ) * Width + x - MinX];
}

/// <summary>Read-only bounded Anvil surface map. It never creates or regenerates chunks.</summary>
public static class WorldMapReader
{
    public const int MaximumRadius = 256;
    private const int MaxChunkBytes = 8 * 1024 * 1024;
    private const long MaxPreviewBytes = 256L * 1024 * 1024;
    public static IReadOnlyList<WorldMapDimension> Dimensions(string worldDirectory)
    {
        var root = Path.GetFullPath(worldDirectory); WorldArchiveService.RejectLinks(root);
        var result = new List<WorldMapDimension>
        {
            new("minecraft:overworld", "主世界", Path.Combine(root, "region")),
            new("minecraft:the_nether", "下界", Path.Combine(root, "DIM-1", "region")),
            new("minecraft:the_end", "末地", Path.Combine(root, "DIM1", "region"))
        };
        // Mod/data-pack dimensions use dimensions/<namespace>/<dimension path>/region.
        var dimensions = Path.Combine(root, "dimensions"); int visited = 0;
        void Walk(string directory, int depth)
        {
            if (++visited > 256 || depth > 8) return;
            WorldArchiveService.RejectLinks(directory);
            if (depth >= 2 && Directory.Exists(Path.Combine(directory, "region")))
            {
                var relative = Path.GetRelativePath(dimensions, directory).Replace('\\', '/');
                int split = relative.IndexOf('/');
                if (split > 0) { var id = relative[..split] + ":" + relative[(split + 1)..]; result.Add(new(id, id, Path.Combine(directory, "region"))); }
                return;
            }
            foreach (var child in Directory.EnumerateDirectories(directory).Take(256))
                if (Path.GetFileName(child) is not "region" and not "entities" and not "poi") Walk(child, depth + 1);
        }
        if (Directory.Exists(dimensions)) Walk(dimensions, 0);
        return result;
    }

    public static WorldMapPreview Read(string worldDirectory, WorldMapDimension dimension, int centerX, int centerZ,
        int radius, CancellationToken cancellationToken = default)
    {
        if (radius is < 8 or > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(radius), "预览半径应为 8–256 个方块。");
        if (centerX < -30_000_000 || centerX > 30_000_000 || centerZ < -30_000_000 || centerZ > 30_000_000)
            throw new ArgumentOutOfRangeException(nameof(centerX), "中心坐标应在 ±30000000 内。");
        string root = Path.GetFullPath(worldDirectory), regionDirectory = Path.GetFullPath(dimension.RegionDirectory);
        if (!regionDirectory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("维度目录必须位于当前存档中。");
        WorldArchiveService.RejectLinks(regionDirectory);
        int minX = centerX - radius, minZ = centerZ - radius, width = radius * 2 + 1;
        var cells = Enumerable.Repeat(new WorldMapCell(WorldMapCellKind.Missing), width * width).ToArray();
        var regions = new Dictionary<(int, int), RegionFile?>(); var warnings = new List<string>();
        int loaded = 0, missing = 0, unavailable = 0; long decodedBytes = 0;
        void Warning(string message) { if (warnings.Count < 6 && !warnings.Contains(message)) warnings.Add(message); }
        void ConsumeBytes(int count)
        {
            decodedBytes += count;
            if (decodedBytes > MaxPreviewBytes) throw new PreviewLimitException("地图解压总量超过 256 MiB，请减小预览半径。");
        }
        try
        {
            for (int cz = FloorDiv(minZ, 16); cz <= FloorDiv(minZ + width - 1, 16); cz++)
                for (int cx = FloorDiv(minX, 16); cx <= FloorDiv(minX + width - 1, 16); cx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WorldMapCell[]? top = null; var kind = WorldMapCellKind.Missing;
                    try
                    {
                        var regionKey = (FloorDiv(cx, 32), FloorDiv(cz, 32));
                        if (!regions.TryGetValue(regionKey, out var region))
                        {
                            string path = Path.Combine(regionDirectory, $"r.{regionKey.Item1}.{regionKey.Item2}.mca");
                            WorldArchiveService.RejectLinks(path);
                            regions.Add(regionKey, region = File.Exists(path) ? new RegionFile(path) : null);
                        }
                        var document = region?.ReadChunk(cx, cz, cancellationToken, ConsumeBytes);
                        if (region != null && document != null)
                        {
                            top = DecodeSurface(document.Root, cx, cz, cancellationToken); loaded++;
                        }
                        else missing++;
                    }
                    catch (PreviewLimitException) { throw; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or OverflowException)
                    { kind = WorldMapCellKind.Unavailable; unavailable++; Warning($"区块 {cx}, {cz}：{ex.Message}"); }
                    int startX = Math.Max(minX, cx * 16), endX = Math.Min(minX + width, cx * 16 + 16);
                    int startZ = Math.Max(minZ, cz * 16), endZ = Math.Min(minZ + width, cz * 16 + 16);
                    for (int z = startZ; z < endZ; z++)
                        for (int x = startX; x < endX; x++)
                            cells[(z - minZ) * width + x - minX] = top?[(z - cz * 16) * 16 + x - cx * 16] ?? new(kind);
                }
        }
        finally { foreach (var region in regions.Values) region?.Dispose(); }
        return new(dimension.Id, minX, minZ, width, cells, loaded, missing, unavailable, warnings);
    }

    public static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value + 1) / divisor - 1;
    private sealed class PreviewLimitException(string message) : IOException(message) { }

    internal static WorldMapCell[] DecodeSurface(NbtTag root, int chunkX, int chunkZ, CancellationToken cancellationToken = default)
    {
        var chunk = root.Get("Level") ?? root;
        if (chunk.Get("xPos") == null || chunk.Get("zPos") == null) throw new InvalidDataException("区块缺少 xPos / zPos 坐标。");
        if (chunk.Get("xPos") is { } x && x.Number != chunkX || chunk.Get("zPos") is { } z && z.Number != chunkZ)
            throw new InvalidDataException("区块坐标与 region 索引不一致。");
        long dataVersion = root.Get("DataVersion")?.Number ?? chunk.Get("DataVersion")?.Number ?? 0;
        if (chunk.Get("Status") is { Type: not 8 }) throw new InvalidDataException("区块 Status 必须为字符串。");
        string status = chunk.Get("Status")?.Text ?? "";
        bool legacyComplete = root.Get("Level") != null && dataVersion is > 0 and < 1952 && status is ("fullchunk" or "postprocessed" or "mobs_spawned");
        if (status.Length > 0 && status is not "full" and not "minecraft:full" && !legacyComplete) throw new NotSupportedException("区块尚未完成生成（" + status + "）。");
        var sections = chunk.Get("sections") ?? chunk.Get("Sections");
        if (sections == null) throw new NotSupportedException("缺少 sections；仅支持 Java 1.13+ 的 Anvil 方块调色板。");
        if (sections.Value is not NbtList sectionList || sectionList.ElementType != 10 && sectionList.Items.Count != 0)
            throw new InvalidDataException("sections 必须为 Compound 列表。");
        if (sections.Items.Count > 256) throw new InvalidDataException("区块 section 数量超过上限。");
        var top = Enumerable.Repeat(new WorldMapCell(WorldMapCellKind.Empty), 256).ToArray();
        var sectionYs = new HashSet<int>();
        foreach (var section in sections.Items.OrderByDescending(s => s.Get("Y")?.Number ?? long.MinValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (section.Type != 10) throw new InvalidDataException("section 必须为 Compound。");
            if (section.Get("Y") == null) throw new InvalidDataException("section 缺少 Y 坐标。");
            int sy = checked((int)section.Get("Y")!.Number);
            if (sy is < -128 or > 127 || !sectionYs.Add(sy)) throw new InvalidDataException("section Y 无效或重复。");
            var states = section.Get("block_states");
            if (states != null && states.Type != 10) throw new InvalidDataException("block_states 必须为 Compound。");
            var paletteTag = states?.Get("palette") ?? section.Get("Palette");
            if (paletteTag == null)
            {
                if (section.Get("Blocks") != null) throw new NotSupportedException("1.12 及更早数字方块 ID 存档暂不支持；请在游戏中升级存档副本。");
                if (states != null || section.Get("BlockStates") != null) throw new InvalidDataException("方块数据缺少调色板。");
                continue; // Light-only sections in older Anvil chunks have no blocks.
            }
            if (paletteTag.Value is not NbtList paletteList || paletteList.ElementType != 10)
                throw new InvalidDataException("方块调色板必须为 Compound 列表。");
            string[] palette = paletteTag.Items.Select(item => item.Get("Name")?.Text is { Length: > 0 } name ? name : throw new InvalidDataException("方块调色板缺少 Name。")).ToArray();
            if (palette.Length is < 1 or > 4096) throw new InvalidDataException("方块调色板大小无效。");
            var dataTag = states?.Get("data") ?? section.Get("BlockStates");
            if (dataTag != null && dataTag.Value is not long[]) throw new InvalidDataException("方块 packed data 必须为 Long Array。");
            var data = dataTag?.Value as long[];
            int bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(palette.Length)));
            int continuousWords = (4096 * bits + 63) / 64, paddedWords = (4096 + 64 / bits - 1) / (64 / bits);
            bool padded = states != null || dataVersion >= 2529;
            if (data == null && palette.Length != 1) throw new InvalidDataException("多方块调色板缺少 packed data。");
            if (data != null)
            {
                // Also accept old chunks copied into upgraded worlds by inspecting exact lengths.
                if (continuousWords != paddedWords && data.Length == continuousWords) padded = false;
                else if (continuousWords != paddedWords && data.Length == paddedWords) padded = true;
                if (data.Length != (padded ? paddedWords : continuousWords)) throw new InvalidDataException("方块 packed data 长度无效。");
            }
            for (int column = 0; column < 256; column++)
            {
                if ((column & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (top[column].Kind == WorldMapCellKind.Surface) continue;
                for (int y = 15; y >= 0; y--)
                {
                    int id = data == null ? 0 : PackedIndex(data, bits, y * 256 + column, padded);
                    if (id >= palette.Length) throw new InvalidDataException("方块调色板索引越界。");
                    string block = palette[id];
                    if (block is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or "minecraft:structure_void") continue;
                    top[column] = new(WorldMapCellKind.Surface, sy * 16 + y, block); break;
                }
            }
        }
        return top;
    }

    internal static int PackedIndex(long[] data, int bits, int index, bool padded)
    {
        if (bits is < 4 or > 12 || index is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(index));
        int word, shift;
        if (padded) { int perWord = 64 / bits; word = index / perWord; shift = index % perWord * bits; }
        else { int bit = index * bits; word = bit / 64; shift = bit % 64; }
        if (word >= data.Length) throw new InvalidDataException("方块数据不完整。");
        ulong value = unchecked((ulong)data[word]) >> shift;
        if (!padded && shift + bits > 64)
        { if (word + 1 >= data.Length) throw new InvalidDataException("跨字方块数据不完整。"); value |= unchecked((ulong)data[word + 1]) << (64 - shift); }
        return (int)(value & ((1UL << bits) - 1));
    }

    private sealed class RegionFile : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _locations = new byte[4096];
        public RegionFile(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try { if (_stream.Length < 8192) throw new InvalidDataException("region 文件缺少 8 KiB 头。"); _stream.ReadExactly(_locations); }
            catch { _stream.Dispose(); throw; }
        }
        public NbtDocument? ReadChunk(int x, int z, CancellationToken cancellationToken, Action<int> consumeBytes)
        {
            int index = (x & 31) + (z & 31) * 32;
            int entry = BinaryPrimitives.ReadInt32BigEndian(_locations.AsSpan(index * 4, 4));
            int sector = (int)((uint)entry >> 8), count = entry & 255;
            if (sector == 0 && count == 0) return null;
            if (sector < 2 || count == 0 || ((long)sector + count) * 4096 > _stream.Length) throw new InvalidDataException("region sector 指针越界。");
            _stream.Position = (long)sector * 4096;
            Span<byte> size = stackalloc byte[4]; _stream.ReadExactly(size);
            int length = BinaryPrimitives.ReadInt32BigEndian(size);
            if (length < 1 || length > count * 4096 - 4 || length > 4 * 1024 * 1024) throw new InvalidDataException("region 区块长度无效。");
            int compression = _stream.ReadByte();
            if ((compression & 128) != 0) throw new NotSupportedException("外置 .mcc 区块暂不支持。");
            if (compression is < 1 or > 3) throw new NotSupportedException($"区块压缩格式 {compression} 暂不支持（包括 LZ4）。");
            if (length < 2) throw new InvalidDataException("region 区块缺少 NBT 数据。");
            var compressed = new byte[length - 1]; _stream.ReadExactly(compressed); cancellationToken.ThrowIfCancellationRequested();
            using var input = new MemoryStream(compressed, writable: false);
            using Stream decoded = compression == 1 ? new GZipStream(input, CompressionMode.Decompress) : compression == 2 ? new ZLibStream(input, CompressionMode.Decompress) : input;
            using var counted = new CountingReadStream(decoded, consumeBytes);
            return NbtDocument.ReadUncompressed(counted, MaxChunkBytes, cancellationToken);
        }
        public void Dispose() => _stream.Dispose();
    }
    private sealed class CountingReadStream(Stream inner, Action<int> consumeBytes) : Stream
    {
        public long Total { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); Total += n; consumeBytes(n); return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static (byte R, byte G, byte B) BlockColor(string block)
    {
        string name = block.StartsWith("minecraft:", StringComparison.Ordinal) ? block[10..] : block;
        if (name is "water" or "bubble_column") return (49, 107, 201);
        if (name.Contains("lava", StringComparison.Ordinal)) return (230, 92, 24);
        if (name.Contains("snow", StringComparison.Ordinal)) return (234, 240, 244);
        if (name.Contains("ice", StringComparison.Ordinal)) return (153, 198, 230);
        if (name.Contains("leaves", StringComparison.Ordinal) || name.Contains("moss", StringComparison.Ordinal)) return (65, 114, 48);
        if (name.Contains("grass", StringComparison.Ordinal) || name is "fern" or "sugar_cane") return (98, 145, 63);
        if (name.Contains("sand", StringComparison.Ordinal) && !name.Contains("soul", StringComparison.Ordinal)) return (216, 205, 152);
        if (name.Contains("dirt", StringComparison.Ordinal) || name.Contains("mud", StringComparison.Ordinal) || name.Contains("soul", StringComparison.Ordinal)) return (128, 98, 68);
        if (name.Contains("end_stone", StringComparison.Ordinal)) return (211, 218, 163);
        if (name.Contains("netherrack", StringComparison.Ordinal)) return (127, 62, 58);
        if (name.Contains("stone", StringComparison.Ordinal) || name.Contains("ore", StringComparison.Ordinal) || name.Contains("bedrock", StringComparison.Ordinal)) return (123, 127, 130);
        if (name.Contains("log", StringComparison.Ordinal) || name.Contains("planks", StringComparison.Ordinal)) return (156, 122, 78);
        uint hash = 2166136261; foreach (char c in block) hash = (hash ^ c) * 16777619;
        return ((byte)(75 + hash % 130), (byte)(75 + (hash >> 8) % 130), (byte)(75 + (hash >> 16) % 130));
    }
}
