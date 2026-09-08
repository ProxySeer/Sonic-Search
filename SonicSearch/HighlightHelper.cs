using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SonicSearch
{
    public static class HighlightHelper
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(HighlightHelper),
                new PropertyMetadata(string.Empty, OnHighlightChanged));

        public static readonly DependencyProperty SearchTextProperty =
            DependencyProperty.RegisterAttached(
                "SearchText",
                typeof(string),
                typeof(HighlightHelper),
                new PropertyMetadata(string.Empty, OnHighlightChanged));

        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

        public static string GetSearchText(DependencyObject obj) => (string)obj.GetValue(SearchTextProperty);
        public static void SetSearchText(DependencyObject obj, string value) => obj.SetValue(SearchTextProperty, value);

        private static readonly SolidColorBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)); // Vibrant Yellow (#FACC15)
        private static readonly SolidColorBrush HighlightBgBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFA, 0xCC, 0x15)); // Subtle amber glow background

        static HighlightHelper()
        {
            HighlightBrush.Freeze();
            HighlightBgBrush.Freeze();
        }

        private static void OnHighlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock textBlock)) return;

            string fullText = GetText(textBlock) ?? string.Empty;
            string searchRaw = GetSearchText(textBlock) ?? string.Empty;

            textBlock.Inlines.Clear();

            if (string.IsNullOrEmpty(fullText)) return;

            var terms = ExtractSearchTerms(searchRaw);

            if (terms.Count == 0)
            {
                textBlock.Inlines.Add(new Run(fullText));
                return;
            }

            var ranges = new List<Tuple<int, int>>();
            foreach (var term in terms)
            {
                int index = 0;
                while ((index = fullText.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    ranges.Add(Tuple.Create(index, term.Length));
                    index += Math.Max(1, term.Length);
                }
            }

            if (ranges.Count == 0)
            {
                textBlock.Inlines.Add(new Run(fullText));
                return;
            }

            ranges.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : b.Item2.CompareTo(a.Item2));
            var merged = new List<Tuple<int, int>>();
            int curStart = ranges[0].Item1;
            int curEnd = curStart + ranges[0].Item2;

            for (int i = 1; i < ranges.Count; i++)
            {
                int nextStart = ranges[i].Item1;
                int nextEnd = nextStart + ranges[i].Item2;
                if (nextStart <= curEnd)
                {
                    curEnd = Math.Max(curEnd, nextEnd);
                }
                else
                {
                    merged.Add(Tuple.Create(curStart, curEnd - curStart));
                    curStart = nextStart;
                    curEnd = nextEnd;
                }
            }
            merged.Add(Tuple.Create(curStart, curEnd - curStart));

            int currentPos = 0;
            foreach (var range in merged)
            {
                if (range.Item1 > currentPos)
                {
                    textBlock.Inlines.Add(new Run(fullText.Substring(currentPos, range.Item1 - currentPos)));
                }

                var matchRun = new Run(fullText.Substring(range.Item1, range.Item2))
                {
                    Foreground = HighlightBrush,
                    Background = HighlightBgBrush,
                    FontWeight = FontWeights.Bold
                };
                textBlock.Inlines.Add(matchRun);

                currentPos = range.Item1 + range.Item2;
            }

            if (currentPos < fullText.Length)
            {
                textBlock.Inlines.Add(new Run(fullText.Substring(currentPos)));
            }
        }

        private static List<string> ExtractSearchTerms(string query)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return terms;

            string[] tokens = query.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Equals("ext:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("type:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("kind:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("content:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("text:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("size:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("date:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("modified:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("location:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("path:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("in:", StringComparison.OrdinalIgnoreCase))
                {
                    i++; // Skip both filter name and following parameter
                    continue;
                }

                if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("type:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("kind:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("content:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("size:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("date:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("modified:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("location:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("path:", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("in:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("folder:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("folders:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("dir:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("directory:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("directories:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("file:", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("files:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                token = token.Replace("*", "").Replace("?", "").Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    terms.Add(token);
                }
            }

            return terms;
        }
    }
}
