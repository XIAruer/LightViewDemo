namespace LandCover.Core;

/// <summary>
/// A model file that is deliberately not committed (they are hundreds of megabytes), plus what
/// is needed to fetch and verify it. The repository carries only the small
/// <c>&lt;model&gt;.onnx.json</c> sidecars, so a downloaded model behaves exactly like a local one.
/// </summary>
public sealed record ModelEntry(
    string Id,
    string FileName,
    string DisplayName,
    string Url,
    long SizeBytes,
    string Sha256,
    string License,
    string Note)
{
    /// <summary>Path relative to <see cref="RepoLayout.ModelsDir"/>, using native separators.</summary>
    public string RelativePath => FileName.Replace('/', Path.DirectorySeparatorChar);

    public string SizeText => SizeBytes >= 1L << 30
        ? $"{SizeBytes / (double)(1L << 30):F1} GB"
        : $"{SizeBytes / (double)(1 << 20):F1} MB";
}

/// <summary>
/// The models the apps know how to download when they are missing.
///
/// Sources are upstream releases (Hugging Face), not mirrors, and every entry pins the exact
/// byte length and SHA-256 of the file the code was tested against. Adding an entry here is all
/// it takes for the CLI flag and both GUIs to offer it.
/// </summary>
public static class ModelCatalog
{
    public const string OemSparsemaskId = "oem";
    public const string SamTinyId = "sam-tiny";
    public const string SamBasePlusId = "sam-base-plus";

    public static IReadOnlyList<ModelEntry> All { get; } =
    [
        new ModelEntry(
            Id: OemSparsemaskId,
            FileName: "oem_sparsemask_8c.onnx",
            DisplayName: "OEM SparseMask 8 类（推荐，免训练）",
            Url: "https://huggingface.co/geobase/sparsemask/resolve/main/onnx/model.onnx",
            SizeBytes: 11_877_071,
            Sha256: "83c6ff78ff65818a38ed20ab562ccb9b20927bac6ef93021b3066a712678193d",
            License: "MIT",
            Note: "OpenEarthMap 8 类，随仓库的 sidecar 自动映射到本项目 7 类调色板。"),

        new ModelEntry(
            Id: SamTinyId,
            FileName: "sam/sam2.1_hiera_tiny.pt",
            DisplayName: "SAM 2.1 Hiera Tiny（CPU 默认）",
            Url: "https://huggingface.co/facebook/sam2.1-hiera-tiny/resolve/main/sam2.1_hiera_tiny.pt",
            SizeBytes: 156_008_466,
            Sha256: "7402e0d864fa82708a20fbd15bc84245c2f26dff0eb43a4b5b93452deb34be69",
            License: "Apache-2.0",
            Note: "交互式标注的后端；没有它时自动退回纯 C# 经典算法。"),

        new ModelEntry(
            Id: SamBasePlusId,
            FileName: "sam/sam2.1_hiera_base_plus.pt",
            DisplayName: "SAM 2.1 Hiera Base+（有 CUDA 时默认）",
            Url: "https://huggingface.co/facebook/sam2.1-hiera-base-plus/resolve/main/sam2.1_hiera_base_plus.pt",
            SizeBytes: 323_606_802,
            Sha256: "a2345aede8715ab1d5d31b4a509fb160c5a4af1970f199d9054ccfb746c004c5",
            License: "Apache-2.0",
            Note: "CPU 上比 Tiny 慢很多，只在有 GPU 时值得用。"),
    ];

    public static ModelEntry? ById(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(e => string.Equals(e.FileName, id, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FileName), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the catalog entry a model path corresponds to, if any.</summary>
    public static ModelEntry? ForModelPath(string modelPath)
    {
        string name = Path.GetFileName(modelPath);
        return All.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FileName), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The SAM weight to suggest first. The service itself picks base+ only when a GPU
    /// is visible, which C# cannot know before the Python process starts, so suggest the CPU one.</summary>
    public static ModelEntry DefaultSam => All.First(e => e.Id == SamTinyId);
}
