using OSGeo.GDAL;
using OSGeo.OSR;
using Ogr = OSGeo.OGR.Ogr;

namespace LandCover.Core;

/// <summary>
/// One-time GDAL/OGR bootstrap.
/// Native binaries ship inside the MaxRev.Gdal.WindowsRuntime.* NuGet package,
/// so no OSGeo4W / PATH / GDAL_DATA setup is required for the user.
/// </summary>
public static class GdalRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _failure;

    /// <summary>True once GDAL loaded. False means fall back to the OpenCV/pure-C# paths.</summary>
    public static bool IsAvailable => EnsureInitialized();

    /// <summary>Populated when initialisation failed; null on success.</summary>
    public static string? FailureReason => _failure;

    public static bool EnsureInitialized()
    {
        if (_initialized)
            return true;

        lock (Gate)
        {
            if (_initialized)
                return true;

            try
            {
                MaxRev.Gdal.Core.GdalBase.ConfigureAll();

                // Decimated reads over big rasters (previews, annotation viewport) use these.
                Gdal.SetConfigOption("GDAL_NUM_THREADS", "ALL_CPUS");
                Gdal.SetConfigOption("GDAL_CACHEMAX", "512");
                Gdal.SetConfigOption("GDAL_TIFF_OVR_BLOCKSIZE", "256");

                // Reading statistics otherwise drops a .aux.xml sidecar next to the user's
                // imagery, which we have no business writing into their data directories.
                Gdal.SetConfigOption("GDAL_PAM_ENABLED", "NO");

                Gdal.AllRegister();
                Ogr.RegisterAll();

                _initialized = true;
            }
            catch (Exception ex)
            {
                _failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            return _initialized;
        }
    }

    /// <summary>e.g. <c>GDAL 3.13.3 "Iowa City", released 2026/08/28</c>.</summary>
    public static string Version => EnsureInitialized() ? Gdal.VersionInfo("--version") : "unavailable";

    /// <summary>True when a driver (raster or vector) with this short name is registered.</summary>
    public static bool HasDriver(string name)
        => EnsureInitialized() && (Gdal.GetDriverByName(name) is not null || Ogr.GetDriverByName(name) is not null);

    /// <summary>Formats readable to GDAL for a given file extension, for error messages.</summary>
    public static string DescribeSupport()
        => EnsureInitialized()
            ? $"GDAL {Gdal.VersionInfo("RELEASE_NAME")} (GTiff/COG, ESRI Shapefile, GPKG, GeoJSON, KML...)"
            : $"GDAL 不可用 ({_failure ?? "未初始化"})";
}
