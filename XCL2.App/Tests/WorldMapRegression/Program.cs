using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using XCL2.App.Services.Worlds;

// Dependency-free console regressions. Fixtures are actual big-endian NBT in sector-aligned MCA
// files, serialized independently of NbtDocument.Save and the reader's packed-index arithmetic.
internal static class Program
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "XCL2-WorldMapRegression-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Directory.CreateDirectory(Scratch);
        try
        {
            Run("MCA gzip, zlib and raw decode the same real chunk", CompressionFormats);
            Run("Negative region and chunk coordinates use the correct header slot", NegativeCoordinates);
            Run("Modern single palette without data supports negative section Y", ModernSinglePalette);
            Run("Surface chooses highest non-air across unsorted sections", HighestSurface);
            Run("1.13 continuous 5-bit entries cross 64-bit boundaries", () => PackedPalette(false, 1631));
            Run("1.16 padded 5-bit entries never cross a word", () => PackedPalette(true, 2567));
            Run("Missing region and absent chunk remain missing", MissingChunks);
            Run("Incomplete generation is unavailable and leaves a warning", UnfinishedChunk);
            Run("1.13 completed statuses require legacy Level and matching DataVersion", LegacyCompleteStatuses);
            Run("Malformed headers, sector pointers, lengths and NBT are unavailable", CorruptChunks);
            Run("Malformed sections, block_states, palette and data types are unavailable", MalformedTypes);
            Run("Block data without palette is unavailable while light-only sections remain valid", MissingPalette);
            Run("External MCC and LZ4 are explicitly unavailable", UnsupportedCompression);
            Run("Radius and center bounds are enforced", Bounds);
            Run("Cancellation propagates from preview, surface and NBT reads", Cancellation);
            Run("Decompressed NBT allocation cap rejects a compressed bomb", DecompressionLimit);
            Run("Failed NBT decoding still consumes the total preview budget", FailedChunkBudget);
            Run("Horizontal slice exposes underground blocks instead of surface", HorizontalSlice);
            Run("Vertical slice uses fixed Z and descending Y without transposing X", VerticalSlice);
            Run("Distribution clips all XYZ bounds and excludes air", BlockDistribution);
            Run("Structure void is counted and sliced as a real non-air block", StructureVoid);
            Run("Corrupt underground palettes do not commit partial pixels or counts", CorruptUnderground);
            Run("Detailed limits, cancellation and isometric pixels are bounded", DetailedLimitsAndRaster);
            Console.WriteLine($"WorldMapRegression: {_passed} passed, {_failed} failed.");
            return _failed == 0 ? 0 : 1;
        }
        finally { Directory.Delete(Scratch, recursive: true); }
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
    }

    private static void CompressionFormats()
    {
        foreach (int compression in new[] { 1, 2, 3 })
        {
            string world = NewWorld();
            WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(4, "minecraft:grass_block")), compression);
            var map = Preview(world, 7, 7);
            Equal(1, map.LoadedChunks, "loaded chunks");
            Equal(0, map.UnavailableChunks, "unavailable chunks");
            Equal(0, map.Warnings.Count, "warnings");
            Cell(map.At(7, 7), WorldMapCellKind.Surface, 79, "minecraft:grass_block");
            Equal(17, map.Width, "diameter includes center");
        }
    }

    private static void NegativeCoordinates()
    {
        Equal(-1, WorldMapReader.FloorDiv(-1, 16), "floor -1 / 16");
        Equal(-1, WorldMapReader.FloorDiv(-16, 16), "floor -16 / 16");
        Equal(-2, WorldMapReader.FloorDiv(-17, 16), "floor -17 / 16");
        string world = NewWorld();
        // Chunk -33,-1 belongs to r.-2.-1.mca, slot 1023, not r.-1.0 or slot zero.
        WriteChunk(world, -33, -1, Chunk(-33, -1, true, 3465, SingleSection(0, "minecraft:stone")), 2);
        var map = Preview(world, -521, -9);
        Equal(1, map.LoadedChunks, "negative loaded chunk");
        Cell(map.At(-521, -9), WorldMapCellKind.Surface, 15, "minecraft:stone");
        Cell(map.At(-529, -9), WorldMapCellKind.Missing);
        Cell(map.At(-521, -17), WorldMapCellKind.Missing);
        Equal(3, map.MissingChunks, "three neighbouring chunks absent");
    }

    private static void ModernSinglePalette()
    {
        string world = NewWorld();
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(-4, "minecraft:deepslate")), 2);
        Cell(Preview(world, 7, 7).At(0, 0), WorldMapCellKind.Surface, -49, "minecraft:deepslate");
        var empty = RoundTrip(Chunk(0, 0, true, 3465, SingleSection(-4, "minecraft:air")));
        foreach (var cell in WorldMapReader.DecodeSurface(empty.Root, 0, 0)) Cell(cell, WorldMapCellKind.Empty);
        var invalid = Chunk(0, 0, true, 3465, Section(0, new[] { "minecraft:air", "minecraft:stone" }, null, true));
        Throws<InvalidDataException>(() => WorldMapReader.DecodeSurface(RoundTrip(invalid).Root, 0, 0), "multi-palette requires data");
    }

    private static void HighestSurface()
    {
        int[] ids = new int[4096];
        ids[2 * 256] = 2; // Column 0: air above grass at section 2, local Y 2.
        ids[10 * 256 + 1] = 1; // Column 1: cave air does not hide stone below.
        var root = Chunk(0, 0, true, 3465,
            SingleSection(-1, "minecraft:stone"),
            Section(2, new[] { "minecraft:air", "minecraft:cave_air", "minecraft:grass_block" }, Pack(ids, 4, true), true),
            SingleSection(4, "minecraft:void_air"),
            SingleSection(3, "minecraft:structure_void"));
        string world = NewWorld(); WriteChunk(world, 0, 0, root, 1);
        var map = Preview(world, 7, 7);
        Cell(map.At(0, 0), WorldMapCellKind.Surface, 34, "minecraft:grass_block");
        Cell(map.At(1, 0), WorldMapCellKind.Surface, -1, "minecraft:stone");
        Cell(map.At(15, 15), WorldMapCellKind.Surface, -1, "minecraft:stone");
    }

    private static void PackedPalette(bool padded, int dataVersion)
    {
        string[] palette = Enumerable.Range(0, 17).Select(i => i == 0 ? "minecraft:air" : "minecraft:test_" + i).ToArray();
        int[] ids = Enumerable.Range(0, 4096).Select(i => (i * 7 + 3) % 17).ToArray();
        ids[12] = 16; // Fifth bit must be read from the next long in the continuous layout.
        long[] packed = Pack(ids, 5, padded);
        Equal(padded ? 342 : 320, packed.Length, "5-bit word count");
        for (int i = 0; i < ids.Length; i++)
            Equal(ids[i], WorldMapReader.PackedIndex(packed, 5, i, padded), "packed index " + i);
        // Continuous index 12 begins at bit 60 and spans two longs; the fixture contains nonzero high bits.
        Equal(ids[12], WorldMapReader.PackedIndex(packed, 5, 12, padded), "64-bit boundary");
        string world = NewWorld();
        WriteChunk(world, 0, 0, Chunk(0, 0, false, dataVersion, Section(1, palette, packed, false)), 2);
        var map = Preview(world, 7, 7);
        Equal(1, map.LoadedChunks, "packed chunk loaded");
        var slice = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.HorizontalSlice, 16));
        Cell(slice.Cells[(0 - slice.Surface.MinZ) * slice.Width + 12 - slice.Surface.MinX], WorldMapCellKind.Surface, 16, palette[16]);
        var counts = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.BlockDistribution, MinY: 16, MaxY: 31));
        Equal((long)ids.Count(id => id != 0), counts.NonAirBlocks, "packed volume count");
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                int localY = 15;
                while (localY >= 0 && ids[localY * 256 + z * 16 + x] == 0) localY--;
                int id = ids[localY * 256 + z * 16 + x];
                Cell(map.At(x, z), WorldMapCellKind.Surface, 16 + localY, palette[id]);
            }
    }

    private static void MissingChunks()
    {
        string world = NewWorld();
        var absentRegion = Preview(world, 7, 7);
        Equal(4, absentRegion.MissingChunks, "missing region chunks");
        File.WriteAllBytes(Path.Combine(world, "region", "r.0.0.mca"), new byte[8192]);
        var absentSlot = Preview(world, 7, 7);
        Equal(4, absentSlot.MissingChunks, "zero header entries");
        Equal(0, absentSlot.UnavailableChunks, "absence is not corruption");
        foreach (var cell in absentSlot.Cells) Cell(cell, WorldMapCellKind.Missing);
    }

    private static void UnfinishedChunk()
    {
        string world = NewWorld();
        var root = Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone"));
        root.Compound["Status"] = Str("minecraft:noise");
        WriteChunk(world, 0, 0, root, 2);
        Unavailable(Preview(world, 7, 7), "unfinished chunk");
    }

    private static void CorruptChunks()
    {
        void Test(Action<string> corrupt, string reason)
        {
            string world = NewWorld();
            WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone")), 3);
            corrupt(Path.Combine(world, "region", "r.0.0.mca"));
            var map = Preview(world, 7, 7);
            Check(map.UnavailableChunks > 0 && map.Warnings.Count > 0, reason + " must be unavailable with warning");
            Cell(map.At(7, 7), WorldMapCellKind.Unavailable);
        }
        Test(path => File.WriteAllBytes(path, new byte[4096]), "short MCA header");
        Test(path => PatchInt(path, 0, (1 << 8) | 1), "sector pointing into header");
        Test(path => PatchInt(path, 0, (255 << 8) | 1), "sector beyond EOF");
        Test(path => PatchInt(path, 8192, 4093), "length beyond allocated sector");
        Test(path => PatchInt(path, 8192, 1), "NBT payload missing");
        Test(path => { using var stream = File.OpenWrite(path); stream.Position = 8197; stream.WriteByte(8); }, "non-compound NBT root");
        string mismatch = NewWorld();
        WriteChunk(mismatch, 0, 0, Chunk(1, 0, true, 3465, SingleSection(0, "minecraft:stone")), 2);
        Unavailable(Preview(mismatch, 7, 7), "header/NBT coordinate mismatch");
        string invalidPalette = NewWorld();
        WriteChunk(invalidPalette, 0, 0, Chunk(0, 0, true, 3465,
            Section(0, new[] { "minecraft:air", "minecraft:stone" }, new long[1], true)), 2);
        Unavailable(Preview(invalidPalette, 7, 7), "invalid packed data length");
    }

    private static void LegacyCompleteStatuses()
    {
        foreach (string status in new[] { "fullchunk", "postprocessed", "mobs_spawned" })
        {
            foreach (int version in new[] { 1, 1631, 1951 })
            {
                string world = NewWorld();
                var root = Chunk(0, 0, false, version, Section(0, new[] { "minecraft:stone" }, null, false));
                root.Get("Level")!.Compound["Status"] = Str(status);
                WriteChunk(world, 0, 0, root, 2);
                var map = Preview(world, 7, 7);
                Equal(1, map.LoadedChunks, $"legacy {status} / DataVersion {version}");
                Cell(map.At(7, 7), WorldMapCellKind.Surface, 15, "minecraft:stone");
            }
            foreach (var (version, modern) in new[] { (0, false), (1952, false), (3465, false), (1631, true) })
            {
                string world = NewWorld();
                var root = Chunk(0, 0, modern, version, Section(0, new[] { "minecraft:stone" }, null, modern));
                (root.Get("Level") ?? root).Compound["Status"] = Str(status);
                WriteChunk(world, 0, 0, root, 2);
                Unavailable(Preview(world, 7, 7), $"legacy status outside supported schema/version: {status}/{version}/{modern}");
            }
        }
        string incompleteWorld = NewWorld();
        var incomplete = Chunk(0, 0, false, 1631, Section(0, new[] { "minecraft:stone" }, null, false));
        incomplete.Get("Level")!.Compound["Status"] = Str("noise");
        WriteChunk(incompleteWorld, 0, 0, incomplete, 2);
        Unavailable(Preview(incompleteWorld, 7, 7), "1.13 incomplete noise status");
    }

    private static void UnsupportedCompression()
    {
        foreach (byte marker in new byte[] { 0x82, 4 })
        {
            string world = NewWorld();
            WriteRegionPayload(world, 0, 0, marker, new byte[] { 0 });
            Unavailable(Preview(world, 7, 7), marker == 0x82 ? "external MCC" : "LZ4");
        }
    }

    private static void MalformedTypes()
    {
        void Test(Action<NbtTag> corrupt, string reason)
        {
            string world = NewWorld();
            var root = Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone"));
            corrupt(root); WriteChunk(world, 0, 0, root, 2);
            Unavailable(Preview(world, 7, 7), reason);
        }
        Test(root => root.Compound["sections"] = Str("wrong"), "sections string");
        Test(root => root.Compound["sections"] = new(9, new NbtList(3, new List<NbtTag> { new(3, 0) })), "sections integer list");
        Test(root => root.Get("sections")!.Items[0].Compound["block_states"] = Str("wrong"), "block_states string");
        Test(root => root.Get("sections")!.Items[0].Get("block_states")!.Compound["palette"] = Str("wrong"), "palette string");
        Test(root => root.Get("sections")!.Items[0].Get("block_states")!.Compound["palette"] = new(9, new NbtList(3, new List<NbtTag> { new(3, 0) })), "palette integer list");
        Test(root => root.Get("sections")!.Items[0].Get("block_states")!.Compound["data"] = new(7, new byte[4096]), "data byte array");
    }

    private static void Bounds()
    {
        string world = NewWorld();
        var dimension = Dimension(world);
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.Read(world, dimension, 0, 0, 7), "minimum radius");
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.Read(world, dimension, 0, 0, 257), "maximum radius");
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.Read(world, dimension, 30_000_001, 0, 8), "center range");
        var maximum = WorldMapReader.Read(world, dimension, 0, 0, 256);
        Equal(513, maximum.Width, "maximum width");
        Equal(513 * 513, maximum.Cells.Length, "bounded cell count");
        var outside = new WorldMapDimension("test:outside", "outside", Path.Combine(Scratch, "elsewhere", "region"));
        Throws<UnauthorizedAccessException>(() => WorldMapReader.Read(world, outside, 0, 0, 8), "dimension outside world");
    }

    private static void MissingPalette()
    {
        foreach (bool modern in new[] { true, false })
        {
            string world = NewWorld();
            var section = Section(0, new[] { "minecraft:stone" }, new long[1024], modern);
            if (modern) section.Get("block_states")!.Compound.Remove("palette");
            else section.Compound.Remove("Palette");
            WriteChunk(world, 0, 0, Chunk(0, 0, modern, modern ? 3465 : 1631, section), 2);
            Unavailable(Preview(world, 7, 7), modern ? "block_states without palette" : "BlockStates without Palette");
        }
        string emptyStatesWorld = NewWorld();
        var emptyStates = Compound(("Y", new(1, (sbyte)0)), ("block_states", Compound()));
        WriteChunk(emptyStatesWorld, 0, 0, Chunk(0, 0, true, 3465, emptyStates), 2);
        Unavailable(Preview(emptyStatesWorld, 7, 7), "empty modern block_states without palette");

        string lightOnlyWorld = NewWorld();
        var lightOnly = Compound(("Y", new(1, (sbyte)0)), ("SkyLight", new(7, new byte[2048])));
        WriteChunk(lightOnlyWorld, 0, 0, Chunk(0, 0, false, 1631, lightOnly), 2);
        var map = Preview(lightOnlyWorld, 7, 7);
        Equal(1, map.LoadedChunks, "legacy light-only section loaded");
        Cell(map.At(7, 7), WorldMapCellKind.Empty);
    }

    private static void Cancellation()
    {
        string world = NewWorld();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => WorldMapReader.Read(world, Dimension(world), 7, 7, 8, cancellation.Token), "preview cancellation");
        var root = Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone"));
        Throws<OperationCanceledException>(() => WorldMapReader.DecodeSurface(root, 0, 0, cancellation.Token), "surface cancellation");
        using var bytes = new MemoryStream(Serialize(root));
        Throws<OperationCanceledException>(() => NbtDocument.ReadUncompressed(bytes, 8 * 1024 * 1024, cancellation.Token), "NBT pre-cancellation");
        using var duringRead = new CancellationTokenSource();
        using var stream = new CancelOnReadStream(Serialize(root), duringRead);
        Throws<OperationCanceledException>(() => NbtDocument.ReadUncompressed(stream, 8 * 1024 * 1024, duringRead.Token), "NBT cancellation between reads");
    }

    private static void DecompressionLimit()
    {
        // Gzip is only a few KiB, but expands beyond the per-chunk 8 MiB allocation cap.
        string world = NewWorld();
        var root = Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone"));
        root.Compound["Padding"] = new(7, new byte[8 * 1024 * 1024]);
        WriteChunk(world, 0, 0, root, 1);
        Check(new FileInfo(Path.Combine(world, "region", "r.0.0.mca")).Length < 100_000, "fixture must be compressed");
        Unavailable(Preview(world, 7, 7), "8 MiB decompression cap");
        using var small = new MemoryStream(new byte[1025]);
        Throws<InvalidDataException>(() => NbtDocument.ReadUncompressed(small, 1024), "explicit allocation cap");
    }

    private static void FailedChunkBudget()
    {
        // Exactly 8 MiB of zeros passes the per-chunk byte cap but fails NBT parsing.
        // 32 failed chunks consume exactly 256 MiB; reading chunk 33 must stop the preview.
        string world = NewWorld();
        byte[] payload;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(new byte[8 * 1024 * 1024]);
            payload = output.ToArray();
        }
        for (int x = -16; x <= 16; x++) WriteRegionPayload(world, x, 0, 1, payload);
        try { WorldMapReader.Read(world, Dimension(world), 0, 0, 256); }
        catch (IOException ex) { Check(ex.Message.Contains("256", StringComparison.Ordinal), "total limit failure must identify 256 MiB"); return; }
        throw new Exception("Failed NBT chunks bypassed the 256 MiB total decompression cap.");
    }

    private static void HorizontalSlice()
    {
        string world = NewWorld();
        int[] ids = new int[4096]; ids[5 * 256 + 7 * 16 + 3] = 1;
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465,
            SingleSection(2, "minecraft:grass_block"),
            Section(-1, new[] { "minecraft:air", "minecraft:diamond_ore" }, Pack(ids, 4, true), true)), 2);
        var slice = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.HorizontalSlice, -11));
        Cell(slice.Surface.At(3, 7), WorldMapCellKind.Surface, 47, "minecraft:grass_block");
        Cell(slice.Cells[(7 - slice.Surface.MinZ) * slice.Width + 3 - slice.Surface.MinX], WorldMapCellKind.Surface, -11, "minecraft:diamond_ore");
        Cell(slice.Cells[(7 - slice.Surface.MinZ) * slice.Width + 4 - slice.Surface.MinX], WorldMapCellKind.Empty);
        Cell(slice.Cells[0], WorldMapCellKind.Missing);
    }

    private static void VerticalSlice()
    {
        string world = NewWorld(); int[] ids = new int[4096];
        ids[2 * 256 + 7 * 16 + 3] = 1;
        ids[3 * 256 + 8 * 16 + 3] = 1; // Adjacent Z must not leak into the plane.
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, Section(0, new[] { "minecraft:air", "minecraft:gold_ore" }, Pack(ids, 4, true), true)), 2);
        var slice = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.VerticalSlice, MinY: -2, MaxY: 5));
        Equal(8, slice.Height, "vertical extent");
        Equal(7, slice.SliceZ, "fixed Z");
        Cell(slice.Cells[(5 - 2) * slice.Width + 3 - slice.Surface.MinX], WorldMapCellKind.Surface, 2, "minecraft:gold_ore");
        Cell(slice.Cells[(5 - 3) * slice.Width + 3 - slice.Surface.MinX], WorldMapCellKind.Empty);
        Cell(slice.Cells[0], WorldMapCellKind.Missing);
        var raster = WorldMapRasterizer.Render(slice, false);
        Equal(17, raster.Width, "slice raster width"); Equal(8, raster.Height, "slice raster height");
    }

    private static void BlockDistribution()
    {
        string world = NewWorld();
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465,
            SingleSection(0, "minecraft:stone"), SingleSection(1, "minecraft:dirt"), SingleSection(2, "minecraft:air")), 2);
        // 17x17 bounds start at 8,8: only 8x8 columns intersect the saved 16x16 chunk.
        var stats = WorldMapReader.ReadVisualization(world, Dimension(world), 16, 16, 8, new(WorldMapView.BlockDistribution, MinY: 14, MaxY: 17));
        Equal(256L, stats.NonAirBlocks, "64 columns * 4 layers");
        Equal(128L, stats.Blocks.Single(b => b.Block == "minecraft:stone").Count, "stone clipped to 2 layers");
        Equal(128L, stats.Blocks.Single(b => b.Block == "minecraft:dirt").Count, "dirt clipped to 2 layers");
        var air = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.BlockDistribution, MinY: 32, MaxY: 47));
        Equal(0L, air.NonAirBlocks, "air excluded");
    }

    private static void StructureVoid()
    {
        string world = NewWorld();
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:structure_void")), 2);
        var stats = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8,
            new(WorldMapView.BlockDistribution, MinY: 0, MaxY: 15));
        Equal(4096L, stats.NonAirBlocks, "structure void is not air");
        Equal(4096L, stats.Blocks.Single(b => b.Block == "minecraft:structure_void").Count, "structure void exact count");
        Cell(stats.Surface.At(7, 7), WorldMapCellKind.Empty); // Remains invisible in the surface preview.
        var slice = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.HorizontalSlice, 5));
        Cell(slice.Cells[(7 - slice.Surface.MinZ) * slice.Width + 7 - slice.Surface.MinX],
            WorldMapCellKind.Surface, 5, "minecraft:structure_void");
    }

    private static void CorruptUnderground()
    {
        string world = NewWorld(); int[] ids = new int[4096]; ids[100] = 15;
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(2, "minecraft:stone"),
            Section(0, new[] { "minecraft:dirt" }, Pack(ids, 4, true), true)), 2);
        var stats = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.BlockDistribution, MinY: 0, MaxY: 47));
        Equal(0L, stats.NonAirBlocks, "corrupt chunk must not commit partial counts");
        Equal(1, stats.Surface.UnavailableChunks, "bad underground index unavailable");
        var slice = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.HorizontalSlice, 0));
        Cell(slice.Cells[(7 - slice.Surface.MinZ) * slice.Width + 7 - slice.Surface.MinX], WorldMapCellKind.Unavailable);
    }

    private static void DetailedLimitsAndRaster()
    {
        string world = NewWorld();
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.ValidateVisualization(65, new(WorldMapView.Isometric)), "isometric radius cap");
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.ValidateVisualization(65, new(WorldMapView.BlockDistribution)), "statistics radius cap");
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.ValidateVisualization(8, new(WorldMapView.VerticalSlice, MinY: -64, MaxY: 448)), "vertical span cap");
        Throws<ArgumentOutOfRangeException>(() => WorldMapReader.ValidateVisualization(8, new(WorldMapView.HorizontalSlice, -2049)), "slice Y cap");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => WorldMapReader.ReadVisualization(world, Dimension(world), 0, 0, 8, new(), cancellation.Token), "detailed pre-cancellation");
        WriteChunk(world, 0, 0, Chunk(0, 0, true, 3465, SingleSection(0, "minecraft:stone")), 2);
        var projection = WorldMapReader.ReadVisualization(world, Dimension(world), 7, 7, 8, new(WorldMapView.Isometric));
        var raster = WorldMapRasterizer.Render(projection, false);
        Check(raster.Width > projection.Width && raster.Height > projection.Height, "isometric projection uses transformed coordinates");
        Equal(raster.Width * raster.Height * 4, raster.Pixels.Length, "bounded raster buffer");
        Check(raster.Pixels.Where((_, index) => index % 4 == 2).Distinct().Count() > 2, "saved terrain produces non-flat image");
        Throws<OperationCanceledException>(() => WorldMapRasterizer.Render(projection, false, cancellation.Token), "render cancellation");
    }

    private static string NewWorld()
    {
        string world = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(world, "region")); return world;
    }
    private static WorldMapDimension Dimension(string world) => new("minecraft:overworld", "主世界", Path.Combine(world, "region"));
    private static WorldMapPreview Preview(string world, int x, int z) => WorldMapReader.Read(world, Dimension(world), x, z, 8);
    private static void Unavailable(WorldMapPreview map, string reason)
    {
        Equal(1, map.UnavailableChunks, reason + " unavailable count");
        Equal(0, map.LoadedChunks, reason + " loaded count");
        Check(map.Warnings.Count > 0, reason + " warning");
        Cell(map.At(7, 7), WorldMapCellKind.Unavailable);
    }
    private static void Cell(WorldMapCell cell, WorldMapCellKind kind, int y = 0, string block = "")
    { Equal(kind, cell.Kind, "cell kind"); if (kind == WorldMapCellKind.Surface) { Equal(y, cell.Y, "surface Y"); Equal(block, cell.Block, "surface block"); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message + ": expected " + typeof(T).Name); }

    private static NbtTag Compound(params (string Key, NbtTag Value)[] fields) => new(10, fields.ToDictionary(f => f.Key, f => f.Value));
    private static NbtTag Str(string text) => new(8, text);
    private static NbtTag List(params NbtTag[] items) => new(9, new NbtList(10, items.ToList()));
    private static NbtTag SingleSection(int y, string block) => Section(y, new[] { block }, null, true);
    private static NbtTag Section(int y, string[] palette, long[]? data, bool modern)
    {
        var paletteTag = List(palette.Select(name => Compound(("Name", Str(name)))).ToArray());
        var section = Compound(("Y", new(1, unchecked((sbyte)y))));
        if (modern)
        {
            var states = Compound(("palette", paletteTag));
            if (data != null) states.Compound["data"] = new(12, data);
            section.Compound["block_states"] = states;
        }
        else
        {
            section.Compound["Palette"] = paletteTag;
            if (data != null) section.Compound["BlockStates"] = new(12, data);
        }
        return section;
    }
    private static NbtTag Chunk(int x, int z, bool modern, int dataVersion, params NbtTag[] sections)
    {
        var chunk = Compound(("xPos", new(3, x)), ("zPos", new(3, z)), ("Status", Str("minecraft:full")), (modern ? "sections" : "Sections", List(sections)));
        var root = modern ? chunk : Compound(("Level", chunk));
        root.Compound["DataVersion"] = new(3, dataVersion); return root;
    }

    private static long[] Pack(int[] ids, int bits, bool padded)
    {
        // Bit-by-bit fixture writer, rather than duplicating PackedIndex's shifts/masks.
        int perWord = 64 / bits;
        var words = new ulong[padded ? (ids.Length + perWord - 1) / perWord : (ids.Length * bits + 63) / 64];
        for (int i = 0; i < ids.Length; i++)
            for (int bit = 0; bit < bits; bit++)
                if ((ids[i] & (1 << bit)) != 0)
                {
                    int address = padded ? i / perWord * 64 + i % perWord * bits + bit : i * bits + bit;
                    words[address / 64] |= 1UL << (address % 64);
                }
        return words.Select(word => unchecked((long)word)).ToArray();
    }
    private static NbtDocument RoundTrip(NbtTag root)
    { using var bytes = new MemoryStream(Serialize(root)); return NbtDocument.ReadUncompressed(bytes); }
    private static byte[] Serialize(NbtTag root)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(10); WriteString(stream, ""); WritePayload(stream, root); return stream.ToArray();
    }
    private static void WriteString(Stream stream, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); WriteNumber(stream, bytes.Length, 2); stream.Write(bytes); }
    private static void WriteNumber(Stream stream, long value, int width)
    { for (int shift = (width - 1) * 8; shift >= 0; shift -= 8) stream.WriteByte(unchecked((byte)(value >> shift))); }
    private static void WritePayload(Stream stream, NbtTag tag)
    {
        switch (tag.Type)
        {
            case 1: WriteNumber(stream, (sbyte)tag.Value, 1); break;
            case 3: WriteNumber(stream, (int)tag.Value, 4); break;
            case 7: var bytes = (byte[])tag.Value; WriteNumber(stream, bytes.Length, 4); stream.Write(bytes); break;
            case 8: WriteString(stream, (string)tag.Value); break;
            case 9:
                var list = (NbtList)tag.Value; stream.WriteByte(list.ElementType); WriteNumber(stream, list.Items.Count, 4);
                foreach (var item in list.Items) WritePayload(stream, item); break;
            case 10:
                foreach (var pair in tag.Compound) { stream.WriteByte(pair.Value.Type); WriteString(stream, pair.Key); WritePayload(stream, pair.Value); }
                stream.WriteByte(0); break;
            case 12:
                var longs = (long[])tag.Value; WriteNumber(stream, longs.Length, 4);
                foreach (long number in longs) WriteNumber(stream, number, 8); break;
            default: throw new NotSupportedException("Fixture writer tag " + tag.Type);
        }
    }
    private static void WriteChunk(string world, int x, int z, NbtTag root, int compression)
    {
        byte[] raw = Serialize(root), payload;
        if (compression == 3) payload = raw;
        else
        {
            using var output = new MemoryStream();
            using (Stream encoder = compression == 1 ? new GZipStream(output, CompressionLevel.SmallestSize, true) : new ZLibStream(output, CompressionLevel.SmallestSize, true)) encoder.Write(raw);
            payload = output.ToArray();
        }
        WriteRegionPayload(world, x, z, (byte)compression, payload);
    }
    private static void WriteRegionPayload(string world, int x, int z, byte marker, byte[] payload)
    {
        int rx = (int)Math.Floor(x / 32.0), rz = (int)Math.Floor(z / 32.0);
        int lx = x - rx * 32, lz = z - rz * 32;
        int sectors = (payload.Length + 5 + 4095) / 4096;
        string path = Path.Combine(world, "region", $"r.{rx}.{rz}.mca");
        byte[] existing = File.Exists(path) ? File.ReadAllBytes(path) : new byte[8192];
        int firstSector = existing.Length / 4096;
        var region = new byte[existing.Length + sectors * 4096]; existing.CopyTo(region, 0);
        int offset = (lz * 32 + lx) * 4;
        // Header: 3-byte sector offset followed by the allocated sector count.
        region[offset] = (byte)(firstSector >> 16); region[offset + 1] = (byte)(firstSector >> 8);
        region[offset + 2] = (byte)firstSector; region[offset + 3] = checked((byte)sectors);
        int start = firstSector * 4096;
        BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(start, 4), payload.Length + 1);
        region[start + 4] = marker; payload.CopyTo(region, start + 5);
        File.WriteAllBytes(path, region);
    }
    private static void PatchInt(string path, long offset, int number)
    { using var stream = File.OpenWrite(path); stream.Position = offset; Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, number); stream.Write(bytes); }
    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        { int read = base.Read(buffer, offset, count); cancellation.Cancel(); return read; }
        public override int Read(Span<byte> buffer)
        { int read = base.Read(buffer); cancellation.Cancel(); return read; }
    }
}
