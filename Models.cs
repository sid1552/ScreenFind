using System.Collections.Generic;
using System.Windows;

namespace ScreenFind
{
    public class OcrWordInfo
    {
        public string Text { get; set; } = "";
        public Rect Bounds { get; set; }
    }

    public class OcrLineInfo
    {
        public List<OcrWordInfo> Words { get; set; } = new();
        public string FullText { get; set; } = "";
    }

    public class MatchResult
    {
        public Rect Bounds { get; set; }
        public bool IsFuzzy { get; set; }
        public string Text { get; set; } = "";
    }
}
