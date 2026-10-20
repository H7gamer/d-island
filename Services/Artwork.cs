// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DIsland.Services;

/// <summary>
/// Album art as provided by the media app: a still image, or a fully composited frame sequence when
/// the app supplies an animated GIF.
/// </summary>
public sealed class Artwork
{
    private const int MaxFrames = 300;
    private const long MaxTotalPixels = 8_000_000; // ≤ 32 MB of Pbgra32 frames after downscaling

    private readonly BitmapSource[]? _frames;
    private readonly TimeSpan[]? _delays;

    private Artwork(BitmapSource still, BitmapSource[]? frames = null, TimeSpan[]? delays = null)
    {
        Still = still;
        _frames = frames;
        _delays = delays;
    }

    public BitmapSource Still { get; }
    public bool IsAnimated => _frames is { Length: > 1 };

    /// <summary>
    /// Decodes on a short-lived STA thread: GIF compositing uses visuals (which require STA) and can
    /// take a moment for long animations. The resulting bitmaps are frozen, so they cross threads.
    /// </summary>
    /// <param name="maxSize">Longest side to keep, in pixels: decode no larger than it will be shown.</param>
    public static Task<Artwork?> DecodeAsync(byte[] data, int maxSize)
    {
        var result = new TaskCompletionSource<Artwork?>();
        var thread = new Thread(() => result.SetResult(Decode(data, maxSize))) { IsBackground = true, Name = "Artwork decode" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    public static Artwork? Decode(byte[] data, int maxSize)
    {
        try
        {
            if (data.Length > 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                var animated = DecodeGif(data, maxSize);
                if (animated is not null) return animated;
            }

            // Read just the header for the size, then decode straight to the target size
            // (never upscale: DecodePixelWidth would enlarge small images).
            var header = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (Math.Max(header.PixelWidth, header.PixelHeight) > maxSize)
            {
                if (header.PixelWidth >= header.PixelHeight) bitmap.DecodePixelWidth = maxSize;
                else bitmap.DecodePixelHeight = maxSize;
            }
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.EndInit();
            bitmap.Freeze();
            return new Artwork(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates a brush showing this artwork, animating it if it has frames.</summary>
    public ImageBrush CreateBrush(Stretch stretch = Stretch.UniformToFill)
    {
        var brush = new ImageBrush(Still) { Stretch = stretch };
        if (IsAnimated)
        {
            var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var time = TimeSpan.Zero;
            for (var i = 0; i < _frames!.Length; i++)
            {
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(_frames[i], KeyTime.FromTimeSpan(time)));
                time += _delays![i];
            }
            animation.Duration = time;
            brush.BeginAnimation(ImageBrush.ImageSourceProperty, animation);
        }
        return brush;
    }

    /// <summary>
    /// GIF frames are often partial patches with disposal rules, so each frame is composited onto the
    /// running canvas to produce full images that can simply be swapped.
    /// </summary>
    private static Artwork? DecodeGif(byte[] data, int maxSize)
    {
        var decoder = new GifBitmapDecoder(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count <= 1) return null;

        var sourceWidth = ReadUShort(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        var sourceHeight = ReadUShort(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

        // Composite straight into downscaled frames: memory is per output pixel.
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(sourceHeight * scale));

        var count = Math.Min(decoder.Frames.Count, MaxFrames);
        if ((long)width * height * count > MaxTotalPixels) count = (int)Math.Max(1, MaxTotalPixels / ((long)width * height));
        if (count <= 1) return null;

        var full = new Rect(0, 0, width, height);
        var toOutput = new ScaleTransform(scale, scale);
        toOutput.Freeze();
        var frames = new BitmapSource[count];
        var delays = new TimeSpan[count];
        BitmapSource? canvas = null;
        BitmapSource? restorePoint = null;
        var lastDisposal = 0;
        var lastRect = Rect.Empty;

        for (var i = 0; i < count; i++)
        {
            var frame = decoder.Frames[i];
            var meta = frame.Metadata as BitmapMetadata;
            var rect = new Rect(
                ReadUShort(meta, "/imgdesc/Left") ?? 0,
                ReadUShort(meta, "/imgdesc/Top") ?? 0,
                frame.PixelWidth,
                frame.PixelHeight);
            var disposal = ReadByte(meta, "/grctlext/Disposal") ?? 0;
            var delayCs = ReadUShort(meta, "/grctlext/Delay") ?? 0;
            delays[i] = TimeSpan.FromMilliseconds(delayCs <= 1 ? 100 : delayCs * 10); // browsers treat 0/1 as 100ms

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Apply the previous frame's disposal before drawing this one.
                if (lastDisposal == 3 && restorePoint is not null)
                {
                    dc.DrawImage(restorePoint, full);
                }
                else if (canvas is not null)
                {
                    if (lastDisposal == 2)
                    {
                        var cleared = new Rect(lastRect.X * scale, lastRect.Y * scale, lastRect.Width * scale, lastRect.Height * scale);
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                            new RectangleGeometry(full), new RectangleGeometry(cleared)));
                    }
                    dc.DrawImage(canvas, full);
                    if (lastDisposal == 2) dc.Pop();
                }
                dc.PushTransform(toOutput); // frame rects are in source GIF coordinates
                dc.DrawImage(frame, rect);
                dc.Pop();
            }

            if (disposal == 3) restorePoint = canvas;

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            frames[i] = bitmap;

            canvas = bitmap;
            lastDisposal = disposal;
            lastRect = rect;
        }

        return new Artwork(frames[0], frames, delays);
    }

    private static int? ReadUShort(BitmapMetadata? meta, string query)
    {
        try { return meta?.GetQuery(query) is ushort v ? v : null; }
        catch (Exception) { return null; }
    }

    private static int? ReadByte(BitmapMetadata? meta, string query)
    {
        try { return meta?.GetQuery(query) is byte v ? v : null; }
        catch (Exception) { return null; }
    }
}
