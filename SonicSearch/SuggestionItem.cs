using System;

namespace SonicSearch
{
    public class SuggestionItem
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string CompletionText { get; set; }
        public string IconKind { get; set; } // "Filter", "History", "Extension", "Size", "Date"
        public bool IsHistory { get; set; }
    }
}
