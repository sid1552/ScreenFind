using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SysDrawing = System.Drawing;

namespace ScreenFind
{
    public partial class OverlayWindow : Window
    {
        // ─── OCR data ──────────────────────────────────────────────────
        private readonly SysDrawing.Bitmap _capturedBitmap;
        private readonly bool _enhanceOcr;
        private List<OcrLineInfo>? _ocrLines;
        private bool _ocrCompleted;

        // ─── Search state ──────────────────────────────────────────────
        private List<MatchResult> _matches = new();
        private int _currentIndex = -1;

        // ─── DPI scaling (physical pixels → WPF DIPs) ──────────────────
        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        // ────────────────────────────────────────────────────────────────
        public OverlayWindow(SysDrawing.Bitmap screenshot, bool enhanceOcr = false)
        {
            InitializeComponent();
            _capturedBitmap = screenshot;
            _enhanceOcr = enhanceOcr;
            Loaded += OverlayWindow_Loaded;
        }

        // ════════════════════════════════════════════════════════════════
        //  INITIALIZATION
        // ════════════════════════════════════════════════════════════════

        private async void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Get DPI scale so we can convert pixel coords → DIPs
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _scaleX = source.CompositionTarget.TransformToDevice.M11;
                _scaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            // 2. Size the window to cover the full primary screen (in DIPs)
            Left = 0;
            Top = 0;
            Width  = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // 3. Show the captured screenshot as background
            SetScreenshotBackground();

            // 4. Focus the search box immediately (user can start typing)
            SearchBox.Focus();

            // 5. Run OCR in the background
            await RunOcrAsync();
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
                // Save the capture to a temp file so the WinRT decoder can read it
                // If enhance is on, preprocess a copy (original stays untouched for display)
                var tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "screenfind_capture.bmp");

                if (_enhanceOcr)
                {
                    using var enhanced = ImagePreprocessor.Enhance(_capturedBitmap);
                    enhanced.Save(tempPath, SysDrawing.Imaging.ImageFormat.Bmp);
                }
                else
                {
                    _capturedBitmap.Save(tempPath, SysDrawing.Imaging.ImageFormat.Bmp);
                }

                try
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
                            Words = line.Words.Select(w => new OcrWordInfo
                            {
                                Text = w.Text,
                                Bounds = new Rect(
                                    w.BoundingRect.X,
                                    w.BoundingRect.Y,
                                    w.BoundingRect.Width,
                                    w.BoundingRect.Height)
                            }).ToList()
                        };
                        lineInfo.FullText = string.Join(" ", lineInfo.Words.Select(w => w.Text));
                        _ocrLines.Add(lineInfo);
                    }

                    _ocrCompleted = true;

                    Dispatcher.Invoke(() =>
                    {
                        LoadingBadge.Visibility = Visibility.Collapsed;

                        // If the user already typed something while OCR was running,
                        // apply the search now
                        if (!string.IsNullOrEmpty(SearchBox.Text))
                            UpdateHighlights(SearchBox.Text);
                    });
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingBadge.Visibility = Visibility.Collapsed;
                    MatchInfo.Text = $"OCR error";
                    System.Diagnostics.Debug.WriteLine($"OCR error: {ex}");
                });
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  SEARCH  (called every time the user types a character)
        // ════════════════════════════════════════════════════════════════

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_ocrCompleted)
                UpdateHighlights(SearchBox.Text);
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

            // ── Pass 2: Fuzzy word-level match (catches OCR misreads) ──
            var fuzzyMatches = FuzzyMatcher.FindFuzzyMatches(
                _ocrLines, query, exactMatchedWords);
            _matches.AddRange(fuzzyMatches);

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

        /// <summary>
        /// Draw yellow highlight rectangles for every match.
        /// </summary>
        private void DrawAllHighlights()
        {
            HighlightCanvas.Children.Clear();

            foreach (var m in _matches)
            {
                var padded = new Rect(
                    m.Bounds.X - 4, m.Bounds.Y - 4,
                    m.Bounds.Width + 8, m.Bounds.Height + 8);
                // Blue for fuzzy matches, yellow for exact matches
                var stroke = m.IsFuzzy ? FuzzyStroke : MatchStroke;
                var fill = m.IsFuzzy ? FuzzyFill : MatchFill;
                var rect = MakeRect(padded, stroke, 2, fill, 5);
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
            var rect = MakeRect(inflated, stroke, 3, fill, 7);
            HighlightCanvas.Children.Add(rect);
        }

        /// <summary>
        /// Helper: create a WPF Rectangle positioned on the canvas.
        /// Coordinates are converted from physical pixels → DIPs.
        /// </summary>
        private Rectangle MakeRect(
            Rect pixelBounds,
            SolidColorBrush stroke, double strokeThickness,
            SolidColorBrush fill, double radius)
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
                    Close();
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

        private void CopyCurrentMatchText()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;

            var text = _matches[_currentIndex].Text;
            if (string.IsNullOrEmpty(text)) return;

            Clipboard.SetText(text);

            // Brief "Copied!" feedback in the match info area
            var savedText = MatchInfo.Text;
            MatchInfo.Text = $"Copied: \"{text}\"";

            // Restore after 1.5 seconds
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                MatchInfo.Text = savedText;
            };
            timer.Start();
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
