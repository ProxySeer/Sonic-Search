using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO.Filesystem.Ntfs;

namespace SonicSearch
{
    public class RegexSearcher
    {
        private readonly IDictionary<string, INode> _nodes;

        public RegexSearcher(IDictionary<string, INode> nodes)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        }

        public class SearchOptions
        {
            public string BasePath { get; set; }
            public string WildcardPattern { get; set; } = "*";
            public string FilenameContains { get; set; }
            public string FilenameStartsWith { get; set; }
            public string FilenameEndsWith { get; set; }
            public string RegexPattern { get; set; }
            public ulong? MinSizeBytes { get; set; }
            public ulong? MaxSizeBytes { get; set; }

            public DateTime? MinLastModified { get; set; }        
            public DateTime? MaxLastModified { get; set; }        
            public int? ModifiedWithinLastDays { get; set; }     
        }



        public List<INode> SearchIndex(
          SearchOptions options,
          CancellationToken token,
          Func<INode, bool> extraFilter = null)
        {
            if (string.IsNullOrEmpty(options.WildcardPattern))
                throw new ArgumentException("WildcardPattern cannot be null or empty.");

            string inputPattern = options.WildcardPattern;

  

            string wildcardPattern = inputPattern switch
            {
                "*" => "*",
                _ when inputPattern.StartsWith("*") && inputPattern.EndsWith("*") => $"*{inputPattern.Trim('*')}*",
                _ when inputPattern.StartsWith("*") => $"*{inputPattern.TrimStart('*')}",
                _ when inputPattern.EndsWith("*") => $"{inputPattern.TrimEnd('*')}*",
                _ => inputPattern
            };

            options.RegexPattern = "^" + Regex.Escape(wildcardPattern)
                                          .Replace("\\*", ".*")
                                          .Replace("\\?", ".") + "$";

            var regex = new Regex(options.RegexPattern, RegexOptions.IgnoreCase);
            string basePath = options.BasePath.TrimEnd('\\') + "\\";

            var result = _nodes.Values
                .AsParallel()
                .WithDegreeOfParallelism(Environment.ProcessorCount)
                .Where(node =>
                {
                    if (token.IsCancellationRequested)
                        return false;
                    if (node == null || string.IsNullOrEmpty(node.FullName))
                        return false;
                    if (!node.FullName.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        return false;

                    string fileName = Path.GetFileName(node.FullName);

                    if (extraFilter != null && !extraFilter(node))
                        return false;

                    return regex.IsMatch(fileName);
                })
                .ToList();

            return result;
        }




    }
}
