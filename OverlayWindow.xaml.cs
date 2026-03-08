using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PaddleOCRSharp;
using SysDrawing = System.Drawing;
using SysForms = System.Windows.Forms;

namespace ScreenFind
{
    public partial class OverlayWindow : Window
    {
        // ─── Win32 for precise multi-monitor positioning ──────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // ─── OCR data ──────────────────────────────────────────────────
        private SysDrawing.Bitmap _capturedBitmap;
        private readonly bool _enhanceOcr;
        private readonly bool _dragToSelect;
        private readonly bool _usePaddleOcr;
        private List<OcrLineInfo>? _ocrLines;
        private bool _ocrCompleted;

        // ─── Multi-monitor ─────────────────────────────────────────────
        private readonly SysForms.Screen _targetScreen;
        private readonly bool _isPrimary;
        private string _pendingQuery = "";   // search query from primary (for secondaries)

        // ─── Events for cross-monitor coordination ─────────────────────
        /// <summary>Fired by primary when the search query changes.</summary>
        public event Action<string>? SearchChanged;
        /// <summary>Fired when Escape is pressed — tells MainWindow to close all overlays.</summary>
        public event Action? CloseAllRequested;

        // ─── Search state ──────────────────────────────────────────────
        private List<MatchResult> _matches = new();
        private int _currentIndex = -1;

        // ─── DPI scaling (physical pixels → WPF DIPs) ──────────────────
        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        // ─── Drag-to-select state ────────────────────────────────────────
        private bool _isDragging;
        private Point _dragStartDip;              // mouse-down position in WPF DIPs
        private Rectangle? _lassoRect;            // the visual drag rectangle
        private List<OcrWordInfo> _selectedWords = new();
        private List<Rectangle> _selectionHighlights = new();  // reusable highlight pool

        // ─── "Copied!" feedback timer (single instance to prevent race conditions)
        private System.Windows.Threading.DispatcherTimer? _feedbackTimer;
        private string _savedMatchInfoText = "";

        // ────────────────────────────────────────────────────────────────
        public OverlayWindow(SysForms.Screen targetScreen,
            bool enhanceOcr = false, bool dragToSelect = true, bool isPrimary = true,
            bool usePaddleOcr = false)
        {
            InitializeComponent();
            _targetScreen = targetScreen;
            _enhanceOcr = enhanceOcr;
            _dragToSelect = dragToSelect;
            _isPrimary = isPrimary;
            _usePaddleOcr = usePaddleOcr;
            Opacity = 0; // hidden until Activate()

            // Wire mouse events once (survive across Activate/Dismiss cycles)
            SelectionCanvas.MouseLeftButtonDown += SelectionCanvas_MouseLeftButtonDown;
            SelectionCanvas.MouseMove += SelectionCanvas_MouseMove;
            SelectionCanvas.MouseLeftButtonUp += SelectionCanvas_MouseLeftButtonUp;

            // Secondary monitors: hide search bar permanently
            if (!_isPrimary)
            {
                SearchBarBorder.Visibility = Visibility.Collapsed;
                LoadingBadge.Visibility = Visibility.Collapsed;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  PRE-WARMING — forces HWND creation + full WPF layout at startup
        //  so the first hotkey press is near-instant.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pre-create the native window (HWND) and run the first WPF layout pass.
        /// The overlay stays invisible (Opacity=0). Call once at startup.
        /// </summary>
        public void Prewarm()
        {
            // Show() creates the HWND and triggers layout, but Opacity=0 so nothing visible.
            // Then immediately Hide() so it's not in the way.
            Show();
            Hide();
        }

        // ════════════════════════════════════════════════════════════════
        //  ACTIVATE / DISMISS — reuse the pre-warmed window each time
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Set new screenshot, reset all state, show the overlay, and start OCR.
        /// Called each time the hotkey fires.
        /// </summary>
        public async void Activate(SysDrawing.Bitmap screenshot)
        {
            // Dispose previous screenshot
            _capturedBitmap?.Dispose();
            _capturedBitmap = screenshot;

            // Reset all search/highlight/selection state
            ResetState();

            // Reposition on the target monitor and update DPI
            var hwnd = new WindowInteropHelper(this).Handle;
            var bounds = _targetScreen.Bounds;
            MoveWindow(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
            uint dpi = GetDpiForWindow(hwnd);
            _scaleX = dpi / 96.0;
            _scaleY = dpi / 96.0;

            // Show the captured screenshot as background
            SetScreenshotBackground();

            // Make visible and show
            Opacity = 1;
            Show();

            if (_isPrimary)
                SearchBox.Focus();

            // Run OCR in the background
            await RunOcrAsync();
        }

        /// <summary>
        /// Hide the overlay without destroying it. The window stays in memory
        /// so the next Activate() is near-instant (no HWND/layout overhead).
        /// </summary>
        public void Dismiss()
        {
            Opacity = 0;
            Hide();
        }

        /// <summary>
        /// Clear all state so the overlay is fresh for the next Activate() call.
        /// </summary>
        private void ResetState()
        {
            // Search state
            SearchBox.Text = "";
            _ocrLines = null;
            _ocrCompleted = false;
            _matches.Clear();
            _currentIndex = -1;
            _pendingQuery = "";
            MatchInfo.Text = "";

            // Highlights
            HighlightCanvas.Children.Clear();

            // Selection state
            _isDragging = false;
            _selectedWords.Clear();
            foreach (var r in _selectionHighlights)
                r.Visibility = Visibility.Collapsed;
            _lassoRect = null;
            SelectionCanvas.IsHitTestVisible = false;
            SelectionCanvas.Cursor = Cursors.Arrow;

            // Loading badge
            if (_isPrimary)
                LoadingBadge.Visibility = Visibility.Visible;

            // Feedback timer
            _feedbackTimer?.Stop();
            _savedMatchInfoText = "";
        }

        /// <summary>
        /// Fires after HWND is created but BEFORE first render — positions the window
        /// on the correct monitor. Only runs once (during Prewarm or first Show).
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Position window on target monitor
            var hwnd = new WindowInteropHelper(this).Handle;
            var bounds = _targetScreen.Bounds;
            MoveWindow(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);

            // Get DPI
            uint dpi = GetDpiForWindow(hwnd);
            _scaleX = dpi / 96.0;
            _scaleY = dpi / 96.0;

            // Don't set Opacity=1 here — Activate() controls visibility
        }

        /// <summary>
        /// Convert the System.Drawing.Bitmap to a WPF BitmapSource and display it.
        /// The BitmapSource DPI is set to the screen's actual DPI so that
        /// 1 image pixel = 1 physical screen pixel → pixel-perfect alignment.
        /// </summary>
        private void SetScreenshotBackground()
        {
            var bitmapData = _capturedBitmap.LockBits(
                new SysDrawing.Rectangle(0, 0, _capturedBitmap.Width, _capturedBitmap.Height),
                SysDrawing.Imaging.ImageLockMode.ReadOnly,
                SysDrawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                var bitmapSource = BitmapSource.Create(
                    bitmapData.Width,
                    bitmapData.Height,
                    96 * _scaleX,   // DPI X
                    96 * _scaleY,   // DPI Y
                    PixelFormats.Bgra32,
                    null,
                    bitmapData.Scan0,
                    bitmapData.Stride * bitmapData.Height,
                    bitmapData.Stride);

                bitmapSource.Freeze(); // allow cross-thread access
                ScreenshotImage.Source = bitmapSource;
            }
            finally
            {
                _capturedBitmap.UnlockBits(bitmapData);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  OCR  (uses the Windows 10+ built-in OCR engine)
        // ════════════════════════════════════════════════════════════════

        private async Task RunOcrAsync()
        {
            try
            {
                // Preprocessing may upscale the image — need to scale OCR bounds back down
                double preprocessScale = 1.0;

                if (_usePaddleOcr)
                {
                    // PaddleOCR path: pass Bitmap directly (no temp file needed)
                    // If enhance is on, preprocess first then pass the enhanced bitmap
                    if (_enhanceOcr)
                    {
                        var (enhanced, scaleFactor) = ImagePreprocessor.Enhance(_capturedBitmap);
                        preprocessScale = scaleFactor;
                        try
                        {
                            await RunPaddleOcrAsync(enhanced, preprocessScale);
                        }
                        finally
                        {
                            enhanced.Dispose();
                        }
                    }
                    else
                    {
                        await RunPaddleOcrAsync(_capturedBitmap, preprocessScale);
                    }
                }
                else
                {
                    // Windows OCR path: needs a temp file for WinRT StorageFile API
                    var tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), $"screenfind_{Guid.NewGuid():N}.bmp");

                    if (_enhanceOcr)
                    {
                        var (enhanced, scaleFactor) = ImagePreprocessor.Enhance(_capturedBitmap);
                        preprocessScale = scaleFactor;
                        enhanced.Save(tempPath, SysDrawing.Imaging.ImageFormat.Bmp);
                        enhanced.Dispose();
                    }
                    else
                    {
                        _capturedBitmap.Save(tempPath, SysDrawing.Imaging.ImageFormat.Bmp);
                    }

                    try
                    {
                        await RunWindowsOcrAsync(tempPath, preprocessScale);
                    }
                    finally
                    {
                        try { File.Delete(tempPath); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingBadge.Visibility = Visibility.Collapsed;
                    MatchInfo.Text = $"OCR error: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Run OCR using the Windows 10+ built-in WinRT OCR engine.
        /// This is the original/default path — fast but less accurate on small text.
        /// </summary>
        private async Task RunWindowsOcrAsync(string tempPath, double preprocessScale)
        {
            // Open via WinRT StorageFile → BitmapDecoder → SoftwareBitmap
            var storageFile = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(tempPath);

            using var stream = await storageFile
                .OpenAsync(Windows.Storage.FileAccessMode.Read);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder
                .CreateAsync(stream);

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            // Create OCR engine (uses the user's installed languages)
            var engine = Windows.Media.Ocr.OcrEngine
                .TryCreateFromUserProfileLanguages();

            if (engine == null)
            {
                // Fallback: try English
                engine = Windows.Media.Ocr.OcrEngine
                    .TryCreateFromLanguage(
                        new Windows.Globalization.Language("en-US"));
            }

            if (engine == null)
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingBadge.Visibility = Visibility.Collapsed;
                    MatchInfo.Text = "No OCR language pack";
                });
                return;
            }

            // Run OCR
            var ocrResult = await engine.RecognizeAsync(softwareBitmap);

            // Convert results into our data model
            _ocrLines = new List<OcrLineInfo>();
            foreach (var line in ocrResult.Lines)
            {
                var lineInfo = new OcrLineInfo
                {
                    // Divide bounds by preprocessScale to map back to original
                    // screen pixels (upscaling makes the image larger, so OCR
                    // returns larger coords that we need to shrink back down)
                    Words = line.Words.Select(w => new OcrWordInfo
                    {
                        Text = w.Text,
                        Bounds = new Rect(
                            w.BoundingRect.X / preprocessScale,
                            w.BoundingRect.Y / preprocessScale,
                            w.BoundingRect.Width / preprocessScale,
                            w.BoundingRect.Height / preprocessScale)
                    }).ToList()
                };
                lineInfo.FullText = string.Join(" ", lineInfo.Words.Select(w => w.Text));
                _ocrLines.Add(lineInfo);
            }

            OnOcrCompleted();
        }

        /// <summary>
        /// Run OCR using PaddleOCR (more accurate, especially for small/low-contrast text).
        /// PaddleOCR is CPU-bound so we run it on a background thread via Task.Run()
        /// to keep the WPF UI responsive.
        ///
        /// PaddleOCR returns line-level bounding boxes (not word-level like Windows OCR),
        /// so we estimate per-word bounds by splitting proportionally by character count.
        /// </summary>
        private async Task RunPaddleOcrAsync(SysDrawing.Bitmap bitmap, double preprocessScale)
        {
            // Run PaddleOCR on a background thread (CPU-intensive)
            // Passing the Bitmap directly avoids saving/reading a temp file.
            // Task.Run moves this off the UI thread so the overlay stays responsive
            var ocrResult = await Task.Run(() => PaddleOcrEngineManager.DetectText(bitmap));

            // Convert PaddleOCR results into our OcrLineInfo/OcrWordInfo model
            _ocrLines = new List<OcrLineInfo>();

            if (ocrResult?.TextBlocks != null)
            {
                foreach (var block in ocrResult.TextBlocks)
                {
                    if (string.IsNullOrWhiteSpace(block.Text)) continue;

                    // PaddleOCR gives us BoxPoints: 4 corners of a rotated rectangle.
                    // We compute an axis-aligned bounding rect from the corner points.
                    // BoxPoints format: [top-left, top-right, bottom-right, bottom-left]
                    // Each point has X and Y properties.
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    foreach (var point in block.BoxPoints)
                    {
                        if (point.X < minX) minX = point.X;
                        if (point.Y < minY) minY = point.Y;
                        if (point.X > maxX) maxX = point.X;
                        if (point.Y > maxY) maxY = point.Y;
                    }

                    // Scale back down if image was preprocessed (upscaled)
                    minX /= preprocessScale;
                    minY /= preprocessScale;
                    maxX /= preprocessScale;
                    maxY /= preprocessScale;

                    double blockWidth = maxX - minX;
                    double blockHeight = maxY - minY;

                    // Split the block text into words and estimate per-word bounding boxes.
                    // PaddleOCR only gives line-level boxes, so we divide the line width
                    // proportionally by character count (like splitting a ruler into segments).
                    var words = block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int totalChars = words.Sum(w => w.Length);

                    var wordInfos = new List<OcrWordInfo>();
                    double currentX = minX;

                    for (int i = 0; i < words.Length; i++)
                    {
                        // Each word gets a proportional slice of the total width
                        double wordWidth = (totalChars > 0)
                            ? blockWidth * words[i].Length / totalChars
                            : blockWidth / words.Length;

                        wordInfos.Add(new OcrWordInfo
                        {
                            Text = words[i],
                            Bounds = new Rect(currentX, minY, wordWidth, blockHeight)
                        });

                        currentX += wordWidth;
                    }

                    var lineInfo = new OcrLineInfo
                    {
                        Words = wordInfos,
                        FullText = string.Join(" ", words)
                    };
                    _ocrLines.Add(lineInfo);
                }
            }

            OnOcrCompleted();
        }

        /// <summary>
        /// Shared logic that runs after either OCR engine finishes.
        /// Updates UI state: hides loading badge, enables drag-to-select,
        /// and applies any pending search query.
        /// </summary>
        private void OnOcrCompleted()
        {
            _ocrCompleted = true;

            Dispatcher.Invoke(() =>
            {
                if (_isPrimary)
                    LoadingBadge.Visibility = Visibility.Collapsed;

                // Enable drag-to-select now that OCR data is ready (if enabled)
                if (_dragToSelect)
                {
                    SelectionCanvas.IsHitTestVisible = true;
                    SelectionCanvas.Cursor = Cursors.Cross;
                }

                // Apply any pending search query
                // Primary: user may have typed while OCR was running
                // Secondary: primary may have broadcast a query before OCR finished
                var query = _isPrimary ? SearchBox.Text : _pendingQuery;
                if (!string.IsNullOrEmpty(query))
                    UpdateHighlights(query);
            });
        }

        // ════════════════════════════════════════════════════════════════
        //  SEARCH  (called every time the user types a character)
        // ════════════════════════════════════════════════════════════════

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Clear any drag selection when the user types
            SelectionCanvas.Children.Clear();
            _selectedWords.Clear();

            if (_ocrCompleted)
                UpdateHighlights(SearchBox.Text);

            // Broadcast search query to secondary overlays
            SearchChanged?.Invoke(SearchBox.Text);
        }

        /// <summary>
        /// Called by MainWindow to apply the primary's search query on this (secondary) overlay.
        /// </summary>
        public void ApplySearch(string query)
        {
            _pendingQuery = query;
            if (!_ocrCompleted) return;

            // Clear drag selection
            SelectionCanvas.Children.Clear();
            _selectedWords.Clear();

            UpdateHighlights(query);
        }

        /// <summary>
        /// Find all occurrences of <paramref name="query"/> in the OCR text
        /// and draw highlight rectangles on screen.
        /// Supports multi-word queries (matches substring across a line).
        /// </summary>
        private void UpdateHighlights(string query)
        {
            HighlightCanvas.Children.Clear();
            _matches.Clear();
            _currentIndex = -1;

            if (string.IsNullOrWhiteSpace(query) || _ocrLines == null)
            {
                MatchInfo.Text = "";
                return;
            }

            // Track which (lineIndex, wordIndex) pairs are covered by exact matches
            var exactMatchedWords = new HashSet<(int LineIdx, int WordIdx)>();

            // ── Pass 1: Exact substring match (unchanged logic) ──
            for (int lineIdx = 0; lineIdx < _ocrLines.Count; lineIdx++)
            {
                var line = _ocrLines[lineIdx];

                // Build a map: character offset → word index
                var wordSpans = new List<(int Start, int End, int WordIdx)>();
                int charPos = 0;
                for (int i = 0; i < line.Words.Count; i++)
                {
                    int start = charPos;
                    int end = charPos + line.Words[i].Text.Length - 1;
                    wordSpans.Add((start, end, i));
                    charPos = end + 2; // +1 for last char, +1 for the space
                }

                // Search for the query in the full line text
                int searchFrom = 0;
                while (searchFrom <= line.FullText.Length - query.Length)
                {
                    int idx = line.FullText.IndexOf(
                        query, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    int matchEnd = idx + query.Length - 1;

                    // Which words overlap with chars [idx..matchEnd]?
                    var hitSpans = wordSpans
                        .Where(s => s.End >= idx && s.Start <= matchEnd)
                        .ToList();

                    var hitWords = hitSpans.Select(s => line.Words[s.WordIdx]).ToList();

                    if (hitWords.Count > 0)
                    {
                        // Mark these words as exact-matched
                        foreach (var s in hitSpans)
                            exactMatchedWords.Add((lineIdx, s.WordIdx));

                        double minX = hitWords.Min(w => w.Bounds.X);
                        double minY = hitWords.Min(w => w.Bounds.Y);
                        double maxX = hitWords.Max(w => w.Bounds.Right);
                        double maxY = hitWords.Max(w => w.Bounds.Bottom);

                        _matches.Add(new MatchResult
                        {
                            Bounds = new Rect(minX, minY, maxX - minX, maxY - minY),
                            Text = string.Join(" ", hitWords.Select(w => w.Text))
                        });
                    }

                    searchFrom = idx + 1;
                }
            }

            // ── Pass 2: Fuzzy word-level match (skip for very short queries) ──
            if (query.Length >= 3)
            {
                var fuzzyMatches = FuzzyMatcher.FindFuzzyMatches(
                    _ocrLines, query, exactMatchedWords);
                _matches.AddRange(fuzzyMatches);
            }

            // Draw all highlights
            DrawAllHighlights();

            // Auto-select first match
            if (_matches.Count > 0)
            {
                _currentIndex = 0;
                DrawCurrentMatchHighlight();
            }

            UpdateMatchInfoText();
        }

        // ════════════════════════════════════════════════════════════════
        //  HIGHLIGHT RENDERING
        // ════════════════════════════════════════════════════════════════

        // Colors
        private static readonly SolidColorBrush MatchStroke =
            new(Color.FromArgb(255, 250, 200, 50));
        private static readonly SolidColorBrush MatchFill =
            new(Color.FromArgb(55, 255, 255, 0));

        private static readonly SolidColorBrush CurrentStroke =
            new(Color.FromArgb(255, 255, 100, 0));
        private static readonly SolidColorBrush CurrentFill =
            new(Color.FromArgb(110, 255, 150, 0));

        // Fuzzy match colors (blue-ish tint to distinguish from exact matches)
        private static readonly SolidColorBrush FuzzyStroke =
            new(Color.FromArgb(255, 80, 160, 255));
        private static readonly SolidColorBrush FuzzyFill =
            new(Color.FromArgb(55, 80, 160, 255));

        // Current fuzzy match (brighter blue, like orange is to yellow)
        private static readonly SolidColorBrush CurrentFuzzyStroke =
            new(Color.FromArgb(255, 30, 120, 255));
        private static readonly SolidColorBrush CurrentFuzzyFill =
            new(Color.FromArgb(110, 30, 120, 255));

        // Drag-to-select colors (green)
        private static readonly SolidColorBrush SelectionStroke =
            new(Color.FromArgb(255, 50, 205, 50));
        private static readonly SolidColorBrush SelectionFill =
            new(Color.FromArgb(55, 50, 205, 50));

        // Lasso rectangle (white dashed)
        private static readonly SolidColorBrush LassoStroke =
            new(Color.FromArgb(180, 255, 255, 255));
        private static readonly SolidColorBrush LassoFill =
            new(Color.FromArgb(20, 255, 255, 255));

        /// <summary>
        /// Draw yellow highlight rectangles for every match.
        /// </summary>
        private void DrawAllHighlights()
        {
            HighlightCanvas.Children.Clear();

            for (int i = 0; i < _matches.Count; i++)
            {
                var m = _matches[i];
                var padded = new Rect(
                    m.Bounds.X - 4, m.Bounds.Y - 4,
                    m.Bounds.Width + 8, m.Bounds.Height + 8);
                // Blue for fuzzy matches, yellow for exact matches
                var stroke = m.IsFuzzy ? FuzzyStroke : MatchStroke;
                var fill = m.IsFuzzy ? FuzzyFill : MatchFill;
                var rect = MakeRect(padded, stroke, 2, fill, 5, i);
                HighlightCanvas.Children.Add(rect);
            }
        }

        /// <summary>
        /// Draw an extra, brighter rectangle for the currently-selected match.
        /// </summary>
        private void DrawCurrentMatchHighlight()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;

            var m = _matches[_currentIndex];
            // Slightly larger rect with a brighter color
            var inflated = new Rect(
                m.Bounds.X - 6, m.Bounds.Y - 6,
                m.Bounds.Width + 12, m.Bounds.Height + 12);

            var stroke = m.IsFuzzy ? CurrentFuzzyStroke : CurrentStroke;
            var fill = m.IsFuzzy ? CurrentFuzzyFill : CurrentFill;
            var rect = MakeRect(inflated, stroke, 3, fill, 7, _currentIndex);
            HighlightCanvas.Children.Add(rect);
        }

        /// <summary>
        /// Helper: create a WPF Rectangle positioned on the canvas.
        /// Coordinates are converted from physical pixels → DIPs.
        /// When matchIndex is provided, the rect becomes clickable (copies match text).
        /// </summary>
        private Rectangle MakeRect(
            Rect pixelBounds,
            SolidColorBrush stroke, double strokeThickness,
            SolidColorBrush fill, double radius,
            int? matchIndex = null)
        {
            var r = new Rectangle
            {
                Stroke = stroke,
                StrokeThickness = strokeThickness,
                Fill = fill,
                Width  = pixelBounds.Width  / _scaleX,
                Height = pixelBounds.Height / _scaleY,
                RadiusX = radius,
                RadiusY = radius
            };
            Canvas.SetLeft(r, pixelBounds.X / _scaleX);
            Canvas.SetTop(r,  pixelBounds.Y / _scaleY);

            if (matchIndex.HasValue)
            {
                r.Cursor = Cursors.Hand;
                int idx = matchIndex.Value;
                r.MouseLeftButtonDown += (s, e) =>
                {
                    CopyMatchText(idx);
                    e.Handled = true;
                };
            }

            return r;
        }

        private void UpdateMatchInfoText()
        {
            if (_matches.Count == 0)
            {
                MatchInfo.Text = _ocrCompleted ? "No matches" : "";
            }
            else
            {
                int exact = _matches.Count(m => !m.IsFuzzy);
                int fuzzy = _matches.Count(m => m.IsFuzzy);

                // e.g. "1 / 4 (3 exact, 1 fuzzy)" or "1 / 3" if no fuzzy
                string position = _currentIndex >= 0
                    ? $"{_currentIndex + 1} / {_matches.Count}"
                    : $"{_matches.Count} found";

                if (fuzzy > 0)
                    MatchInfo.Text = $"{position} ({exact} exact, {fuzzy} fuzzy)";
                else
                    MatchInfo.Text = position;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  KEYBOARD NAVIGATION
        // ════════════════════════════════════════════════════════════════

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    // Tell MainWindow to dismiss ALL overlays (hide, not close)
                    CloseAllRequested?.Invoke();
                    e.Handled = true;
                    break;

                case Key.Enter:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        NavigateMatch(-1);   // previous
                    else
                        NavigateMatch(+1);   // next
                    e.Handled = true;
                    break;

                case Key.F3:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        NavigateMatch(-1);
                    else
                        NavigateMatch(+1);
                    e.Handled = true;
                    break;

                case Key.C:
                    // Ctrl+C: copy current match text (only if nothing selected in search box)
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                        && SearchBox.SelectionLength == 0)
                    {
                        CopyCurrentMatchText();
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void NavigateMatch(int direction)
        {
            if (_matches.Count == 0) return;

            _currentIndex = (_currentIndex + direction + _matches.Count) % _matches.Count;

            // Redraw: all highlights + current highlight on top
            DrawAllHighlights();
            DrawCurrentMatchHighlight();
            UpdateMatchInfoText();
        }

        private void CopyMatchText(int index)
        {
            if (index < 0 || index >= _matches.Count) return;

            var text = _matches[index].Text;
            if (string.IsNullOrEmpty(text)) return;

            TrySetClipboard(text);
            ShowCopiedFeedback($"Copied: \"{text}\"");
        }

        private void CopyCurrentMatchText()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;

            var text = _matches[_currentIndex].Text;
            if (string.IsNullOrEmpty(text)) return;

            TrySetClipboard(text);
            ShowCopiedFeedback($"Copied: \"{text}\"");
        }

        /// <summary>
        /// Show temporary feedback in the MatchInfo area. Uses a single timer
        /// so rapid copies don't corrupt the text.
        /// </summary>
        private void ShowCopiedFeedback(string message)
        {
            // Stop any existing feedback timer and save the real text (only if not mid-feedback)
            if (_feedbackTimer == null || !_feedbackTimer.IsEnabled)
                _savedMatchInfoText = MatchInfo.Text;
            else
                _feedbackTimer.Stop();

            MatchInfo.Text = message;

            _feedbackTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _feedbackTimer.Tick += (s, e) =>
            {
                _feedbackTimer.Stop();
                MatchInfo.Text = _savedMatchInfoText;
            };
            _feedbackTimer.Start();
        }

        // ════════════════════════════════════════════════════════════════
        //  DRAG-TO-SELECT
        // ════════════════════════════════════════════════════════════════

        private void SelectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var clickPosDip = e.GetPosition(SelectionCanvas);

            // Check if the click is on an existing match highlight — forward to click-to-copy
            if (_matches.Count > 0)
            {
                for (int i = 0; i < _matches.Count; i++)
                {
                    // Convert match bounds (physical pixels) to DIPs for hit-testing
                    var b = _matches[i].Bounds;
                    var dipRect = new Rect(
                        b.X / _scaleX - 4, b.Y / _scaleY - 4,
                        b.Width / _scaleX + 8, b.Height / _scaleY + 8);

                    if (dipRect.Contains(clickPosDip))
                    {
                        CopyMatchText(i);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Start a new drag selection
            _isDragging = true;
            _dragStartDip = clickPosDip;
            _selectedWords.Clear();
            SelectionCanvas.Children.Clear();

            // Create the dashed lasso rectangle
            _lassoRect = new Rectangle
            {
                Stroke = LassoStroke,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = LassoFill,
                Width = 0,
                Height = 0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_lassoRect, clickPosDip.X);
            Canvas.SetTop(_lassoRect, clickPosDip.Y);
            SelectionCanvas.Children.Add(_lassoRect);

            SelectionCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void SelectionCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _lassoRect == null) return;

            var currentPos = e.GetPosition(SelectionCanvas);

            // Update lasso rect position and size
            double x = Math.Min(_dragStartDip.X, currentPos.X);
            double y = Math.Min(_dragStartDip.Y, currentPos.Y);
            double w = Math.Abs(currentPos.X - _dragStartDip.X);
            double h = Math.Abs(currentPos.Y - _dragStartDip.Y);

            Canvas.SetLeft(_lassoRect, x);
            Canvas.SetTop(_lassoRect, y);
            _lassoRect.Width = w;
            _lassoRect.Height = h;

            // Convert DIP lasso rect → physical pixels for OCR bounds comparison
            var physicalRect = new Rect(
                x * _scaleX, y * _scaleY,
                w * _scaleX, h * _scaleY);

            UpdateSelectionHighlights(physicalRect);
        }

        private void SelectionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            SelectionCanvas.ReleaseMouseCapture();

            // Remove the lasso rect (keep word highlights)
            if (_lassoRect != null)
            {
                SelectionCanvas.Children.Remove(_lassoRect);
                _lassoRect = null;
            }

            if (_selectedWords.Count > 0)
            {
                // Sort words in reading order and build text
                var text = BuildSelectedText(_selectedWords);
                ShowSelectionCopiedFeedback(text);

                // Copy to clipboard on a background STA thread — zero UI blocking
                CopyToClipboardBackground(text);
            }

            // Re-focus the search box so keyboard shortcuts keep working
            SearchBox.Focus();
            e.Handled = true;
        }

        /// <summary>
        /// Find all OCR words that intersect the lasso rect and draw green highlights.
        /// </summary>
        private void UpdateSelectionHighlights(Rect physicalLassoRect)
        {
            _selectedWords.Clear();

            if (_ocrLines == null) return;

            // Collect matching words
            int matchIndex = 0;
            foreach (var line in _ocrLines)
            {
                foreach (var word in line.Words)
                {
                    if (word.Bounds.IntersectsWith(physicalLassoRect))
                    {
                        _selectedWords.Add(word);

                        // Reuse existing rectangle or create a new one
                        Rectangle r;
                        if (matchIndex < _selectionHighlights.Count)
                        {
                            r = _selectionHighlights[matchIndex];
                            r.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            r = new Rectangle
                            {
                                Stroke = SelectionStroke,
                                StrokeThickness = 1.5,
                                Fill = SelectionFill,
                                RadiusX = 3,
                                RadiusY = 3,
                                IsHitTestVisible = false
                            };
                            _selectionHighlights.Add(r);
                            SelectionCanvas.Children.Add(r);
                        }

                        r.Width = word.Bounds.Width / _scaleX + 4;
                        r.Height = word.Bounds.Height / _scaleY + 4;
                        Canvas.SetLeft(r, word.Bounds.X / _scaleX - 2);
                        Canvas.SetTop(r, word.Bounds.Y / _scaleY - 2);

                        matchIndex++;
                    }
                }
            }

            // Hide unused rectangles from the pool (instead of removing them)
            for (int i = matchIndex; i < _selectionHighlights.Count; i++)
                _selectionHighlights[i].Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Build readable text from selected words — grouped into lines by Y proximity,
        /// sorted left-to-right within each line.
        /// </summary>
        private static string BuildSelectedText(List<OcrWordInfo> words)
        {
            if (words.Count == 0) return "";

            // Sort by Y first, then X
            var sorted = words.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();

            var lines = new List<List<OcrWordInfo>>();
            var currentLine = new List<OcrWordInfo> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                // If this word's Y is close to the previous word's Y, same line
                double threshold = sorted[i].Bounds.Height * 0.5;
                if (Math.Abs(sorted[i].Bounds.Y - currentLine[0].Bounds.Y) < threshold)
                {
                    currentLine.Add(sorted[i]);
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = new List<OcrWordInfo> { sorted[i] };
                }
            }
            lines.Add(currentLine);

            // Sort each line left-to-right, join words with spaces, join lines with newlines
            return string.Join("\n",
                lines.Select(line =>
                    string.Join(" ", line.OrderBy(w => w.Bounds.X).Select(w => w.Text))));
        }

        /// <summary>
        /// Show "Copied!" feedback for drag-selected text.
        /// </summary>
        private void ShowSelectionCopiedFeedback(string text)
        {
            var truncated = text.Length > 60 ? text.Substring(0, 60) + "..." : text;
            truncated = truncated.Replace("\n", " ").Replace("\r", "");
            ShowCopiedFeedback($"Copied: \"{truncated}\"");
        }

        // ════════════════════════════════════════════════════════════════
        //  CLIPBOARD HELPER
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Try to set clipboard text with retries — the clipboard can be locked
        /// by other apps, causing CLIPBRD_E_CANT_OPEN (COMException).
        /// </summary>
        private static void TrySetClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Fully non-blocking clipboard copy — runs all attempts on a background thread
        /// using Win32 directly, so the UI thread never blocks.
        /// </summary>
        private void CopyToClipboardBackground(string text)
        {
            // Clipboard.SetText must run on an STA thread. Spin up a short-lived
            // STA thread so the UI thread is completely free.
            var thread = new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        return;
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // ════════════════════════════════════════════════════════════════
        //  CLEANUP
        // ════════════════════════════════════════════════════════════════

        protected override void OnClosed(EventArgs e)
        {
            _capturedBitmap?.Dispose();
            base.OnClosed(e);
        }
    }
}
