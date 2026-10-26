namespace LandCover.Core;

/// <summary>
/// Keep in sync with train/classes.json (7-class mountain land cover).
/// Old Deepness 4c model used only building/woodland/water/road — farmland was missing.
/// </summary>
public static class LandCoverPalette
{
    public static readonly string[] ClassNames =
    [
        "背景 Background",
        "建筑 Building",
        "道路 Road",
        "农田 Farmland",
        "林地 Woodland",
        "水体 Water",
        "裸地 Bareland",
    ];

    public static readonly string[] ClassKeys =
    [
        "background",
        "building",
        "road",
        "farmland",
        "woodland",
        "water",
        "bareland",
    ];

    // BGR for OpenCvSharp
    public static readonly (byte B, byte G, byte R)[] ColorsBgr =
    [
        (0, 0, 0),         // 0 background
        (0, 0, 222),       // 1 building
        (255, 255, 255),   // 2 road
        (73, 181, 75),   // 3 farmland (light green, OpenEarthMap-like)
        (38, 97, 34),      // 4 woodland
        (255, 69, 0),      // 5 water
        (128, 128, 0),     // 6 bareland (teal-ish / cyan-gray)
    ];

    public static byte ClassCount => (byte)ClassNames.Length;
}
