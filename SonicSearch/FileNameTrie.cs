using System;
using System.Collections.Generic;

namespace SonicSearch
{
    /// <summary>
    /// A simple trie over lowercased file *names* (not full paths), used to accelerate
    /// the common case of a search query that is just the start of a filename - the
    /// majority of what people type into a "fast file search" box.
    /// </summary>
    /// <remarks>
    /// This is an acceleration structure, not a replacement for the full filter
    /// pipeline in MainWindow's ApplyFilterAsync: it only narrows the candidate set
    /// for a plain filename-prefix query. Qualifiers (ext:, size:, date:, ...),
    /// substrings that aren't at the start of the name, and content search still
    /// need the full scan over the candidate set this trie returns.
    /// </remarks>
    public sealed class FileNameTrie
    {
        private sealed class TrieNode
        {
            public Dictionary<char, TrieNode> Children;
            // indices into the FileItem[] snapshot this trie was built from,
            // for every file whose name starts with the prefix ending at this node
            public List<int> Indices;
        }

        private readonly TrieNode _root = new TrieNode();

        public FileNameTrie(IList<FileItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                Insert(items[i].LowerName, i);
        }

        private void Insert(string lowerName, int itemIndex)
        {
            if (string.IsNullOrEmpty(lowerName))
                return;

            TrieNode node = _root;
            AddIndex(node, itemIndex);

            for (int i = 0; i < lowerName.Length; i++)
            {
                char c = lowerName[i];

                if (node.Children == null)
                    node.Children = new Dictionary<char, TrieNode>();

                TrieNode child;
                if (!node.Children.TryGetValue(c, out child))
                {
                    child = new TrieNode();
                    node.Children[c] = child;
                }

                node = child;
                AddIndex(node, itemIndex);
            }
        }

        private static void AddIndex(TrieNode node, int itemIndex)
        {
            if (node.Indices == null)
                node.Indices = new List<int>();
            node.Indices.Add(itemIndex);
        }

        /// <summary>
        /// Returns the indices (into the FileItem[] snapshot used to build this trie)
        /// of every file whose name starts with <paramref name="prefix"/>, or null if
        /// the prefix isn't present at all (empty result, distinct from "no filtering").
        /// </summary>
        public List<int> PrefixSearch(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return null;

            TrieNode node = _root;
            string lowerPrefix = prefix.ToLowerInvariant();

            for (int i = 0; i < lowerPrefix.Length; i++)
            {
                if (node.Children == null)
                    return new List<int>();

                TrieNode child;
                if (!node.Children.TryGetValue(lowerPrefix[i], out child))
                    return new List<int>();

                node = child;
            }

            return node.Indices ?? new List<int>();
        }
    }
}
