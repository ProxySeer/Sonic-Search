using System;
using System.Collections.Generic;

namespace SonicSearch
{
    /// <summary>
    /// A named, user-managed collection of favorite file/folder paths shown as one tab in the
    /// Favorites view. <see cref="ItemPaths"/> order is display order (drag-to-reorder writes
    /// back to this list).
    /// </summary>
    public class FavoriteGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Favorites";
        public List<string> ItemPaths { get; set; } = new List<string>();
    }
}
