using System;
using SysDrawing = System.Drawing;
using System.Drawing.Imaging;

namespace ScreenFind
{
    /// <summary>
    /// Preprocesses screenshots to improve OCR accuracy on low-contrast text.
    /// Converts to grayscale and boosts contrast ~1.5x.
    /// </summary>
    public static class ImagePreprocessor
    {
        public static SysDrawing.Bitmap Enhance(SysDrawing.Bitmap source)
        {
            int w = source.Width;
            int h = source.Height;

            var result = new SysDrawing.Bitmap(w, h, PixelFormat.Format32bppArgb);

            var srcData = source.LockBits(
                new SysDrawing.Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            var dstData = result.LockBits(
                new SysDrawing.Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* srcPtr = (byte*)srcData.Scan0;
                    byte* dstPtr = (byte*)dstData.Scan0;
                    int stride = srcData.Stride;
                    float contrast = 1.5f;
                    float mid = 128f;

                    for (int y = 0; y < h; y++)
                    {
                        byte* srcRow = srcPtr + y * stride;
                        byte* dstRow = dstPtr + y * stride;

                        for (int x = 0; x < w; x++)
                        {
                            int offset = x * 4;
                            byte b = srcRow[offset];
                            byte g = srcRow[offset + 1];
                            byte r = srcRow[offset + 2];
                            byte a = srcRow[offset + 3];

                            // Convert to grayscale (standard luminance weights)
                            float gray = 0.299f * r + 0.587f * g + 0.114f * b;

                            // Boost contrast: stretch away from midpoint
                            float enhanced = (gray - mid) * contrast + mid;
                            byte val = (byte)Math.Clamp((int)enhanced, 0, 255);

                            dstRow[offset] = val;     // B
                            dstRow[offset + 1] = val;  // G
                            dstRow[offset + 2] = val;  // R
                            dstRow[offset + 3] = a;    // A
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }

            return result;
        }
    }
}
