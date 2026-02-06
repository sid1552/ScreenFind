using System;
using System.Threading.Tasks;
using PaddleOCRSharp;
using SysDrawing = System.Drawing;

namespace ScreenFind
{
    /// <summary>
    /// Manages a single PaddleOCR engine instance for the entire app lifetime.
    ///
    /// Why a singleton? Loading PaddleOCR models takes ~1-2 seconds and uses significant
    /// memory. We load once on first use and reuse across all captures. The engine is
    /// lazy-initialized so it only loads if the user actually enables PaddleOCR.
    ///
    /// Thread safety: PaddleOCR's DetectText is not documented as thread-safe,
    /// so we use a lock to prevent concurrent calls (e.g. multi-monitor captures).
    /// </summary>
    public static class PaddleOcrEngineManager
    {
        // Lazy<T> ensures the engine is created exactly once, on first access.
        // (Like Python's functools.lru_cache or a module-level singleton)
        private static Lazy<PaddleOCREngine> _lazyEngine = new(CreateEngine);

        // Lock object to serialize DetectText calls (like Python's threading.Lock)
        private static readonly object _detectLock = new();

        private static PaddleOCREngine CreateEngine()
        {
            // Tuned parameters for screen OCR speed:
            var param = new OCRParameter
            {
                // Use all available CPU cores (Environment.ProcessorCount is like
                // Python's os.cpu_count() — returns the number of logical processors)
                cpu_math_library_num_threads = Environment.ProcessorCount,

                // Intel MKL-DNN acceleration — significant speedup on Intel/AMD CPUs
                enable_mkldnn = true,

                // Skip angle classification — screen text is always upright,
                // so we don't need to detect rotated text. Saves ~20% time.
                cls = false,

                // Detection and recognition both needed
                det = true,
                rec = true,
            };

            // null config = use built-in default models (Chinese + English)
            return new PaddleOCREngine(null, param);
        }

        /// <summary>
        /// Pre-load the engine on a background thread so the first capture is fast.
        /// Called when the user checks the PaddleOCR checkbox.
        /// "Fire and forget" — we don't await the result.
        /// (Like Python's threading.Thread(target=func).start())
        /// </summary>
        public static void Warmup()
        {
            Task.Run(() =>
            {
                try { _ = _lazyEngine.Value; }
                catch { /* will fail again on first use, handled there */ }
            });
        }

        /// <summary>
        /// Run OCR on a System.Drawing.Bitmap directly (no file I/O needed).
        /// Thread-safe — only one DetectText call runs at a time.
        /// </summary>
        public static OCRResult DetectText(SysDrawing.Bitmap bitmap)
        {
            var engine = _lazyEngine.Value;

            // lock ensures only one thread calls DetectText at a time
            // (like Python's "with lock:")
            lock (_detectLock)
            {
                return engine.DetectText(bitmap);
            }
        }

        /// <summary>
        /// Clean up the native PaddleOCR resources on app exit.
        /// Called from App.xaml.cs OnExit. Safe to call even if engine was never created.
        /// </summary>
        public static void Shutdown()
        {
            if (_lazyEngine.IsValueCreated)
            {
                try
                {
                    _lazyEngine.Value.Dispose();
                }
                catch { /* best effort cleanup */ }
            }
        }
    }
}
