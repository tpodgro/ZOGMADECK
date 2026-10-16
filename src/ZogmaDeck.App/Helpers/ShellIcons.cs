using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZogmaDeck.Helpers;

/// <summary>
/// Icône d'un programme, raccourci ou fichier, telle qu'affichée par l'Explorateur (jusqu'à 256 px, avec transparence).
/// Permet d'utiliser l'icône officielle d'une application installée sans l'embarquer dans ZogmaDeck.
/// </summary>
public static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx, cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint f0, f1, f2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    /// <summary>Retourne l'icône (BGRA prémultiplié) ou null.</summary>
    public static BitmapSource? Get(string path, int size = 256)
    {
        var full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(full) && !Directory.Exists(full)) return null;
        IShellItemImageFactory? factory = null;
        var hbmp = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(full, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
                return null;

            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var pixels = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);
            if (ds.dsBmih.biHeight > 0) // DIB « bottom-up » : on retourne les lignes
            {
                var flipped = new byte[pixels.Length];
                for (var y = 0; y < h; y++) Buffer.BlockCopy(pixels, y * stride, flipped, (h - 1 - y) * stride, stride);
                pixels = flipped;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Enregistre l'icône d'un programme en PNG dans le dossier assets et retourne le chemin relatif, ou null.</summary>
    public static string? SaveToAssets(string path, string assetsDirectory)
    {
        var icon = Get(path);
        if (icon is null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(icon));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        Directory.CreateDirectory(assetsDirectory);
        var name = "app-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + ".png";
        var dest = Path.Combine(assetsDirectory, name);
        if (!File.Exists(dest)) File.WriteAllBytes(dest, bytes);
        return "assets/" + name;
    }
}
