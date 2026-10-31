using System.IO;

namespace XCL2.App.Services.Worlds;

public enum WorldMapView { TopDown, HorizontalSlice, VerticalSlice, Isometric, BlockDistribution }

public sealed record WorldMapReadOptions(WorldMapView View = WorldMapView.TopDown, int SliceY = 64, int MinY = -64, int MaxY = 319);
public sealed record WorldBlockCount(string Block, long Count);

/// <summary>Cells are exact saved blocks. Vertical rows run from MaxY down to MinY at SliceZ.</summary>
public sealed record WorldMapVisualization(WorldMapPreview Surface, WorldMapReadOptions Options, int Height,
    WorldMapCell[] Cells, int SliceZ, IReadOnlyList<WorldBlockCount> Blocks)
{
    public int Width => Surface.Width;
    public long NonAirBlocks => Blocks.Sum(b => b.Count);
}

public static partial class WorldMapReader
{
    public const int MaximumDetailedRadius = 64;
    public const int MaximumVerticalSpan = 512;

    public static void ValidateVisualization(int radius, WorldMapReadOptions options)
    {
        if (!Enum.IsDefined(options.View)) throw new ArgumentOutOfRangeException(nameof(options.View));
        if (radius is < 8 or > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(radius), "预览半径应为 8–256 个方块。");
        if (options.View is WorldMapView.Isometric or WorldMapView.BlockDistribution && radius > MaximumDetailedRadius)
            throw new ArgumentOutOfRangeException(nameof(radius), "三维样式和体积方块统计的半径最多 64 格；请先缩小范围。");
        if (options.SliceY is < -2048 or > 2047) throw new ArgumentOutOfRangeException(nameof(options.SliceY), "切片 Y 应在 −2048–2047 之间。");
        if (options.MinY < -2048 || options.MaxY > 2047 || options.MinY > options.MaxY || (long)options.MaxY - options.MinY + 1 > MaximumVerticalSpan)
            throw new ArgumentOutOfRangeException(nameof(options.MinY), "Y 范围应在 −2048–2047 内，起点不大于终点，跨度最多 512 格。");
    }

    /// <summary>Read-only; keeps one bounded NBT chunk in memory and aggregates counts instead of storing a voxel volume.</summary>
    public static WorldMapVisualization ReadVisualization(string worldDirectory, WorldMapDimension dimension, int centerX, int centerZ,
        int radius, WorldMapReadOptions options, CancellationToken cancellationToken = default)
    {
        ValidateVisualization(radius, options);
        cancellationToken.ThrowIfCancellationRequested();
        int width = radius * 2 + 1, minX = centerX - radius, minZ = centerZ - radius;
        int height = options.View == WorldMapView.VerticalSlice ? options.MaxY - options.MinY + 1 : width;
        bool detailed = options.View is WorldMapView.HorizontalSlice or WorldMapView.VerticalSlice or WorldMapView.BlockDistribution;
        var cells = detailed ? Enumerable.Repeat(new WorldMapCell(WorldMapCellKind.Empty), width * height).ToArray() : Array.Empty<WorldMapCell>();
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        void Inspect(NbtTag root, int cx, int cz)
        {
            int firstX = Math.Max(minX, cx * 16), lastX = Math.Min(minX + width, cx * 16 + 16);
            int firstZ = Math.Max(minZ, cz * 16), lastZ = Math.Min(minZ + width, cz * 16 + 16);
            if (options.View == WorldMapView.VerticalSlice && (centerZ < firstZ || centerZ >= lastZ)) return;
            // Commit only after complete validation: a corrupt underground index cannot leave partial counts or pixels behind.
            var pending = new List<(int Index, WorldMapCell Cell)>();
            var chunkCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var section in DecodeSections(root, cx, cz, cancellationToken))
            {
                int low = Math.Max(section.Y * 16, options.View == WorldMapView.HorizontalSlice ? options.SliceY : options.MinY);
                int high = Math.Min(section.Y * 16 + 15, options.View == WorldMapView.HorizontalSlice ? options.SliceY : options.MaxY);
                for (int y = low; y <= high; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int z = options.View == WorldMapView.VerticalSlice ? centerZ : firstZ;
                        z < (options.View == WorldMapView.VerticalSlice ? centerZ + 1 : lastZ); z++)
                        for (int x = firstX; x < lastX; x++)
                        {
                            string block = section.BlockAt(x - cx * 16, y - section.Y * 16, z - cz * 16);
                            if (IsAir(block)) continue;
                            if (options.View == WorldMapView.BlockDistribution)
                                chunkCounts[block] = chunkCounts.GetValueOrDefault(block) + 1;
                            else
                            {
                                int row = options.View == WorldMapView.VerticalSlice ? options.MaxY - y : z - minZ;
                                pending.Add((row * width + x - minX, new(WorldMapCellKind.Surface, y, block)));
                            }
                        }
                }
            }
            if (totals.Count + chunkCounts.Keys.Count(key => !totals.ContainsKey(key)) > 65536)
                throw new InvalidDataException("方块种类超过 65536，请缩小统计范围。");
            foreach (var item in pending) cells[item.Index] = item.Cell;
            foreach (var item in chunkCounts)
            {
                totals[item.Key] = totals.GetValueOrDefault(item.Key) + item.Value;
            }
        }
        var surface = ReadCore(worldDirectory, dimension, centerX, centerZ, radius, cancellationToken, detailed ? Inspect : null);
        if (options.View is WorldMapView.HorizontalSlice or WorldMapView.VerticalSlice)
        {
            for (int row = 0; row < height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    var source = surface.At(minX + x, options.View == WorldMapView.VerticalSlice ? centerZ : minZ + row);
                    if (source.Kind is WorldMapCellKind.Missing or WorldMapCellKind.Unavailable) cells[row * width + x] = new(source.Kind);
                }
            }
        }
        else cells = surface.Cells;
        return new(surface, options, height, cells, centerZ,
            totals.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new WorldBlockCount(p.Key, p.Value)).ToArray());
    }
}
