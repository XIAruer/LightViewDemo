using System.Text.Json;
using System.Text.Json.Serialization;

namespace LandCoverLabeler;

public sealed class ClassDef
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("name_zh")] public string NameZh { get; set; } = "";

    /// <summary>BGR, as stored in classes.json (OpenCV convention).</summary>
    [JsonPropertyName("color_bgr")] public int[] ColorBgr { get; set; } = [0, 0, 0];

    [JsonIgnore] public byte B => (byte)ColorBgr[0];
    [JsonIgnore] public byte G => (byte)ColorBgr[1];
    [JsonIgnore] public byte R => (byte)ColorBgr[2];

    [JsonIgnore] public Color WinColor => Color.FromArgb(R, G, B);

    /// <summary>Thin black/white outline so both light and dark palette colours stay visible.</summary>
    [JsonIgnore] public Color WinContrast => (R * 299 + G * 587 + B * 114) / 1000 > 140 ? Color.Black : Color.White;

    [JsonIgnore] public string Display => $"{Id}  {NameZh}";

    public override string ToString() => Display;
}

/// <summary>
/// The class palette, loaded from train/classes.json — the single source of truth shared with
/// the Python trainer and the C# inference side. Falling back to a built-in copy would
/// silently desynchronise the three, so a missing file is a hard error instead.
/// </summary>
public static class Classes
{
    /// <summary>Sentinel written by the eraser / left in untouched areas; skipped by the loss.</summary>
    public const byte Ignore = 255;

    public static ClassDef[] All { get; private set; } = [];
    public static int NumClasses => All.Length;
    public static string? LoadedFrom { get; private set; }

    public static void Load(string path)
    {
        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = new List<ClassDef>();
        foreach (var c in root.GetProperty("classes").EnumerateArray())
        {
            list.Add(new ClassDef
            {
                Id = c.GetProperty("id").GetInt32(),
                Name = c.GetProperty("name").GetString() ?? "",
                NameZh = c.TryGetProperty("name_zh", out var zh) ? zh.GetString() ?? "" : "",
                ColorBgr = c.GetProperty("color_bgr").EnumerateArray().Select(v => v.GetInt32()).ToArray(),
            });
        }

        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (list.Count == 0)
            throw new InvalidDataException($"classes.json 中没有类别: {path}");
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id != i)
                throw new InvalidDataException($"classes.json 的 id 必须从 0 连续编号，但第 {i} 项是 {list[i].Id}");
        }

        All = [.. list];
        LoadedFrom = path;
    }

    /// <summary>Finds classes.json next to the executable (copied from train/), then walks up to the repo root.</summary>
    public static string LocateClassesFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string direct = Path.Combine(dir.FullName, "classes.json");
            if (File.Exists(direct))
                return direct;

            string inTrain = Path.Combine(dir.FullName, "train", "classes.json");
            if (File.Exists(inTrain))
                return inTrain;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 classes.json（应在 train/classes.json）");
    }

    public static ClassDef Get(int id) =>
        id >= 0 && id < All.Length ? All[id] : All[0];
}
