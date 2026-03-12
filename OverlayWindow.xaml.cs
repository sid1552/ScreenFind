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
using SysDrawing = System.Drawing;
using SysForms = System.Windows.Forms;

namespace ScreenFind
{
    public partial class OverlayWindow : Window
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private SysDrawing.Bitmap _capturedBitmap;
        private readonly bool _enhanceOcr;
        private readonly bool _dragToSelect;
        private List<OcrLineInfo>? _ocrLines;
        private bool _ocrCompleted;

        private readonly SysForms.Screen _targetScreen;
        private readonly bool _isPrimary;
        private string _pendingQuery = "";

        public event Action<string>? SearchChanged;
        public event Action? CloseAllRequested;

        private List<MatchResult> _matches = new();
        private int _currentIndex = -1;

        // Physical pixels to WPF DIPs
        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        private bool _isDragging;
        private Point _dragStartDip;
        private Rectangle? _lassoRect;
        private List<OcrWordInfo> _selectedWords = new();
        private List<Rectangle> _selectionHighlights = new();

        private System.Windows.Threading.DispatcherTimer? _feedbackTimer;
        private string _savedMatchInfoText = "";

        public OverlayWindow(SysForms.Screen targetScreen,
            bool enhanceOcr = false, bool dragToSelect = true, bool isPrimary = true)
        {
            InitializeComponent();
            _targetScreen = targetScreen;
            _enhanceOcr = enhanceOcr;
            _dragToSelect = dragToSelect;
            _isPrimary = isPrimary;
            Opacity = 0;

            SelectionCanvas.MouseLeftButtonDown += SelectionCanvas_MouseLeftButtonDown;
            SelectionCanvas.MouseMove += SelectionCanvas_MouseMove;
            SelectionCanvas.MouseLeftButtonUp += SelectionCanvas_MouseLeftButtonUp;

            if (!_isPrimary)
            {
                SearchBarBorder.Visibility = Visibility.Collapsed;
                LoadingBadge.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Show+Hide at Opacity=0 forces HWND creation and WPF layout,
        /// so the first hotkey press is near-instant.
        /// </summary>
        public void Prewarm()
        {
            Show();
            Hide();
        }

        public async void Activate(SysDrawing.Bitmap screenshot)
        {
            _capturedBitmap?.Dispose();
            _capturedBitmap = screenshot;

            ResetState();

            var hwnd = new WindowInteropHelper(this).Handle;
            var bounds = _targetScreen.Bounds;
            MoveWindow(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
            uint dpi = GetDpiForWindow(hwnd);
            _scaleX = dpi / 96.0;
            _scaleY = dpi / 96.0;

            SetScreenshotBackground();

            Opacity = 1;
            Show();

            if (_isPrimary)
            {
                // Deferred so it runs after the window is fully rendered
                Dispatcher.BeginInvoke(() =>
                {
                    Activate();
                    Keyboard.Focus(SearchBox);
                });
            }

            await RunOcrAsync();
        }

        public void Dismiss()
        {
            Opacity = 0;
            Hide();
        }

        private void ResetState()
        {
            SearchBox.Text = "";
            _ocrLines = null;
            _ocrCompleted = false;
            _matches.Clear();
            _currentIndex = -1;
            _pendingQuery = "";
            MatchInfo.Text = "";

            HighlightCanvas.Children.Clear();

            _isDragging = false;
            _selectedWords.Clear();
            SelectionCanvas.Children.Clear();
            _selectionHighlights.Clear();
            _lassoRect = null;
            SelectionCanvas.IsHitTestVisible = false;
            SelectionCanvas.Cursor = Cursors.Arrow;

            if (_isPrimary)
                LoadingBadge.Visibility = Visibility.Visible;

            _feedbackTimer?.Stop();
            _savedMatchInfoText = "";
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            var bounds = _targetScreen.Bounds;
            MoveWindow(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);

            uint dpi = GetDpiForWindow(hwnd);
            _scaleX = dpi / 96.0;
            _scaleY = dpi / 96.0;
        }

        /// <summary>
        /// BitmapSource DPI is set to screen's actual DPI so
        /// 1 image pixel = 1 physical screen pixel.
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
                    96 * _scaleX,
                    96 * _scaleY,
                    PixelFormats.Bgra32,
                    null,
                    bitmapData.Scan0,
                    bitmapData.Stride * bitmapData.Height,
                    bitmapData.Stride);

                bitmapSource.Freeze();
                ScreenshotImage.Source = bitmapSource;
            }
            finally
            {
                _capturedBitmap.UnlockBits(bitmapData);
            }
        }

        private async Task RunOcrAsync()
        {
            try
            {
                double preprocessScale = 1.0;

                // WinRT StorageFile API requires a temp file
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
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingBadge.Visibility = Visibility.Collapsed;
                    MatchInfo.Text = $"OCR error: {ex.Message}";
                });
            }
        }

        private async Task RunWindowsOcrAsync(string tempPath, double preprocessScale)
        {
            var storageFile = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(tempPath);

            using var stream = await storageFile
                .OpenAsync(Windows.Storage.FileAccessMode.Read);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder
                .CreateAsync(stream);

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var engine = Windows.Media.Ocr.OcrEngine
                .TryCreateFromUserProfileLanguages();

            if (engine == null)
            {
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

            var ocrResult = await engine.RecognizeAsync(softwareBitmap);

            _ocrLines = new List<OcrLineInfo>();
            foreach (var line in ocrResult.Lines)
            {
                var lineInfo = new OcrLineInfo
                {
                    // Divide by preprocessScale to map upscaled coords back to screen pixels
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

        private void OnOcrCompleted()
        {
            _ocrCompleted = true;

            Dispatcher.Invoke(() =>
            {
                if (_isPrimary)
                    LoadingBadge.Visibility = Visibility.Collapsed;

                if (_dragToSelect)
                {
                    SelectionCanvas.IsHitTestVisible = true;
                    SelectionCanvas.Cursor = Cursors.Cross;
                }

                // User may have typed while OCR was running
                var query = _isPrimary ? SearchBox.Text : _pendingQuery;
                if (!string.IsNullOrEmpty(query))
                    UpdateHighlights(query);
            });
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SelectionCanvas.Children.Clear();
            _selectedWords.Clear();

            if (_ocrCompleted)
                UpdateHighlights(SearchBox.Text);

            SearchChanged?.Invoke(SearchBox.Text);
        }

        public void ApplySearch(string query)
        {
            _pendingQuery = query;
            if (!_ocrCompleted) return;

            SelectionCanvas.Children.Clear();
            _selectedWords.Clear();

            UpdateHighlights(query);
        }

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

            var exactMatchedWords = new HashSet<(int LineIdx, int WordIdx)>();

            for (int lineIdx = 0; lineIdx < _ocrLines.Count; lineIdx++)
            {
                var line = _ocrLines[lineIdx];

                var wordSpans = new List<(int Start, int End, int WordIdx)>();
                int charPos = 0;
                for (int i = 0; i < line.Words.Count; i++)
                {
                    int start = charPos;
                    int end = charPos + line.Words[i].Text.Length - 1;
                    wordSpans.Add((start, end, i));
                    charPos = end + 2; // +1 for last char, +1 for the space
                }

                int searchFrom = 0;
                while (searchFrom <= line.FullText.Length - query.Length)
                {
                    int idx = line.FullText.IndexOf(
                        query, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    int matchEnd = idx + query.Length - 1;

                    var hitSpans = wordSpans
                        .Where(s => s.End >= idx && s.Start <= matchEnd)
                        .ToList();

                    var hitWords = hitSpans.Select(s => line.Words[s.WordIdx]).ToList();

                    if (hitWords.Count > 0)
                    {
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

            if (query.Length >= 3)
            {
                var fuzzyMatches = FuzzyMatcher.FindFuzzyMatches(
                    _ocrLines, query, exactMatchedWords);
                _matches.AddRange(fuzzyMatches);
            }

            DrawAllHighlights();

            if (_matches.Count > 0)
            {
                _currentIndex = 0;
                DrawCurrentMatchHighlight();
            }

            UpdateMatchInfoText();
        }

        private static readonly SolidColorBrush MatchStroke =
            new(Color.FromArgb(255, 250, 200, 50));
        private static readonly SolidColorBrush MatchFill =
            new(Color.FromArgb(55, 255, 255, 0));

        private static readonly SolidColorBrush CurrentStroke =
            new(Color.FromArgb(255, 255, 100, 0));
        private static readonly SolidColorBrush CurrentFill =
            new(Color.FromArgb(110, 255, 150, 0));

        private static readonly SolidColorBrush FuzzyStroke =
            new(Color.FromArgb(255, 80, 160, 255));
        private static readonly SolidColorBrush FuzzyFill =
            new(Color.FromArgb(55, 80, 160, 255));

        private static readonly SolidColorBrush CurrentFuzzyStroke =
            new(Color.FromArgb(255, 30, 120, 255));
        private static readonly SolidColorBrush CurrentFuzzyFill =
            new(Color.FromArgb(110, 30, 120, 255));

        private static readonly SolidColorBrush SelectionStroke =
            new(Color.FromArgb(255, 50, 205, 50));
        private static readonly SolidColorBrush SelectionFill =
            new(Color.FromArgb(55, 50, 205, 50));

        private static readonly SolidColorBrush LassoStroke =
            new(Color.FromArgb(180, 255, 255, 255));
        private static readonly SolidColorBrush LassoFill =
            new(Color.FromArgb(20, 255, 255, 255));

        private void DrawAllHighlights()
        {
            HighlightCanvas.Children.Clear();

            for (int i = 0; i < _matches.Count; i++)
            {
                var m = _matches[i];
                var padded = new Rect(
                    m.Bounds.X - 4, m.Bounds.Y - 4,
                    m.Bounds.Width + 8, m.Bounds.Height + 8);
                var stroke = m.IsFuzzy ? FuzzyStroke : MatchStroke;
                var fill = m.IsFuzzy ? FuzzyFill : MatchFill;
                var rect = MakeRect(padded, stroke, 2, fill, 5, i);
                HighlightCanvas.Children.Add(rect);
            }
        }

        private void DrawCurrentMatchHighlight()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;

            var m = _matches[_currentIndex];
            var inflated = new Rect(
                m.Bounds.X - 6, m.Bounds.Y - 6,
                m.Bounds.Width + 12, m.Bounds.Height + 12);

            var stroke = m.IsFuzzy ? CurrentFuzzyStroke : CurrentStroke;
            var fill = m.IsFuzzy ? CurrentFuzzyFill : CurrentFill;
            var rect = MakeRect(inflated, stroke, 3, fill, 7, _currentIndex);
            HighlightCanvas.Children.Add(rect);
        }

        /// <summary>
        /// Coordinates are converted from physical pixels to DIPs.
        /// When matchIndex is provided, the rect becomes clickable.
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

                string position = _currentIndex >= 0
                    ? $"{_currentIndex + 1} / {_matches.Count}"
                    : $"{_matches.Count} found";

                if (fuzzy > 0)
                    MatchInfo.Text = $"{position} ({exact} exact, {fuzzy} fuzzy)";
                else
                    MatchInfo.Text = position;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
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

        private void ShowCopiedFeedback(string message)
        {
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

        private void SelectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var clickPosDip = e.GetPosition(SelectionCanvas);

            if (_matches.Count > 0)
            {
                for (int i = 0; i < _matches.Count; i++)
                {
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

            _isDragging = true;
            _dragStartDip = clickPosDip;
            _selectedWords.Clear();
            SelectionCanvas.Children.Clear();
            _selectionHighlights.Clear();

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

            double x = Math.Min(_dragStartDip.X, currentPos.X);
            double y = Math.Min(_dragStartDip.Y, currentPos.Y);
            double w = Math.Abs(currentPos.X - _dragStartDip.X);
            double h = Math.Abs(currentPos.Y - _dragStartDip.Y);

            Canvas.SetLeft(_lassoRect, x);
            Canvas.SetTop(_lassoRect, y);
            _lassoRect.Width = w;
            _lassoRect.Height = h;

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

            if (_lassoRect != null)
            {
                SelectionCanvas.Children.Remove(_lassoRect);
                _lassoRect = null;
            }

            if (_selectedWords.Count > 0)
            {
                var text = BuildSelectedText(_selectedWords);
                ShowSelectionCopiedFeedback(text);

                CopyToClipboardBackground(text);
            }

            SearchBox.Focus();
            e.Handled = true;
        }

        private void UpdateSelectionHighlights(Rect physicalLassoRect)
        {
            _selectedWords.Clear();

            if (_ocrLines == null) return;

            int matchIndex = 0;
            foreach (var line in _ocrLines)
            {
                foreach (var word in line.Words)
                {
                    if (word.Bounds.IntersectsWith(physicalLassoRect))
                    {
                        _selectedWords.Add(word);

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

            for (int i = matchIndex; i < _selectionHighlights.Count; i++)
                _selectionHighlights[i].Visibility = Visibility.Collapsed;
        }

        private static string BuildSelectedText(List<OcrWordInfo> words)
        {
            if (words.Count == 0) return "";

            var sorted = words.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();

            var lines = new List<List<OcrWordInfo>>();
            var currentLine = new List<OcrWordInfo> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
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

            return string.Join("\n",
                lines.Select(line =>
                    string.Join(" ", line.OrderBy(w => w.Bounds.X).Select(w => w.Text))));
        }

        private void ShowSelectionCopiedFeedback(string text)
        {
            var truncated = text.Length > 60 ? text.Substring(0, 60) + "..." : text;
            truncated = truncated.Replace("\n", " ").Replace("\r", "");
            ShowCopiedFeedback($"Copied: \"{truncated}\"");
        }

        /// <summary>
        /// Retries because the clipboard can be locked by other apps (COMException).
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
        /// Clipboard.SetText must run on an STA thread, so we spin up
        /// a short-lived one to avoid blocking the UI thread.
        /// </summary>
        private void CopyToClipboardBackground(string text)
        {
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

        protected override void OnClosed(EventArgs e)
        {
            _capturedBitmap?.Dispose();
            base.OnClosed(e);
        }
    }
}
