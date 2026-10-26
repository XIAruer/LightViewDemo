using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace LandCover.Core;

/// <summary>
/// Raster georeference: pixel &lt;-&gt; native CRS coordinates, plus native -&gt; WGS84.
///
/// Two readers feed it. <see cref="FromGdal"/> is the primary path — GDAL/PROJ resolves
/// the CRS and provides a reprojection for *any* CSP (UTM, Gauss-Kruger, web mercator,
/// geographic degrees ...). <see cref="TryRead"/> is a dependency-free fallback that
/// parses the GeoTIFF tags by hand and can only invert Transverse Mercator.
/// </summary>
public sealed class GeoTiffGeoref
{
    public double PixelSizeX { get; init; }
    public double PixelSizeY { get; init; }
    public double OriginX { get; init; }
    public double OriginY { get; init; }
    public int? Epsg { get; init; }
    public string? CrsName { get; init; }

    public bool IsTransverseMercator { get; init; }
    public double TmLat0Deg { get; init; }
    public double TmLon0Deg { get; init; }
    public double TmFalseEasting { get; init; }
    public double TmFalseNorthing { get; init; }
    public double TmScale { get; init; } = 1.0;
    public string? CrsNote { get; init; }

    // --- GDAL enrichment (all null/false on the hand-parsed path) ---

    /// <summary>Raw GDAL geotransform [originX, pxW, rotX, originY, rotY, pxH]; supports rotation.</summary>
    public double[]? Affine { get; init; }

    /// <summary>CRS as WKT, straight from GDAL. Enables full-PROJ reprojection.</summary>
    public string? SourceWkt { get; init; }

    /// <summary>True when the raster CRS is itself geographic (degree lon/lat).</summary>
    public bool IsGeographicCrs { get; init; }

    /// <summary>True when GDAL/PROJ built a working transform to WGS84.</summary>
    public bool HasGdalTransform { get; init; }

    private CoordinateTransformation? _toWgs84;
    private readonly object _ctGate = new();

    public bool HasTransform => PixelSizeX != 0 && PixelSizeY != 0;

    public bool CanInverseToWgs84 => HasGdalTransform || (IsTransverseMercator && TmScale > 0);

    /// <summary>True only for geographic CRS like EPSG:4326 (degree lon/lat in the file itself).</summary>
    public bool IsGeographicDegrees => IsGeographicCrs || Epsg is 4326 or 4258 or 4269;

    public static bool TryRead(string tifPath, out GeoTiffGeoref? georef)
    {
        georef = null;
        if (!File.Exists(tifPath))
            return false;

        try
        {
            using var fs = File.OpenRead(tifPath);
            using var br = new BinaryReader(fs);
            var header = br.ReadBytes(8);
            if (header.Length < 8)
                return false;

            bool little = header[0] == 'I' && header[1] == 'I';
            ushort magic = ReadU16(header, 2, little);
            if (magic != 42)
                return false;

            uint ifdOffset = ReadU32(header, 4, little);
            fs.Seek(ifdOffset, SeekOrigin.Begin);
            ushort entryCount = ReadU16(br.ReadBytes(2), 0, little);

            double[]? pixelScale = null;
            double[]? tiepoint = null;
            short[]? geoKeys = null;
            double[]? geoDoubles = null;
            string? geoAscii = null;

            for (int i = 0; i < entryCount; i++)
            {
                var entry = br.ReadBytes(12);
                if (entry.Length < 12)
                    break;

                ushort tag = ReadU16(entry, 0, little);
                ushort type = ReadU16(entry, 2, little);
                uint count = ReadU32(entry, 4, little);
                uint valueOrOffset = ReadU32(entry, 8, little);

                switch (tag)
                {
                    case 33550:
                        pixelScale = ReadDoubles(fs, little, type, count, valueOrOffset);
                        break;
                    case 33922:
                        tiepoint = ReadDoubles(fs, little, type, count, valueOrOffset);
                        break;
                    case 34735:
                        geoKeys = ReadShorts(fs, little, type, count, valueOrOffset);
                        break;
                    case 34736:
                        geoDoubles = ReadDoubles(fs, little, type, count, valueOrOffset);
                        break;
                    case 34737:
                        geoAscii = ReadAscii(fs, little, type, count, valueOrOffset);
                        break;
                }
            }

            if (pixelScale is null || pixelScale.Length < 2 || tiepoint is null || tiepoint.Length < 6)
                return false;

            double scaleX = pixelScale[0];
            double scaleY = pixelScale[1];
            double originX = tiepoint[3] - tiepoint[0] * scaleX;
            double originY = tiepoint[4] + tiepoint[1] * scaleY;

            int? epsg = null;
            int? projectedEpsg = null;
            int? geographicEpsg = null;
            bool userDefinedProjected = false;
            int gtModelType = 0;
            double? lat0 = null, lon0 = null, fe = null, fn = null, k0 = null;

            if (geoKeys is not null && geoKeys.Length >= 4)
            {
                int numKeys = geoKeys[3];
                for (int k = 0; k < numKeys; k++)
                {
                    int baseIdx = 4 + k * 4;
                    if (baseIdx + 3 >= geoKeys.Length)
                        break;

                    int keyId = geoKeys[baseIdx];
                    int tiffLoc = geoKeys[baseIdx + 1];
                    int valueOrOff = geoKeys[baseIdx + 3];

                    double ReadDoubleKey()
                    {
                        if (tiffLoc == 0)
                            return valueOrOff;
                        if (tiffLoc == 34736 && geoDoubles is not null && valueOrOff < geoDoubles.Length)
                            return geoDoubles[valueOrOff];
                        return 0;
                    }

                    switch (keyId)
                    {
                        case 1024 when tiffLoc == 0:
                            gtModelType = valueOrOff; // 1=projected model type in some files; 2=geographic
                            break;
                        case 3072: // ProjectedCSTypeGeoKey
                        {
                            int code = tiffLoc == 0
                                ? valueOrOff
                                : (geoDoubles is not null && valueOrOff < geoDoubles.Length
                                    ? (int)geoDoubles[valueOrOff]
                                    : 0);
                            if (code is > 0 and < 32767)
                                projectedEpsg = code;
                            else if (code == 32767)
                                userDefinedProjected = true;
                            break;
                        }
                        case 2048: // GeographicTypeGeoKey (datum only — NOT the projected output CRS)
                        {
                            int code = tiffLoc == 0
                                ? valueOrOff
                                : (geoDoubles is not null && valueOrOff < geoDoubles.Length
                                    ? (int)geoDoubles[valueOrOff]
                                    : 0);
                            if (code is > 0 and < 32767)
                                geographicEpsg = code;
                            break;
                        }
                        case 3080:
                            lat0 = ReadDoubleKey();
                            break;
                        case 3081:
                            lon0 = ReadDoubleKey();
                            break;
                        case 3082:
                            fe = ReadDoubleKey();
                            break;
                        case 3083:
                            fn = ReadDoubleKey();
                            break;
                        case 3092:
                            k0 = ReadDoubleKey();
                            break;
                    }
                }
            }

            string? crsNote = null;
            string? crsName = null;

            // Projected CRS wins. GeographicType=4326 only names the datum ellipsoid.
            epsg = projectedEpsg;
            if (epsg is null && userDefinedProjected)
                crsName = "Projected (user-defined TM)";
            else if (epsg is not null)
                crsName = $"EPSG:{epsg}";

            bool isTm = false;
            double useLat0 = lat0 ?? 0;
            double useLon0 = lon0 ?? 0;
            double useFe = fe ?? 0;
            double useFn = fn ?? 0;
            double useK0 = k0 ?? 1.0;

            string ascii = geoAscii?.Trim('\0', '|', ' ') ?? "";
            bool asciiHasTm = ascii.Contains("Transverse Mercator", StringComparison.OrdinalIgnoreCase)
                              || ascii.Contains("Gauss", StringComparison.OrdinalIgnoreCase);

            if (userDefinedProjected || asciiHasTm || (lat0 is not null && lon0 is not null))
            {
                isTm = true;

                // Some exporters swap lat0/lon0 (e.g. LandCover.ai tiles store lat0=19, lon0=0
                // while EPSG:2180 is lat0=0, lon0=19). Detect and correct.
                if (Math.Abs(useLon0) < 1e-9 && Math.Abs(useLat0) > 1e-9 && Math.Abs(useLat0) <= 40)
                {
                    (useLat0, useLon0) = (useLon0, useLat0);
                    crsNote = "GeoTIFF ProjNatOriginLat/Long appear swapped; corrected for inverse TM.";
                }

                if (Math.Abs(useK0) < 1e-9)
                    useK0 = 1.0;
            }

            if (ascii.Length > 0)
            {
                crsName = string.IsNullOrEmpty(crsName)
                    ? ascii
                    : $"{crsName} | {ascii}";
            }

            if (isTm)
            {
                crsName = $"{crsName} | TM lon0={useLon0.ToString(CultureInfo.InvariantCulture)} lat0={useLat0.ToString(CultureInfo.InvariantCulture)} FE={useFe.ToString(CultureInfo.InvariantCulture)} FN={useFn.ToString(CultureInfo.InvariantCulture)} k0={useK0.ToString(CultureInfo.InvariantCulture)}";
            }

            // Never claim EPSG:4326 for meter grids just because datum key is 4326.
            if (epsg == 4326 && isTm)
                epsg = projectedEpsg;

            georef = new GeoTiffGeoref
            {
                PixelSizeX = scaleX,
                PixelSizeY = scaleY,
                OriginX = originX,
                OriginY = originY,
                Epsg = epsg,
                CrsName = crsName,
                IsTransverseMercator = isTm,
                TmLat0Deg = useLat0,
                TmLon0Deg = useLon0,
                TmFalseEasting = useFe,
                TmFalseNorthing = useFn,
                TmScale = useK0,
                CrsNote = crsNote,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pixel (col,row) center -> native CRS (X,Y).</summary>
    public (double X, double Y) PixelToWorld(double col, double row)
    {
        if (Affine is { Length: 6 } g)
            return (g[0] + col * g[1] + row * g[2], g[3] + col * g[4] + row * g[5]);

        double x = OriginX + col * PixelSizeX;
        double y = OriginY - row * PixelSizeY;
        return (x, y);
    }

    /// <summary>Inverse of <see cref="PixelToWorld"/>; north-up rasters only.</summary>
    public (double Col, double Row) WorldToPixel(double x, double y)
    {
        if (Affine is { Length: 6 } g && g[1] != 0 && g[5] != 0)
        {
            double dy = y - g[3];
            double dx = x - g[0];
            return ((dx * g[5] - dy * g[2]) / (g[1] * g[5]), (dy * g[1] - dx * g[4]) / (g[1] * g[5]));
        }

        return ((x - OriginX) / PixelSizeX, (OriginY - y) / PixelSizeY);
    }

    /// <summary>
    /// Native CRS -> WGS84 lon/lat degrees. Uses GDAL/PROJ when the CRS was read by GDAL
    /// (any projection); otherwise falls back to the built-in inverse Transverse Mercator.
    /// </summary>
    public bool TryNativeToWgs84(double x, double y, out double lonDeg, out double latDeg)
    {
        lonDeg = 0;
        latDeg = 0;

        if (HasGdalTransform)
        {
            try
            {
                CoordinateTransformation? ct = GetOrCreateTransform();
                if (ct is not null)
                {
                    double[] p = [x, y, 0];
                    lock (_ctGate)
                        ct.TransformPoint(p);

                    if (double.IsFinite(p[0]) && double.IsFinite(p[1]))
                    {
                        lonDeg = p[0];
                        latDeg = p[1];
                        return true;
                    }
                }
            }
            catch
            {
                // fall through to the TM maths below
            }
        }

        if (!IsTransverseMercator || TmScale <= 0)
            return false;

        InverseTransverseMercatorWgs84(
            x, y,
            TmLat0Deg, TmLon0Deg, TmFalseEasting, TmFalseNorthing, TmScale,
            out lonDeg, out latDeg);
        return true;
    }

    private CoordinateTransformation? GetOrCreateTransform()
    {
        lock (_ctGate)
        {
            if (_toWgs84 is not null)
                return _toWgs84;
            if (string.IsNullOrWhiteSpace(SourceWkt))
                return null;

            var src = new SpatialReference(SourceWkt);
            var dst = new SpatialReference("");
            dst.ImportFromEPSG(4326);

            // Both ends need the traditional GIS order, or a geographic source comes back with
            // its coordinates swapped: EPSG:4326 is defined lat-first in authority order while a
            // raster's own x/y (and every caller here) is lon-first, so PROJ would read our
            // longitude as a latitude. Projected sources are unaffected — traditional order is
            // easting-first, which is what the raster already uses.
            src.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            dst.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            _toWgs84 = new CoordinateTransformation(src, dst);
            return _toWgs84;
        }
    }

    /// <summary>
    /// Reads the georeference through GDAL: any CRS PROJ understands, plus a working
    /// reprojection to WGS84. Returns null when the raster carries no georeferencing at all.
    /// </summary>
    public static GeoTiffGeoref? FromGdal(Dataset ds)
    {
        if (!GdalRuntime.EnsureInitialized())
            return null;

        // GDAL's GetGeoTransform is void and leaves the array at the identity default
        // [0,1,0,0,0,1] when the raster carries no geotransform.
        double[] gt = [0, 1, 0, 0, 0, 1];
        ds.GetGeoTransform(gt);
        bool hasGt = !(gt[0] == 0 && gt[1] == 1 && gt[2] == 0 && gt[3] == 0 && gt[4] == 0 && gt[5] == 1);

        string wkt = ds.GetProjectionRef() ?? string.Empty;
        bool hasCrs = !string.IsNullOrWhiteSpace(wkt);
        if (!hasGt && !hasCrs)
            return null;

        int? epsg = null;
        string? crsName = null;
        bool geographic = false;
        bool canTransform = false;

        if (hasCrs)
        {
            try
            {
                var srs = new SpatialReference(wkt);
                try
                {
                    srs.AutoIdentifyEPSG();
                    if (int.TryParse(srs.GetAuthorityCode(null), out int code) && code is > 0 and < 32768)
                        epsg = code;

                    string name = srs.GetName();
                    crsName = string.IsNullOrWhiteSpace(name) ? null : name;
                    geographic = srs.IsGeographic() == 1;

                    var dst = new SpatialReference("");
                    dst.ImportFromEPSG(4326);
                    using var probe = new CoordinateTransformation(srs, dst);
                    canTransform = true;
                }
                finally
                {
                    srs.Dispose();
                }
            }
            catch
            {
                canTransform = false;
            }
        }

        // GDAL's geotransform maps pixel corners; the rest of the codebase works in
        // metres-per-pixel with a top-left origin, which is what these four fields express.
        double pxW = hasGt ? gt[1] : 0;
        double pxH = hasGt ? -gt[5] : 0;

        return new GeoTiffGeoref
        {
            PixelSizeX = pxW,
            PixelSizeY = pxH,
            OriginX = hasGt ? gt[0] : 0,
            OriginY = hasGt ? gt[3] : 0,
            Epsg = epsg,
            CrsName = crsName,
            Affine = hasGt ? gt : null,
            SourceWkt = hasCrs ? wkt : null,
            IsGeographicCrs = geographic,
            HasGdalTransform = canTransform,
            CrsNote = canTransform ? "CRS 由 GDAL/PROJ 解析" : "CRS 未能建立到 WGS84 的转换",
        };
    }

    private const double Wgs84A = 6378137.0;
    private const double Wgs84F = 1.0 / 298.257223563;

    private static void InverseTransverseMercatorWgs84(
        double x, double y,
        double lat0Deg, double lon0Deg, double fe, double fn, double k0,
        out double lonDeg, out double latDeg)
    {
        double e2 = 2 * Wgs84F - Wgs84F * Wgs84F;
        double ep2 = e2 / (1 - e2);
        double e = Math.Sqrt(e2);
        double a = Wgs84A;

        x -= fe;
        y -= fn;

        double m = y / k0;
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        double mu = m / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));

        double phi1 =
            mu
            + (3 * e1 / 2 - 27 * e1 * e1 * e1 / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * e1 * e1 * e1 * e1 / 32) * Math.Sin(4 * mu)
            + (151 * e1 * e1 * e1 / 96) * Math.Sin(6 * mu)
            + (1097 * e1 * e1 * e1 * e1 / 512) * Math.Sin(8 * mu);

        double c1 = ep2 * Math.Cos(phi1) * Math.Cos(phi1);
        double t1 = Math.Tan(phi1) * Math.Tan(phi1);
        double n1 = a / Math.Sqrt(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1));
        double r1 = a * (1 - e2) / Math.Pow(1 - e2 * Math.Sin(phi1) * Math.Sin(phi1), 1.5);
        double d = x / (n1 * k0);

        double lat = phi1 - (n1 * Math.Tan(phi1) / r1) * (
            d * d / 2
            - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);

        double lon = lon0Deg * Math.PI / 180 + (
            d
            - (1 + 2 * t1 + c1) * d * d * d / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120
        ) / Math.Cos(phi1);

        latDeg = lat * 180 / Math.PI;
        lonDeg = lon * 180 / Math.PI;
    }

    private static ushort ReadU16(byte[] b, int offset, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset))
                  : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset));

    private static uint ReadU32(byte[] b, int offset, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset))
                  : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));

    private static double[]? ReadDoubles(FileStream fs, bool little, ushort type, uint count, uint valueOrOffset)
    {
        if (type != 12 || count == 0 || count > 1024)
            return null;

        int byteCount = (int)count * 8;
        byte[] data = ReadValueBytes(fs, byteCount, valueOrOffset);
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(i * 8, 8);
            result[i] = little
                ? BinaryPrimitives.ReadDoubleLittleEndian(span)
                : BinaryPrimitives.ReadDoubleBigEndian(span);
        }

        return result;
    }

    private static short[]? ReadShorts(FileStream fs, bool little, ushort type, uint count, uint valueOrOffset)
    {
        if (type != 3 || count == 0 || count > 4096)
            return null;

        int byteCount = (int)count * 2;
        byte[] data = ReadValueBytes(fs, byteCount, valueOrOffset);
        var result = new short[count];
        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(i * 2, 2);
            result[i] = little
                ? BinaryPrimitives.ReadInt16LittleEndian(span)
                : BinaryPrimitives.ReadInt16BigEndian(span);
        }

        return result;
    }

    private static string? ReadAscii(FileStream fs, bool little, ushort type, uint count, uint valueOrOffset)
    {
        if (type != 2 || count == 0 || count > 4096)
            return null;

        byte[] data = ReadValueBytes(fs, (int)count, valueOrOffset);
        return Encoding.ASCII.GetString(data);
    }

    private static byte[] ReadValueBytes(FileStream fs, int byteCount, uint valueOrOffset)
    {
        long restore = fs.Position;
        byte[] data;
        if (byteCount <= 4)
        {
            var inline = BitConverter.GetBytes(valueOrOffset);
            data = new byte[byteCount];
            Array.Copy(inline, data, byteCount);
        }
        else
        {
            fs.Seek(valueOrOffset, SeekOrigin.Begin);
            data = new byte[byteCount];
            int read = fs.Read(data, 0, byteCount);
            if (read < byteCount)
                Array.Resize(ref data, read);
        }

        fs.Seek(restore, SeekOrigin.Begin);
        return data;
    }
}
