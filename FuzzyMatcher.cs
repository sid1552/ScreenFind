using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ScreenFind
{
    /// <summary>
    /// Fuzzy matching for OCR text — catches near-matches that exact substring search misses
    /// (e.g. OCR reading "rn" as "m", "l" as "1", "O" as "0").
    /// </summary>
    public static class FuzzyMatcher
    {
        /// <summary>
        /// Standard Levenshtein edit distance between two strings.
        /// Returns the minimum number of single-character edits (insert, delete, substitute)
        /// needed to change <paramref name="a"/> into <paramref name="b"/>.
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            // Use a single-row DP approach for memory efficiency
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
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),  // insert, delete
                        prev[j - 1] + cost);                       // substitute
                }
                // Swap rows
                (prev, curr) = (curr, prev);
            }

            return prev[b.Length];
        }

        /// <summary>
        /// Returns true if the candidate word is a fuzzy match for the query word.
        /// Threshold: max(1, wordLength / 4) — allows ~1 typo per 4 characters.
        /// </summary>
        public static bool IsFuzzyMatch(string query, string candidate)
        {
            // Quick length check — if lengths differ too much, can't be a match
            int maxLen = Math.Max(query.Length, candidate.Length);
            int threshold = Math.Max(1, maxLen / 4);

            if (Math.Abs(query.Length - candidate.Length) > threshold)
                return false;

            int distance = LevenshteinDistance(query, candidate);
            // Must be > 0 (exact matches are handled by the first pass) and within threshold
            return distance > 0 && distance <= threshold;
        }

        /// <summary>
        /// Scan OCR lines for fuzzy word-sequence matches.
        /// Splits the query into words, then for each line checks consecutive OCR word
        /// sequences of the same length. Skips words already covered by exact matches.
        /// </summary>
        /// <param name="lines">OCR results</param>
        /// <param name="query">User's search query</param>
        /// <param name="exactMatchedWords">Set of (lineIndex, wordIndex) pairs already matched exactly</param>
        /// <returns>List of fuzzy MatchResults</returns>
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

                // Slide a window of queryWords.Length across the line's words
                for (int startWord = 0; startWord <= line.Words.Count - queryWords.Length; startWord++)
                {
                    // Check if any word in this window was already exact-matched
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

                    // Check if every query word fuzzy-matches the corresponding OCR word
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
                        // Combine bounding boxes of matched words
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
