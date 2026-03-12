using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ScreenFind
{
    /// <summary>
    /// Catches near-matches that exact search misses (e.g. OCR reading "rn" as "m").
    /// </summary>
    public static class FuzzyMatcher
    {
        public static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++)
                prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }

            return prev[b.Length];
        }

        /// <summary>
        /// Threshold: max(1, wordLength / 4) -- allows ~1 typo per 4 characters.
        /// </summary>
        public static bool IsFuzzyMatch(string query, string candidate)
        {
            int maxLen = Math.Max(query.Length, candidate.Length);
            int threshold = Math.Max(1, maxLen / 4);

            if (Math.Abs(query.Length - candidate.Length) > threshold)
                return false;

            int distance = LevenshteinDistance(query, candidate);
            // > 0 because exact matches are handled separately
            return distance > 0 && distance <= threshold;
        }

        public static List<MatchResult> FindFuzzyMatches(
            List<OcrLineInfo> lines,
            string query,
            HashSet<(int LineIdx, int WordIdx)> exactMatchedWords)
        {
            var results = new List<MatchResult>();
            var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (queryWords.Length == 0)
                return results;

            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                var line = lines[lineIdx];
                if (line.Words.Count < queryWords.Length)
                    continue;

                for (int startWord = 0; startWord <= line.Words.Count - queryWords.Length; startWord++)
                {
                    bool overlapsExact = false;
                    for (int k = 0; k < queryWords.Length; k++)
                    {
                        if (exactMatchedWords.Contains((lineIdx, startWord + k)))
                        {
                            overlapsExact = true;
                            break;
                        }
                    }
                    if (overlapsExact) continue;

                    bool allMatch = true;
                    for (int k = 0; k < queryWords.Length; k++)
                    {
                        var ocrWord = line.Words[startWord + k].Text;
                        if (!IsFuzzyMatch(queryWords[k], ocrWord))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        var hitWords = Enumerable.Range(startWord, queryWords.Length)
                            .Select(i => line.Words[i])
                            .ToList();

                        double minX = hitWords.Min(w => w.Bounds.X);
                        double minY = hitWords.Min(w => w.Bounds.Y);
                        double maxX = hitWords.Max(w => w.Bounds.Right);
                        double maxY = hitWords.Max(w => w.Bounds.Bottom);

                        results.Add(new MatchResult
                        {
                            Bounds = new Rect(minX, minY, maxX - minX, maxY - minY),
                            IsFuzzy = true,
                            Text = string.Join(" ", hitWords.Select(w => w.Text))
                        });
                    }
                }
            }

            return results;
        }
    }
}
