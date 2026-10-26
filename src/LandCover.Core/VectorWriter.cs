using OSGeo.OSR;
using OSGeo.OGR;
using OgrFeature = OSGeo.OGR.Feature;
using OgrGeometry = OSGeo.OGR.Geometry;

namespace LandCover.Core;

/// <summary>
/// Writes classification polygons through GDAL/OGR, which handles the format-specific
/// details that are painful by hand: the Shapefile .shx/.dbf/.prj sidecars, the .gpkg
/// container, per-format field-name limits, WGS84 reprojection for KML, and so on.
/// </summary>
public static class VectorWriter
{
    /// <summary>Extensions this writer understands, mapped to their OGR driver.</summary>
    private static readonly Dictionary<string, string> DriverByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".shp"] = "ESRI Shapefile",
        [".gpkg"] = "GPKG",
        [".geojson"] = "GeoJSON",
        [".json"] = "GeoJSON",
        [".kml"] = "KML",
        [".fgb"] = "FlatGeobuf",
    };

    public static bool CanWrite(string path) =>
        DriverByExtension.ContainsKey(System.IO.Path.GetExtension(path));

    public static IEnumerable<string> SupportedExtensions => DriverByExtension.Keys.Order();

    /// <summary>
    /// Writes <paramref name="features"/> to a vector file, choosing the driver from the
    /// file extension. Geometry stays in the raster's native CRS (KML is reprojected to WGS84).
    /// </summary>
    public static void Write(string path, List<VectorFeature> features, GeoTiffGeoref? georef)
    {
        if (!GdalRuntime.EnsureInitialized())
            throw new InvalidOperationException($"GDAL 初始化失败: {GdalRuntime.FailureReason}");
        if (!CanWrite(path))
            throw new NotSupportedException(
                $"不支持的矢量格式 '{System.IO.Path.GetExtension(path)}'，可用: {string.Join(", ", SupportedExtensions)}");

        string ext = System.IO.Path.GetExtension(path);
        string driverName = DriverByExtension[ext];
        bool needsWgs84 = driverName == "KML";

        Driver driver = Ogr.GetDriverByName(driverName)
            ?? throw new InvalidOperationException($"GDAL 缺少驱动: {driverName}");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        // Re-creating an existing Shapefile leaves stale .shx/.dbf behind.
        SafeDelete(driver, path);
        using var ds = driver.CreateDataSource(path, null)
            ?? throw new IOException($"无法创建矢量文件: {path}");

        SpatialReference? srs = ResolveSrs(georef, needsWgs84);

        // Shapefile .dbf defaults to ISO-8859-1, which mangles the Chinese class names.
        string[] layerOptions = driverName == "ESRI Shapefile" ? ["ENCODING=UTF-8"] : [];
        using var layer = ds.CreateLayer(
            System.IO.Path.GetFileNameWithoutExtension(path),
            srs,
            wkbGeometryType.wkbPolygon,
            layerOptions);

        if (layer is null)
            throw new IOException($"无法创建图层: {path}");

        // Shapefile truncates field names at 10 chars — keep ours under that.
        layer.CreateField(new FieldDefn("class_id", FieldType.OFTInteger), 0);
        layer.CreateField(new FieldDefn("class_name", FieldType.OFTString), 0);
        layer.CreateField(new FieldDefn("area_px", FieldType.OFTReal), 0);
        layer.CreateField(new FieldDefn("area_m2", FieldType.OFTReal), 0);
        layer.CreateField(new FieldDefn("class_key", FieldType.OFTString), 0);

        FeatureDefn defn = layer.GetLayerDefn();
        CoordinateTransformation? ct = needsWgs84 ? BuildWgs84Transform(georef) : null;

        bool useTransaction = driverName is "GPKG" or "FlatGeobuf";
        if (useTransaction)
            layer.StartTransaction();

        foreach (VectorFeature f in features)
        {
            using var feat = new OgrFeature(defn);
            feat.SetField("class_id", f.ClassId);
            feat.SetField("class_name", f.ClassName);
            feat.SetField("area_px", f.AreaPx);
            feat.SetField("area_m2", f.AreaM2);
            feat.SetField("class_key", f.ClassId < LandCoverPalette.ClassKeys.Length
                ? LandCoverPalette.ClassKeys[f.ClassId]
                : $"class_{f.ClassId}");

            using OgrGeometry? geom = BuildPolygon(f.Rings, ct);
            if (geom is null)
                continue;

            feat.SetGeometryDirectly(geom);
            int err = layer.CreateFeature(feat);
            if (err != 0)
                Console.Error.WriteLine($"[矢量] 写入要素失败 (err={err})");
        }

        if (useTransaction)
            layer.CommitTransaction();

        srs?.Dispose();
        ct?.Dispose();
    }

    /// <summary>
    /// KML is defined as WGS84 lon/lat, so its features are reprojected on the way out.
    /// Returns null when the native CRS already is geographic.
    /// </summary>
    private static CoordinateTransformation? BuildWgs84Transform(GeoTiffGeoref? georef)
    {
        if (georef is null || georef.IsGeographicDegrees)
            return null;
        if (string.IsNullOrWhiteSpace(georef.SourceWkt))
            return null;

        try
        {
            var src = new SpatialReference(georef.SourceWkt);
            var dst = new SpatialReference("");
            dst.ImportFromEPSG(4326);
            dst.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return new CoordinateTransformation(src, dst);
        }
        catch
        {
            return null;
        }
    }

    private static SpatialReference? ResolveSrs(GeoTiffGeoref? georef, bool wgs84)
    {
        if (wgs84 || georef is null)
        {
            var s = new SpatialReference("");
            s.ImportFromEPSG(4326);
            s.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return s;
        }

        SpatialReference? srs = null;
        if (!string.IsNullOrWhiteSpace(georef.SourceWkt))
        {
            try
            {
                srs = new SpatialReference(georef.SourceWkt);
            }
            catch
            {
                srs = null;
            }
        }

        if (srs is null && georef.Epsg is int epsg)
        {
            try
            {
                srs = new SpatialReference("");
                srs.ImportFromEPSG(epsg);
            }
            catch
            {
                srs?.Dispose();
                srs = null;
            }
        }

        srs?.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        return srs;
    }

    private static OgrGeometry? BuildPolygon(List<double[][]> rings, CoordinateTransformation? ct)
    {
        if (rings.Count == 0)
            return null;

        var poly = new OgrGeometry(wkbGeometryType.wkbPolygon);
        bool any = false;

        foreach (double[][] ring in rings)
        {
            if (ring.Length < 3)
                continue;

            var linearRing = new OgrGeometry(wkbGeometryType.wkbLinearRing);
            foreach (double[] pt in ring)
            {
                // OGR does not auto-close rings; Polygon() is tolerant but Shapefile is not.
                linearRing.AddPoint_2D(pt[0], pt[1]);
            }

            double[] first = ring[0];
            double[] last = ring[^1];
            if (Math.Abs(first[0] - last[0]) > 1e-12 || Math.Abs(first[1] - last[1]) > 1e-12)
                linearRing.AddPoint_2D(first[0], first[1]);

            poly.AddGeometry(linearRing);
            any = true;
        }

        if (!any)
        {
            poly.Dispose();
            return null;
        }

        if (ct is not null)
        {
            // Only outer/hole rings; reproject every vertex in place.
            for (int i = 0; i < poly.GetGeometryCount(); i++)
            {
                // GetGeometryRef returns the polygon's own ring; do not dispose it.
                OgrGeometry ring = poly.GetGeometryRef(i);
                for (int p = 0; p < ring.GetPointCount(); p++)
                {
                    double[] xyz = new double[3];
                    ring.GetPoint(p, xyz);
                    ct.TransformPoint(xyz);
                    ring.SetPoint(p, xyz[0], xyz[1], 0);
                }
            }
        }

        return poly;
    }

    private static void SafeDelete(Driver driver, string path)
    {
        try
        {
            if (driver.GetName() == "ESRI Shapefile")
            {
                string stem = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
                    System.IO.Path.GetFileNameWithoutExtension(path));
                foreach (string ext in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg", ".qix" })
                {
                    string f = stem + ext;
                    if (File.Exists(f))
                        File.Delete(f);
                }

                return;
            }

            if (File.Exists(path) && driver.TestCapability("DeleteDataSource"))
                driver.DeleteDataSource(path);
        }
        catch
        {
            // best-effort; CreateDataSource will report a real failure below
        }
    }
}
