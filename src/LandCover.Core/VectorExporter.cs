using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;

namespace LandCover.Core;

public sealed class VectorFeature
{
    public required int ClassId { get; init; }
    public required string ClassName { get; init; }
    public required double AreaPx { get; init; }
    public required double AreaM2 { get; init; }
    public required List<double[][]> Rings { get; init; } // each ring [ [x,y], ... ] closed
}

public static class VectorExporter
{
    /// <summary>The "ignore / not painted" pixel value shared with the annotation store.</summary>
    public const byte IgnoreValue = 255;

    /// <summary>
    /// Traces polygons out of a class-id plane.
    ///
    /// <paramref name="offsetX"/>/<paramref name="offsetY"/> say where this plane sits inside the
    /// raster the georeference describes. The annotation tool exports only the bounding box of the
    /// painted tiles rather than a whole 400 MP plane, and without the offset every exported
    /// coordinate would be shifted by the crop origin.
    ///
    /// <paramref name="minClass"/> is 1 by default because the inference pipeline treats class 0 as
    /// "background, not traced" — but in the annotation round-trip background is a real thing the
    /// user can paint, so that caller passes 0.
    /// </summary>
    public static List<VectorFeature> Extract(
        Mat classMap,
        GeoTiffGeoref? georef,
        double minAreaPx = 64,
        double simplifyTolerancePx = 1.5,
        int offsetX = 0,
        int offsetY = 0,
        int minClass = 1)
    {
        var features = new List<VectorFeature>();

        // 255 is the "ignore / not painted" sentinel in an annotation plane. It must never count as
        // a class: tracing it produces one enormous polygon covering everything the user has not
        // painted, which then round-trips back in as a solid fill over the whole image.
        int maxClass = 0;
        for (int y = 0; y < classMap.Rows; y++)
        {
            for (int x = 0; x < classMap.Cols; x++)
            {
                byte v = classMap.At<byte>(y, x);
                if (v != IgnoreValue && v > maxClass)
                    maxClass = v;
            }
        }

        double pxArea = georef is { HasTransform: true }
            ? Math.Abs(georef.PixelSizeX * georef.PixelSizeY)
            : 1.0;

        int classLimit = Math.Max(maxClass, (int)LandCoverPalette.ClassCount - 1);
        for (int cls = Math.Max(0, minClass); cls <= classLimit; cls++)
        {
            if (cls == IgnoreValue)
                continue;

            using var binary = new Mat();
            Cv2.InRange(classMap, new Scalar(cls), new Scalar(cls), binary);

            Cv2.FindContours(binary, out var contours, out _, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
            string name = cls < LandCoverPalette.ClassNames.Length
                ? LandCoverPalette.ClassNames[cls]
                : $"class_{cls}";

            foreach (var contour in contours)
            {
                double area = Math.Abs(Cv2.ContourArea(contour));
                if (area < minAreaPx)
                    continue;

                var approx = Cv2.ApproxPolyDP(contour, simplifyTolerancePx, true);
                if (approx.Length < 4)
                    continue;

                var ring = new double[approx.Length + 1][];
                for (int i = 0; i < approx.Length; i++)
                {
                    // OpenCvSharp Point has X/Y fields only — no .Point property
                    int px = approx[i].X;
                    int py = approx[i].Y;
                    if (georef is { HasTransform: true })
                    {
                        var (wx, wy) = georef.PixelToWorld(px + 0.5 + offsetX, py + 0.5 + offsetY);
                        ring[i] = new[] { wx, wy };
                    }
                    else
                    {
                        ring[i] = new[] { (double)px, (double)(classMap.Rows - py) };
                    }
                }

                ring[^1] = (double[])ring[0].Clone();

                features.Add(new VectorFeature
                {
                    ClassId = cls,
                    ClassName = name,
                    AreaPx = area,
                    AreaM2 = area * pxArea,
                    Rings = new List<double[][]> { ring },
                });
            }
        }

        return features;
    }

    public static void WriteGeoJson(string path, List<VectorFeature> features, GeoTiffGeoref? georef)
    {
        var root = new JsonObject
        {
            ["type"] = "FeatureCollection",
        };

        if (georef is { HasTransform: true })
        {
            // Native projected CRS (meters). Do NOT label as EPSG:4326.
            string crsLabel = georef.Epsg is int epsg
                ? $"EPSG:{epsg}"
                : georef.CrsName ?? "projected-unknown";
            root["crs"] = new JsonObject
            {
                ["type"] = "name",
                ["properties"] = new JsonObject { ["name"] = crsLabel },
            };
            root["properties"] = new JsonObject
            {
                ["crs"] = crsLabel,
                ["crs_note"] = georef.CrsNote ?? "",
                ["units"] = "meters (native projected)",
                ["pixel_size_x"] = georef.PixelSizeX,
                ["pixel_size_y"] = georef.PixelSizeY,
                ["origin_x"] = georef.OriginX,
                ["origin_y"] = georef.OriginY,
            };
        }
        else
        {
            root["properties"] = new JsonObject
            {
                ["crs"] = "pixel",
                ["note"] = "No GeoTIFF georeference found; coordinates are image pixels (Y up).",
            };
        }

        var featuresNode = new JsonArray();
        foreach (var f in features)
        {
            var rings = new JsonArray();
            foreach (var ring in f.Rings)
            {
                var coords = new JsonArray();
                foreach (var pt in ring)
                    coords.Add(new JsonArray(pt[0], pt[1]));
                rings.Add(coords);
            }

            var feature = new JsonObject
            {
                ["type"] = "Feature",
                ["properties"] = new JsonObject
                {
                    ["class_id"] = f.ClassId,
                    ["class_name"] = f.ClassName,
                    ["class_key"] = f.ClassId < LandCoverPalette.ClassKeys.Length
                        ? LandCoverPalette.ClassKeys[f.ClassId]
                        : $"class_{f.ClassId}",
                    ["area_px"] = Math.Round(f.AreaPx, 2),
                    ["area_m2"] = Math.Round(f.AreaM2, 2),
                },
                ["geometry"] = new JsonObject
                {
                    ["type"] = "Polygon",
                    ["coordinates"] = rings,
                },
            };
            featuresNode.Add(feature);
        }

        root["features"] = featuresNode;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts), new UTF8Encoding(false));
    }

    /// <summary>
    /// KML requires WGS84 lon/lat. Inverse-project meters first; never write projected XY as lon/lat.
    /// </summary>
    public static void WriteKml(string path, List<VectorFeature> features, GeoTiffGeoref? georef)
    {
        bool alreadyLonLat = georef is { IsGeographicDegrees: true };
        bool canInverse = georef is { CanInverseToWgs84: true };

        if (!alreadyLonLat && !canInverse)
        {
            string why = georef is null
                ? "missing georeference"
                : "projected CRS without usable TM parameters for inverse";
            var empty = new StringBuilder();
            empty.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
            empty.AppendLine(@"<kml xmlns=""http://www.opengis.net/kml/2.2"">");
            empty.AppendLine("  <Document>");
            empty.AppendLine("    <name>LandCover (NOT EXPORTED)</name>");
            empty.AppendLine($"    <description>KML not written: source is not WGS84 lon/lat ({why}). Use the .geojson in QGIS (native projected coordinates).</description>");
            empty.AppendLine("  </Document>");
            empty.AppendLine("</kml>");
            File.WriteAllText(path, empty.ToString(), new UTF8Encoding(false));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        sb.AppendLine(@"<kml xmlns=""http://www.opengis.net/kml/2.2"">");
        sb.AppendLine("  <Document>");
        sb.AppendLine("    <name>LandCover</name>");
        sb.AppendLine("    <description>WGS84 lon/lat (EPSG:4326). "
            + (alreadyLonLat
                ? "Source was already geographic."
                : $"Inverse-projected from {georef!.CrsName ?? "projected CRS"}.")
            + (georef?.CrsNote is { Length: > 0 } note ? " " + EscapeXml(note) : "")
            + "</description>");

        var used = features.Select(f => f.ClassId).Distinct().OrderBy(i => i);
        foreach (int cls in used)
        {
            var (b, g, r) = cls < LandCoverPalette.ColorsBgr.Length
                ? LandCoverPalette.ColorsBgr[cls]
                : ((byte)128, (byte)128, (byte)128);
            string color = $"cc{r:X2}{g:X2}{b:X2}";
            sb.AppendLine($"    <Style id=\"c{cls}\"><LineStyle><width>1</width></LineStyle><PolyStyle><color>{color}</color></PolyStyle></Style>");
        }

        int skipped = 0;
        foreach (var f in features)
        {
            var lonLat = new List<(double Lon, double Lat)>(f.Rings[0].Length);
            foreach (var p in f.Rings[0])
            {
                if (alreadyLonLat)
                    lonLat.Add((p[0], p[1]));
                else if (georef!.TryNativeToWgs84(p[0], p[1], out double lon, out double lat))
                    lonLat.Add((lon, lat));
                else
                {
                    skipped++;
                    continue;
                }
            }

            if (lonLat.Count < 4)
            {
                skipped++;
                continue;
            }

            sb.AppendLine("    <Placemark>");
            sb.AppendLine($"      <name>{EscapeXml(f.ClassName)}</name>");
            sb.AppendLine($"      <styleUrl>#c{f.ClassId}</styleUrl>");
            sb.AppendLine("      <ExtendedData>");
            sb.AppendLine($"        <Data name=\"class_id\"><value>{f.ClassId}</value></Data>");
            sb.AppendLine($"        <Data name=\"area_m2\"><value>{f.AreaM2.ToString("F2", CultureInfo.InvariantCulture)}</value></Data>");
            sb.AppendLine("      </ExtendedData>");
            sb.AppendLine("      <Polygon>");
            sb.AppendLine("        <outerBoundaryIs><LinearRing><coordinates>");
            // KML order: lon,lat,alt
            sb.AppendLine("          " + string.Join(" ", lonLat.Select(p =>
                $"{p.Lon.ToString("F8", CultureInfo.InvariantCulture)},{p.Lat.ToString("F8", CultureInfo.InvariantCulture)},0")));
            sb.AppendLine("        </coordinates></LinearRing></outerBoundaryIs>");
            sb.AppendLine("      </Polygon>");
            sb.AppendLine("    </Placemark>");
        }

        sb.AppendLine("  </Document>");
        sb.AppendLine("</kml>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        if (skipped > 0)
            Console.WriteLine($"[KML] 跳过 {skipped} 个无法反算的顶点/面");
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
