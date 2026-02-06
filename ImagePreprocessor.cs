using System;
using SysDrawing = System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenFind
{
    /// <summary>
    /// Preprocesses screenshots to improve OCR accuracy.
    /// Pipeline: 2x upscale → grayscale → auto-contrast → sharpen.
    /// Returns the enhanced bitmap AND the upscale factor, so the caller
    /// can divide OCR bounding boxes by it to map back to screen coords.
    /// </summary>
    public static class ImagePreprocessor
    {
        /// <summary>
        /// Enhance a screenshot for better OCR accuracy.
        /// Returns (enhanced bitmap, scale factor applied).
        /// Caller must divide OCR bounding boxes by ScaleFactor.
        /// </summary>
        public static (SysDrawing.Bitmap Bitmap, double ScaleFactor) Enhance(SysDrawing.Bitmap source)
        {
            // ── Step 1: Upscale 2x ──────────────────────────────────────
            // Doubling resolution is the single biggest win — small/thin text
            // that Windows OCR misses becomes readable at 2x.
            // Skip upscale if image is already huge (4K+) to save memory.
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
                    // Bicubic gives the sharpest upscale for text edges
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, 0, 0, newW, newH);
                }
            }
            else
            {
                // Clone so we don't modify the original
                working = (SysDrawing.Bitmap)source.Clone();
            }

            // ── Step 2: Grayscale + auto-contrast + sharpen ─────────────
            var result = GrayscaleContrastSharpen(working);
            working.Dispose();

            return (result, scale);
        }

        /// <summary>
        /// Three-stage pixel processing:
        /// 1) Convert to grayscale using luminance weights
        /// 2) Auto-stretch contrast using 2nd/98th percentile clipping
        ///    (adapts to the actual image instead of a fixed 1.5x boost)
        /// 3) Sharpen with 3×3 kernel to crispen text edges
        /// </summary>
        private static SysDrawing.Bitmap GrayscaleContrastSharpen(SysDrawing.Bitmap source)
        {
            int w = source.Width;
            int h = source.Height;

            // ── Pass 1: Grayscale + histogram ───────────────────────────
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
                            // Standard luminance: 0.299R + 0.587G + 0.114B
                            byte val = (byte)(0.299f * row[px + 2]   // R
                                            + 0.587f * row[px + 1]   // G
                                            + 0.114f * row[px]);     // B
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

            // ── Auto-contrast: percentile-based stretch ─────────────────
            // Simple min/max doesn't work for screen captures — there's almost
            // always pure black (taskbar) and pure white (backgrounds), so
            // min=0/max=255 and nothing gets stretched. Instead, use 2nd/98th
            // percentile to find the "real" intensity range of the text.
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
            if (range < 10) range = 10; // safety for very flat images

            for (int i = 0; i < gray.Length; i++)
            {
                float stretched = (gray[i] - lowClip) * 255f / range;
                gray[i] = (byte)Math.Clamp((int)stretched, 0, 255);
            }

            // ── Pass 2: Sharpen + write output ──────────────────────────
            // 3×3 sharpening kernel:  [0,-1,0 / -1,5,-1 / 0,-1,0]
            // Makes text edges crisper — especially helpful after upscaling,
            // which can soften edges slightly even with bicubic interpolation.
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
                            // Apply kernel to inner pixels, skip 1px border
                            if (y > 0 && y < h - 1 && x > 0 && x < w - 1)
                            {
                                val = 5 * gray[y * w + x]
                                    - gray[(y - 1) * w + x]       // top
                                    - gray[(y + 1) * w + x]       // bottom
                                    - gray[y * w + (x - 1)]       // left
                                    - gray[y * w + (x + 1)];      // right
                                val = Math.Clamp(val, 0, 255);
                            }
                            else
                            {
                                val = gray[y * w + x]; // border pixels: copy as-is
                            }

                            int px = x * 4;
                            byte b = (byte)val;
                            dstRow[px]     = b;   // B
                            dstRow[px + 1] = b;   // G
                            dstRow[px + 2] = b;   // R
                            dstRow[px + 3] = 255; // A
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
