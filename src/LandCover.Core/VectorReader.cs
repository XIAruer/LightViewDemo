using OSGeo.OSR;
using OSGeo.OGR;
using OgrFeature = OSGeo.OGR.Feature;
using OgrGeometry = OSGeo.OGR.Geometry;

namespace LandCover.Core;

/// <summary>
/// A polygon read back from a vector file. <see cref="Rings"/> are in raster pixel coordinates —
/// ring 0 is the outer boundary, any others are holes — and each ring is closed.
/// </summary>
public sealed record VectorPolygon(int ClassId, List<double[][]> Rings);

public sealed record VectorReadResult(
    List<VectorPolygon> Polygons,
    int SkippedFeatures,
    List<string> Warnings);

/// <summary>
/// Reads polygons back through GDAL/OGR — the counterpart to <see cref="VectorWriter"/>, for the
/// round trip "annotate → export → tidy up in QGIS → import → train".
///
/// Anything OGR can open works (.shp/.gpkg/.geojson/.fgb), and the features are reprojected from
/// whatever CRS the file carries into the raster's CRS, then into pixel coordinates: someone who
/// edits the shapefile in QGIS and saves it in their working projection should still get labels
/// landing in the right place rather than a silent offset.
/// </summary>
public static class VectorReader
{
    /// <summary>Attributes the class id is read from, in order of preference.</summary>
    private static readonly string[] ClassIdFields = ["class_id", "classid", "class", "cls", "id"];

    private static readonly string[] ClassKeyFields = ["class_key", "key"];

    public static VectorReadResult ReadPolygons(string path, GeoTiffGeoref georef, int defaultClassId = 1)
    {
        ArgumentNullException.ThrowIfNull(georef);

        if (!GdalRuntime.EnsureInitialized())
            throw new InvalidOperationException($"GDAL 初始化失败: {GdalRuntime.FailureReason}");
        if (!File.Exists(path))
            throw new FileNotFoundException($"矢量文件不存在: {path}", path);

        var polygons = new List<VectorPolygon>();
        var warnings = new List<string>();
        int skipped = 0;

        using DataSource ds = Ogr.Open(path, 0)
            ?? throw new IOException($"无法打开矢量文件: {path}");
        using Layer layer = ds.GetLayerByIndex(0)
            ?? throw new IOException($"矢量文件里没有图层: {path}");

        CoordinateTransformation? ct = BuildTransform(layer, georef, warnings);
        try
        {
            FeatureDefn defn = layer.GetLayerDefn();
            int classField = FindField(defn, ClassIdFields);
            int keyField = FindField(defn, ClassKeyFields);
            if (classField < 0 && keyField < 0)
            {
                warnings.Add($"矢量里没有 class_id/class_key 字段，全部按 "
                    + $"{Describe(defaultClassId)} 处理。");
            }

            layer.ResetReading();
            for (OgrFeature? feat = layer.GetNextFeature(); feat is not null; feat = layer.GetNextFeature())
            {
                using (feat)
                {
                    OgrGeometry? geom = feat.GetGeometryRef();
                    if (geom is null)
                    {
                        skipped++;
                        continue;
                    }

                    int classId = ResolveClassId(feat, classField, keyField, defaultClassId);
                    bool any = false;

                    foreach (List<double[][]> rings in ExtractParts(geom, ct, georef))
                    {
                        if (rings.Count == 0 || rings[0].Length < 4)
                        {
                            skipped++;
                            continue;
                        }

                        polygons.Add(new VectorPolygon(classId, rings));
                        any = true;
                    }

                    if (!any)
                        skipped++;
                }
            }
        }
        finally
        {
            ct?.Dispose();
        }

        return new VectorReadResult(polygons, skipped, warnings);
    }

    /// <summary>
    /// Layer CRS -> raster CRS. Null means "use the coordinates as they are", which is the right
    /// answer when neither side declares a CRS — better than refusing to import at all.
    /// </summary>
    private static CoordinateTransformation? BuildTransform(Layer layer, GeoTiffGeoref georef, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(georef.SourceWkt))
        {
            warnings.Add("影像没有坐标系信息，矢量坐标按原样当作像素坐标使用。");
            return null;
        }

        SpatialReference? src = null;
        try
        {
            src = layer.GetSpatialRef();
            if (src is null)
            {
                warnings.Add("矢量文件没有坐标系信息（缺 .prj？），按与影像同一坐标系处理。");
                return null;
            }

            // Both ends in traditional GIS order, for the same reason as GeoTiffGeoref: authority
            // order puts latitude first for geographic CRSs while every coordinate here is x-first.
            var dst = new SpatialReference(georef.SourceWkt);
            src.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            dst.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            var ct = new CoordinateTransformation(src, dst);
            dst.Dispose();
            return ct;
        }
        catch (Exception ex)
        {
            warnings.Add($"坐标系转换不可用（{ex.Message}），按与影像同一坐标系处理。");
            return null;
        }
        finally
        {
            src?.Dispose();
        }
    }

    private static int FindField(FeatureDefn defn, string[] candidates)
    {
        foreach (string name in candidates)
        {
            int idx = defn.GetFieldIndex(name);
            if (idx >= 0)
                return idx;

            // Shapefile lower-cases nothing, but QGIS users write CamelCase freely.
            for (int i = 0; i < defn.GetFieldCount(); i++)
            {
                if (string.Equals(defn.GetFieldDefn(i).GetName(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    private static int ResolveClassId(OgrFeature feat, int classField, int keyField, int fallback)
    {
        if (classField >= 0)
        {
            int value = feat.GetFieldAsInteger(classField);

            // 255 is the ignore sentinel on the annotation side too, so a polygon drawn as 255 in
            // GIS round-trips as a deliberate "carve this out of the training set".
            if (value == IgnoreClassId)
                return IgnoreClassId;
            if (value >= 0 && value < (int)LandCoverPalette.ClassCount)
                return value;
        }

        if (keyField >= 0)
        {
            string key = (feat.GetFieldAsString(keyField) ?? "").Trim();
            for (int i = 0; i < LandCoverPalette.ClassKeys.Length; i++)
                if (string.Equals(LandCoverPalette.ClassKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    return i;
        }

        return fallback;
    }

    /// <summary>The eraser sentinel, shared with the annotation side (255 = not part of training).</summary>
    public const int IgnoreClassId = 255;

    private static string Describe(int classId) =>
        classId >= 0 && classId < LandCoverPalette.ClassNames.Length
            ? $"{classId} {LandCoverPalette.ClassNames[classId]}"
            : $"类别 {classId}";

    /// <summary>
    /// Flattens a polygon/multipolygon/collection into one ring-list per polygon part, in pixel
    /// coordinates. Handles holes: ring 0 is the outer boundary, later rings are subtracted.
    /// </summary>
    private static IEnumerable<List<double[][]>> ExtractParts(
        OgrGeometry geom, CoordinateTransformation? ct, GeoTiffGeoref georef)
    {
        wkbGeometryType type = Flatten(geom.GetGeometryType());

        if (type == wkbGeometryType.wkbPolygon)
        {
            var rings = new List<double[][]>();
            for (int i = 0; i < geom.GetGeometryCount(); i++)
            {
                double[][]? ring = ToPixelRing(geom.GetGeometryRef(i), ct, georef);
                if (ring is not null)
                    rings.Add(ring);
            }

            yield return rings;
            yield break;
        }

        if (type is wkbGeometryType.wkbMultiPolygon or wkbGeometryType.wkbGeometryCollection)
        {
            for (int i = 0; i < geom.GetGeometryCount(); i++)
                foreach (List<double[][]> part in ExtractParts(geom.GetGeometryRef(i), ct, georef))
                    yield return part;
        }
    }

    private static wkbGeometryType Flatten(wkbGeometryType type)
    {
        // wkbPolygon25D / wkbMultiPolygon25D etc: strip the Z/M flags.
        int raw = (int)type;
        if (raw >= 1000)
            raw -= 1000;
        else if (raw is >= 100 and < 200)
            raw -= 100;
        return (wkbGeometryType)raw;
    }

    private static double[][]? ToPixelRing(
        OgrGeometry ring, CoordinateTransformation? ct, GeoTiffGeoref georef)
    {
        int n = ring.GetPointCount();
        if (n < 3)
            return null;

        var points = new List<double[]>(n + 1);
        var xyz = new double[3];
        for (int i = 0; i < n; i++)
        {
            ring.GetPoint(i, xyz);
            double wx = xyz[0], wy = xyz[1];

            if (ct is not null)
            {
                var p = new[] { wx, wy, 0.0 };
                ct.TransformPoint(p);
                if (!double.IsFinite(p[0]) || !double.IsFinite(p[1]))
                    continue;
                wx = p[0];
                wy = p[1];
            }

            var (col, row) = georef.WorldToPixel(wx, wy);
            if (!double.IsFinite(col) || !double.IsFinite(row))
                continue;

            points.Add([col, row]);
        }

        if (points.Count < 3)
            return null;

        // Close the ring; OpenCV's fill is tolerant but the next reader may not be.
        double[] first = points[0];
        double[] last = points[^1];
        if (Math.Abs(first[0] - last[0]) > 1e-9 || Math.Abs(first[1] - last[1]) > 1e-9)
            points.Add([first[0], first[1]]);

        return [.. points];
    }
}
