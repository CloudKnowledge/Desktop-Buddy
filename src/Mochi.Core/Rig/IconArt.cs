using SkiaSharp;

namespace Mochi.Core.Rig;

/// <summary>Renders Mochi's face as PNG/ICO images for the app icon, tray icon and menus.</summary>
public static class IconArt
{
    public static SKBitmap RenderFace(PetLook look, int size, bool happy = false)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        // The head spans roughly y = -45 (ear tips) to +36 (chest fluff) in model units.
        float s = size / 90f;
        canvas.Translate(size / 2f, size / 2f + 4.5f * s);
        canvas.Scale(s);
        using var renderer = new PetRenderer(look with { Accessory = AccessoryKind.None });
        var pose = new PetPose();
        pose.ResetToNeutral();
        pose.HeadTurn = 0;
        pose.LookX = 0;
        pose.LookY = 0.05f;
        pose.Blush = 0.55f;
        pose.Smile = 0.8f;
        if (happy) pose.HappyEyes = 1;
        renderer.DrawHeadOnly(canvas, pose);
        return bmp;
    }

    public static byte[] RenderFacePng(PetLook look, int size)
    {
        using var bmp = RenderFace(look, size);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Builds a multi-resolution .ico with PNG-compressed frames (supported since Windows Vista).</summary>
    public static byte[] BuildIco(PetLook look, params int[] sizes)
    {
        var frames = sizes.Select(s => (Size: s, Png: RenderFacePng(look, s))).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(f.Png.Length);
            w.Write(offset);
            offset += f.Png.Length;
        }
        foreach (var f in frames) w.Write(f.Png);
        w.Flush();
        return ms.ToArray();
    }
}
