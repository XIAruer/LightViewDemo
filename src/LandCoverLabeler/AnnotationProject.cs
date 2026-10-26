using System.Text.Json;
using System.Text.Json.Serialization;

namespace LandCoverLabeler;

/// <summary>
/// One annotation job: which raster is being labelled and where its label tiles live.
/// Layout on disk:
/// <code>
/// annotations/&lt;name&gt;/
///   project.json      source raster + size + tile size
///   labels/x{tx}_y{ty}.png
/// </code>
/// </summary>
public sealed class AnnotationProject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("tile_size")] public int TileSize { get; set; } = 512;
    [JsonPropertyName("created_utc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    [JsonIgnore] public string RootDir { get; private set; } = "";
    [JsonIgnore] public string LabelsDir => Path.Combine(RootDir, "labels");
    [JsonIgnore] public string ProjectFile => Path.Combine(RootDir, "project.json");

    [JsonIgnore] public LabelStore Labels { get; private set; } = null!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AnnotationProject Create(string rootDir, string sourcePath, int width, int height, int tileSize)
    {
        var p = new AnnotationProject
        {
            Name = Path.GetFileName(rootDir),
            Source = Path.GetFullPath(sourcePath),
            Width = width,
            Height = height,
            TileSize = tileSize,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
        };
        p.RootDir = Path.GetFullPath(rootDir);
        Directory.CreateDirectory(p.LabelsDir);
        p.Labels = new LabelStore(width, height, p.LabelsDir, tileSize);
        p.Save();
        return p;
    }

    public static AnnotationProject Load(string projectFile)
    {
        string json = File.ReadAllText(projectFile);
        var p = JsonSerializer.Deserialize<AnnotationProject>(json, JsonOpts)
            ?? throw new InvalidDataException($"无法解析工程文件: {projectFile}");

        p.RootDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        if (p.TileSize <= 0)
            p.TileSize = 512;
        if (p.Width <= 0 || p.Height <= 0)
            throw new InvalidDataException($"工程文件缺少影像尺寸: {projectFile}");

        Directory.CreateDirectory(p.LabelsDir);
        p.Labels = new LabelStore(p.Width, p.Height, p.LabelsDir, p.TileSize);
        return p;
    }

    public void Save()
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(ProjectFile, JsonSerializer.Serialize(this, JsonOpts));
    }

    public bool SourceExists => File.Exists(Source);

    /// <summary>Tile indices that hold any labelled pixel — the training set for this project.</summary>
    public List<(int Tx, int Ty)> LabeledTiles()
    {
        var result = new List<(int, int)>();
        for (int ty = 0; ty < Labels.TilesY; ty++)
            for (int tx = 0; tx < Labels.TilesX; tx++)
                if (Labels.TileHasData(tx, ty))
                    result.Add((tx, ty));
        return result;
    }

    /// <summary>
    /// Bounding box of the labelled tiles in raster pixels, padded by <paramref name="padTiles"/>.
    ///
    /// The vector export works on this window rather than the whole plane: on a several-hundred-MP raster the
    /// annotated corner is a few thousand pixels across, and materialising the full plane just to
    /// trace a handful of polygons would cost hundreds of megabytes. The padding guarantees every
    /// polygon is complete — a shape cannot extend past a tile that has no paint in it.
    /// Returns an empty box when nothing is labelled.
    /// </summary>
    public (int X, int Y, int Width, int Height) LabeledBounds(int padTiles = 1)
    {
        var tiles = LabeledTiles();
        if (tiles.Count == 0)
            return (0, 0, 0, 0);

        int ts = Labels.TileSize;
        int minTx = tiles.Min(t => t.Tx), maxTx = tiles.Max(t => t.Tx);
        int minTy = tiles.Min(t => t.Ty), maxTy = tiles.Max(t => t.Ty);

        int x = Math.Max(0, (minTx - padTiles) * ts);
        int y = Math.Max(0, (minTy - padTiles) * ts);
        int right = Math.Min(Width, (maxTx + 1 + padTiles) * ts);
        int bottom = Math.Min(Height, (maxTy + 1 + padTiles) * ts);

        return (x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
