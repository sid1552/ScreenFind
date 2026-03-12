using System;
using SysDrawing = System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenFind
{
    /// <summary>
    /// Pipeline: 2x upscale, grayscale, auto-contrast, sharpen.
    /// Caller must divide OCR bounding boxes by the returned scale factor.
    /// </summary>
    public static class ImagePreprocessor
    {
        public static (SysDrawing.Bitmap Bitmap, double ScaleFactor) Enhance(SysDrawing.Bitmap source)
        {
            // Skip upscale if image is already huge (4K+) to save memory
            int scale = 2;
            if (source.Width * scale > 7680 || source.Height * scale > 7680)
                scale = 1;

            SysDrawing.Bitmap working;
            if (scale > 1)
            {
                int newW = source.Width * scale;
                int newH = source.Height * scale;
                working = new SysDrawing.Bitmap(newW, newH, PixelFormat.Format32bppArgb);
                using (var g = SysDrawing.Graphics.FromImage(working))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, 0, 0, newW, newH);
                }
            }
            else
            {
                working = (SysDrawing.Bitmap)source.Clone();
            }

            var result = GrayscaleContrastSharpen(working);
            working.Dispose();

            return (result, scale);
        }

        private static SysDrawing.Bitmap GrayscaleContrastSharpen(SysDrawing.Bitmap source)
        {
            int w = source.Width;
            int h = source.Height;

            var gray = new byte[w * h];
            var histogram = new int[256];

            var srcData = source.LockBits(
                new SysDrawing.Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* srcPtr = (byte*)srcData.Scan0;
                    int stride = srcData.Stride;

                    for (int y = 0; y < h; y++)
                    {
                        byte* row = srcPtr + y * stride;
                        int rowOffset = y * w;

                        for (int x = 0; x < w; x++)
                        {
                            int px = x * 4;
                            byte val = (byte)(0.299f * row[px + 2]
                                            + 0.587f * row[px + 1]
                                            + 0.114f * row[px]);
                            gray[rowOffset + x] = val;
                            histogram[val]++;
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(srcData);
            }

            // 2nd/98th percentile stretch — simple min/max won't work because
            // screen captures almost always have pure black and white pixels
            int totalPixels = w * h;
            int lowClip = 0, highClip = 255;
            int cumSum = 0;
            bool foundLow = false;

            for (int i = 0; i < 256; i++)
            {
                cumSum += histogram[i];
                if (!foundLow && cumSum >= totalPixels * 0.02)
                {
                    lowClip = i;
                    foundLow = true;
                }
                if (cumSum >= totalPixels * 0.98)
                {
                    highClip = i;
                    break;
                }
            }

            float range = highClip - lowClip;
            if (range < 10) range = 10;

            for (int i = 0; i < gray.Length; i++)
            {
                float stretched = (gray[i] - lowClip) * 255f / range;
                gray[i] = (byte)Math.Clamp((int)stretched, 0, 255);
            }

            // 3x3 sharpen kernel — crisps text edges softened by upscaling
            var result = new SysDrawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var dstData = result.LockBits(
                new SysDrawing.Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* dstPtr = (byte*)dstData.Scan0;
                    int dstStride = dstData.Stride;

                    for (int y = 0; y < h; y++)
                    {
                        byte* dstRow = dstPtr + y * dstStride;
                        for (int x = 0; x < w; x++)
                        {
                            int val;
                            if (y > 0 && y < h - 1 && x > 0 && x < w - 1)
                            {
                                val = 5 * gray[y * w + x]
                                    - gray[(y - 1) * w + x]
                                    - gray[(y + 1) * w + x]
                                    - gray[y * w + (x - 1)]
                                    - gray[y * w + (x + 1)];
                                val = Math.Clamp(val, 0, 255);
                            }
                            else
                            {
                                val = gray[y * w + x];
                            }

                            int px = x * 4;
                            byte b = (byte)val;
                            dstRow[px]     = b;
                            dstRow[px + 1] = b;
                            dstRow[px + 2] = b;
                            dstRow[px + 3] = 255;
                        }
                    }
                }
            }
            finally
            {
                result.UnlockBits(dstData);
            }

            return result;
        }
    }
}
