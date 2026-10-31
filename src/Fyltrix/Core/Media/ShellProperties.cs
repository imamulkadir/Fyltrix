using System.Runtime.InteropServices;

namespace Fyltrix.Core.Media;

public sealed record PropertyItem(string Label, string Value);

/// <summary>Metadata as Explorer shows it: dimensions, codec, duration, EXIF, tags.</summary>
public sealed class MediaInfo
{
    public List<PropertyItem> Items { get; } = new();
    public TimeSpan? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
}

/// <summary>
/// Reads the Windows property system (the same handlers that fill Explorer's Details pane), formatting values
/// with PSFormatForDisplay so they read exactly like Explorer ("1/125 sec.", "f/2.8", "00:03:25"). Read-only.
/// </summary>
public static class ShellProperties
{
    public enum Group { Image, Video, Audio }

    private static readonly (string Name, string Label)[] ImageProps =
    {
        ("System.Image.Dimensions", "Dimensions"), ("System.Image.BitDepth", "Bit depth"), ("System.Image.HorizontalResolution", "Resolution"),
        ("System.Photo.DateTaken", "Date taken"), ("System.Photo.CameraManufacturer", "Camera maker"), ("System.Photo.CameraModel", "Camera model"),
        ("System.Photo.LensModel", "Lens"), ("System.Photo.ExposureTime", "Exposure"), ("System.Photo.FNumber", "Aperture"),
        ("System.Photo.ISOSpeed", "ISO"), ("System.Photo.FocalLength", "Focal length"), ("System.Photo.Flash", "Flash"),
        ("System.Photo.Orientation", "Orientation"), ("System.GPS.Latitude", "Latitude"), ("System.GPS.Longitude", "Longitude"),
        ("System.ApplicationName", "Program"), ("System.Title", "Title"), ("System.Author", "Authors"), ("System.Copyright", "Copyright"),
    };

    private static readonly (string Name, string Label)[] VideoProps =
    {
        ("System.Media.Duration", "Length"), ("System.Video.FrameWidth", "Frame width"), ("System.Video.FrameHeight", "Frame height"),
        ("System.Video.FrameRate", "Frame rate"), ("System.Video.TotalBitrate", "Total bitrate"), ("System.Video.EncodingBitrate", "Video bitrate"),
        ("System.Audio.EncodingBitrate", "Audio bitrate"), ("System.Audio.ChannelCount", "Audio channels"), ("System.Audio.SampleRate", "Audio sample rate"),
        ("System.Title", "Title"), ("System.Media.Year", "Year"),
    };

    private static readonly (string Name, string Label)[] AudioProps =
    {
        ("System.Title", "Title"), ("System.Music.Artist", "Artist"), ("System.Music.AlbumTitle", "Album"), ("System.Music.AlbumArtist", "Album artist"),
        ("System.Media.Year", "Year"), ("System.Music.Genre", "Genre"), ("System.Music.TrackNumber", "Track"), ("System.Media.Duration", "Length"),
        ("System.Audio.EncodingBitrate", "Bitrate"), ("System.Audio.SampleRate", "Sample rate"), ("System.Audio.ChannelCount", "Channels"),
        ("System.Audio.SampleSize", "Sample size"), ("System.Audio.IsVariableBitRate", "Variable bitrate"),
    };

    public static MediaInfo Read(string path, Group group)
    {
        var info = new MediaInfo();
        var props = group switch { Group.Image => ImageProps, Group.Video => VideoProps, _ => AudioProps };
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_BESTEFFORT, ref iid, out store) != 0 || store == null) return info;
            foreach (var (name, label) in props)
            {
                if (PSGetPropertyKeyFromName(name, out var key) != 0) continue;
                if (store.GetValue(ref key, out var pv) != 0) continue;
                try
                {
                    if (pv.vt == VT_EMPTY) continue;
                    switch (name)
                    {
                        case "System.Media.Duration" when pv.vt == VT_UI8:
                            info.Duration = TimeSpan.FromTicks((long)pv.uhVal);
                            break;
                        case "System.Video.FrameWidth" when pv.vt == VT_UI4:
                            info.Width = (int)pv.ulVal;
                            break;
                        case "System.Video.FrameHeight" when pv.vt == VT_UI4:
                            info.Height = (int)pv.ulVal;
                            break;
                    }
                    if (PSFormatForDisplayAlloc(ref key, ref pv, PDFF_DEFAULT, out var text) == 0 && text != IntPtr.Zero)
                    {
                        var s = Marshal.PtrToStringUni(text);
                        Marshal.FreeCoTaskMem(text);
                        if (!string.IsNullOrWhiteSpace(s)) info.Items.Add(new PropertyItem(label, s.Trim().TrimStart('‎', '‏')));
                    }
                }
                finally { PropVariantClear(ref pv); }
            }
            if (group == Group.Video && PSGetPropertyKeyFromName("System.Video.FourCC", out var fk) == 0 && store.GetValue(ref fk, out var fv) == 0)
            {
                try
                {
                    if (fv.vt == VT_UI4 && fv.ulVal != 0)
                    {
                        info.VideoCodec = FourCC(fv.ulVal);
                        info.Items.Insert(Math.Min(1, info.Items.Count), new PropertyItem("Video codec", CodecName(info.VideoCodec)));
                    }
                }
                finally { PropVariantClear(ref fv); }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
        }
        return info;
    }

    public static string FourCC(uint v) =>
        new(new[] { (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF) }.Where(c => c >= 32 && c < 127).ToArray());

    private static string CodecName(string fourcc) => fourcc.ToUpperInvariant() switch
    {
        "H264" or "AVC1" or "X264" => "H.264 / AVC (" + fourcc + ")",
        "HEVC" or "H265" or "HVC1" or "HEV1" => "H.265 / HEVC (" + fourcc + ")",
        "AV01" => "AV1",
        "VP90" or "VP09" => "VP9",
        "VP80" or "VP08" => "VP8",
        "WMV3" => "Windows Media Video 9",
        "WVC1" => "VC-1",
        "MP4V" or "XVID" or "DIVX" or "DX50" or "FMP4" => "MPEG-4 Part 2 (" + fourcc + ")",
        "MPG2" or "MP2V" => "MPEG-2",
        _ => fourcc,
    };

    // ------------------------------------------------------------------ interop

    private const int GPS_BESTEFFORT = 0x40;
    private const int PDFF_DEFAULT = 0;
    private const ushort VT_EMPTY = 0, VT_UI4 = 19, VT_UI8 = 21;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public ulong uhVal;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSGetPropertyKeyFromName(string pszName, out PROPERTYKEY ppropkey);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSFormatForDisplayAlloc(ref PROPERTYKEY key, ref PROPVARIANT propvar, int pdff, out IntPtr ppszDisplay);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);
}
