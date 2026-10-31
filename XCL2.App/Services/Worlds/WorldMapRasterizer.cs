namespace XCL2.App.Services.Worlds;

public sealed record WorldMapRaster(int Width, int Height, byte[] Pixels);

/// <summary>Bounded software renderer, safe to run on a worker thread. Colors identify saved block types, not game textures.</summary>
public static class WorldMapRasterizer
{
    public static WorldMapRaster Render(WorldMapVisualization view, bool heightMap, CancellationToken cancellationToken = default)
    {
        if (view.Options.View == WorldMapView.Isometric) return Isometric(view, cancellationToken);
        int width = view.Width, height = view.Height;
        var pixels = new byte[checked(width * height * 4)];
        int min = int.MaxValue, max = int.MinValue;
        foreach (var cell in view.Cells) if (cell.Kind == WorldMapCellKind.Surface) { min = Math.Min(min, cell.Y); max = Math.Max(max, cell.Y); }
        for (int row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var cell = view.Cells[row * width + x];
                (byte R, byte G, byte B) color;
                if (cell.Kind == WorldMapCellKind.Surface)
                {
                    if (heightMap) { byte gray = (byte)(55 + (cell.Y - min) * 190L / Math.Max(1, max - min)); color = (gray, gray, gray); }
                    else color = WorldMapReader.BlockColor(cell.Block);
                }
                else
                {
                    byte tone = (byte)(((x / 8 + row / 8) & 1) == 0 ? 44 : 55);
                    color = cell.Kind switch { WorldMapCellKind.Empty => ((byte)23, (byte)27, (byte)31), WorldMapCellKind.Unavailable => ((byte)(tone + 45), tone, tone), _ => (tone, tone, tone) };
                }
                Put(pixels, row * width + x, color);
            }
        }
        return new(width, height, pixels);
    }

    private static WorldMapRaster Isometric(WorldMapVisualization view, CancellationToken cancellationToken)
    {
        int size = view.Width;
        var surfaces = view.Cells.Where(c => c.Kind == WorldMapCellKind.Surface).ToArray();
        int min = surfaces.Length == 0 ? 0 : surfaces.Min(c => c.Y), max = surfaces.Length == 0 ? 0 : surfaces.Max(c => c.Y);
        // Keep even modded 4096-block height ranges bounded. This is a height-field projection, not a fabricated underground volume.
        const double halfX = 5, halfZ = 2.5;
        double elevation = Math.Min(1.5, 280.0 / Math.Max(1, max - min));
        int width = (int)Math.Ceiling((size + 1) * halfX * 2 + 40), height = (int)Math.Ceiling((size + 1) * halfZ * 2 + (max - min) * elevation + 70);
        var pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < width * height; i++) Put(pixels, i, (24, 28, 34));
        double ox = width / 2.0, oy = 22 + (max - min) * elevation;
        void Polygon((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d, (byte R, byte G, byte B) color)
        {
            var points = new[] { a, b, c, d };
            int first = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y))), last = Math.Min(height - 1, (int)Math.Ceiling(points.Max(p => p.Y)));
            Span<double> intersections = stackalloc double[4];
            for (int y = first; y <= last; y++)
            {
                int count = 0; double sample = y + .5;
                for (int p = 0; p < 4; p++)
                {
                    var u = points[p]; var v = points[(p + 1) % 4];
                    if ((u.Y <= sample && sample < v.Y) || (v.Y <= sample && sample < u.Y))
                        intersections[count++] = u.X + (sample - u.Y) * (v.X - u.X) / (v.Y - u.Y);
                }
                if (count < 2) continue;
                double lo = intersections[0], hi = intersections[0];
                for (int p = 1; p < count; p++) { lo = Math.Min(lo, intersections[p]); hi = Math.Max(hi, intersections[p]); }
                for (int x = Math.Max(0, (int)Math.Ceiling(lo - .5)); x <= Math.Min(width - 1, (int)Math.Floor(hi - .5)); x++) Put(pixels, y * width + x, color);
            }
        }
        for (int depth = 0; depth < size * 2 - 1; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = Math.Max(0, depth - size + 1); x <= Math.Min(size - 1, depth); x++)
            {
                int z = depth - x; var cell = view.Cells[z * size + x];
                double px = ox + (x - z) * halfX, baseline = oy + (x + z) * halfZ + 18;
                double py = oy + (x + z) * halfZ - (cell.Kind == WorldMapCellKind.Surface ? (cell.Y - min) * elevation : 0);
                var color = cell.Kind == WorldMapCellKind.Surface ? WorldMapReader.BlockColor(cell.Block) : cell.Kind == WorldMapCellKind.Unavailable ? ((byte)95, (byte)40, (byte)40) : ((byte)53, (byte)58, (byte)64);
                if (cell.Kind == WorldMapCellKind.Surface)
                {
                    Polygon((px - halfX, py + halfZ), (px, py + halfZ * 2), (px, baseline + halfZ * 2), (px - halfX, baseline + halfZ), Shade(color, .56));
                    Polygon((px, py + halfZ * 2), (px + halfX, py + halfZ), (px + halfX, baseline + halfZ), (px, baseline + halfZ * 2), Shade(color, .75));
                }
                Polygon((px, py), (px + halfX, py + halfZ), (px, py + halfZ * 2), (px - halfX, py + halfZ), color);
            }
        }
        return new(width, height, pixels);
    }
    private static (byte R, byte G, byte B) Shade((byte R, byte G, byte B) color, double factor) => ((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
    private static void Put(byte[] pixels, int index, (byte R, byte G, byte B) color)
    { pixels[index * 4] = color.B; pixels[index * 4 + 1] = color.G; pixels[index * 4 + 2] = color.R; pixels[index * 4 + 3] = 255; }
}
