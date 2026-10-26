using System.Text.Json;
using System.Text.Json.Serialization;

namespace LandCover.Core;

/// <summary>
/// Optional sidecar describing what an ONNX model expects and what its outputs mean.
///
/// The pipeline's contract is "input [1,3,H,W] RGB, output [1,C,H,W] logits", but real
/// checkpoints differ in ways that silently produce garbage: their own input normalisation,
/// an output resolution that is not the input resolution, and their own class taxonomy.
/// Putting those facts in <c>&lt;model&gt;.onnx.json</c> keeps the C# generic while making
/// each model's assumptions explicit and checkable.
/// </summary>
public sealed class ModelMetadata
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Tile size this model was trained at; a hint for the CLI/GUI default.</summary>
    [JsonPropertyName("input_size")] public int? InputSize { get; set; }

    /// <summary>"raw" (pixels/255) or "mean_std" (ImageNet-style normalisation).</summary>
    [JsonPropertyName("normalize")] public string Normalize { get; set; } = "raw";

    [JsonPropertyName("mean")] public double[]? Mean { get; set; }
    [JsonPropertyName("std")] public double[]? Std { get; set; }

    /// <summary>Names of this model's output channels, for reporting and error messages.</summary>
    [JsonPropertyName("class_names")] public string[]? ClassNames { get; set; }

    /// <summary>
    /// Model output index -> this project's palette id. Models with their own taxonomy
    /// (e.g. OpenEarthMap's 8 classes) need this, otherwise class ids are read as if they
    /// were palette ids and every label is wrong.
    /// </summary>
    [JsonPropertyName("class_map")] public int[]? ClassMap { get; set; }

    [JsonPropertyName("notes")] public string? Notes { get; set; }

    [JsonIgnore] public string? LoadedFrom { get; private set; }

    public bool UsesMeanStd =>
        string.Equals(Normalize, "mean_std", StringComparison.OrdinalIgnoreCase)
        && Mean is { Length: 3 } && Std is { Length: 3 };

    /// <summary>Loads <c>&lt;modelPath&gt;.json</c>, or null when there is none.</summary>
    public static ModelMetadata? TryLoad(string modelPath)
    {
        foreach (string candidate in new[] { modelPath + ".json", Path.ChangeExtension(modelPath, ".json") })
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                var md = JsonSerializer.Deserialize<ModelMetadata>(File.ReadAllText(candidate));
                if (md is not null)
                {
                    md.LoadedFrom = candidate;
                    md.Validate();
                    return md;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[模型] 无法解析 {candidate}: {ex.Message}");
            }
        }

        return null;
    }

    private void Validate()
    {
        if (UsesMeanStd)
        {
            foreach (double s in Std!)
            {
                if (s <= 0 || !double.IsFinite(s))
                    throw new InvalidDataException($"std 非法: {string.Join(",", Std)}");
            }
        }

        if (ClassMap is not null)
        {
            foreach (int v in ClassMap)
            {
                if (v < 0 || v >= LandCoverPalette.ClassCount)
                    throw new InvalidDataException(
                        $"class_map 里的 {v} 超出调色板 0..{LandCoverPalette.ClassCount - 1}");
            }
        }
    }

    /// <summary>Human-readable one-liner for the console banner.</summary>
    public string Describe(int modelClasses)
    {
        var parts = new List<string> { Name ?? Path.GetFileNameWithoutExtension(LoadedFrom ?? "model") };
        parts.Add($"{modelClasses} 类输出");
        if (ClassMap is { Length: > 0 })
            parts.Add($"class_map 已映射到 {LandCoverPalette.ClassCount} 类调色板");
        if (UsesMeanStd)
            parts.Add("mean/std 归一化");
        if (InputSize is int s)
            parts.Add($"建议 tile={s}");
        return string.Join(" | ", parts);
    }
}
