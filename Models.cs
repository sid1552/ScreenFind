using System.Collections.Generic;
using System.Windows;

namespace ScreenFind
{
    /// <summary>
    /// Represents a single word found by OCR, with its text and screen position (in pixels).
    /// </summary>
    public class OcrWordInfo
    {
        public string Text { get; set; } = "";
        public Rect Bounds { get; set; } // in physical screen pixels
    }

    /// <summary>
    /// Represents one line of text found by OCR, containing multiple words.
    /// </summary>
    public class OcrLineInfo
    {
        public List<OcrWordInfo> Words { get; set; } = new();
        public string FullText { get; set; } = ""; // all words joined with spaces
    }

    /// <summary>
    /// A single search match — the combined bounding box of matched words.
    /// </summary>
    public class MatchResult
    {
        public Rect Bounds { get; set; } // in physical screen pixels
    }
}
