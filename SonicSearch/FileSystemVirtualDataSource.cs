using BrightIdeasSoftware;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SonicSearch
{
    public class FileSystemVirtualDataSource : IVirtualListDataSource, IFilterableDataSource
    {
        private List<FileItem> allItems;
        private List<FileItem> filteredItems;

        private IModelFilter modelFilter;
        private IListFilter listFilter;

        public IReadOnlyList<FileItem> AllItems => allItems;

        public FileSystemVirtualDataSource(List<FileItem> items)
        {
            allItems = items ?? new List<FileItem>();
            filteredItems = new List<FileItem>(allItems);
        }

    
        public int GetObjectCount() => filteredItems.Count;

        public object GetObjectAt(int index) => index >= 0 && index < filteredItems.Count ? filteredItems[index] : null;

        public int GetObjectIndex(object model) => filteredItems.IndexOf(model as FileItem);

        public object GetNthObject(int index) => GetObjectAt(index);
        public void ReplaceAllItems(List<FileItem> newItems)
        {
            allItems = newItems ?? new List<FileItem>();
            filteredItems = new List<FileItem>(allItems);
        }
        public int SearchText(string text, int first, int last, OLVColumn column)
        {
            if (string.IsNullOrWhiteSpace(text) || column == null)
                return -1;

            string lowerText = text.ToLowerInvariant();

            for (int i = first; i <= last; i++)
            {
                var item = GetObjectAt(i) as FileItem;
                if (item != null)
                {
                    string aspectValue = column.GetStringValue(item)?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(aspectValue) && aspectValue.Contains(lowerText))
                        return i;
                }
            }
            return -1;
        }

        public void PrepareCache(int from, int to)
        {
          
        }

        public void Sort(OLVColumn column, SortOrder order)
        {
            if (column == null || order == SortOrder.None)
                return;

            Comparison<FileItem> comparison = (x, y) =>
                string.Compare(column.GetStringValue(x), column.GetStringValue(y), StringComparison.OrdinalIgnoreCase);

            filteredItems.Sort((x, y) => order == SortOrder.Ascending ? comparison(x, y) : comparison(y, x));
        }

        public void AddObjects(ICollection modelObjects)
        {
            foreach (FileItem item in modelObjects)
                allItems.Add(item);

            ApplyFilters(modelFilter, listFilter);
        }

        public void InsertObjects(int index, ICollection modelObjects)
        {
            foreach (FileItem item in modelObjects)
                allItems.Insert(index++, item);

            ApplyFilters(modelFilter, listFilter);
        }

        public void RemoveObjects(ICollection modelObjects)
        {
            foreach (FileItem item in modelObjects)
                allItems.Remove(item);

            ApplyFilters(modelFilter, listFilter);
        }

        public void SetObjects(IEnumerable modelObjects)
        {
            filteredItems = modelObjects.Cast<FileItem>().ToList();
        }

        public void UpdateObject(int index, object modelObject)
        {
            if (index >= 0 && index < filteredItems.Count)
                filteredItems[index] = modelObject as FileItem;
        }

  
        public void ApplyFilters(IModelFilter modelFilter, IListFilter listFilter)
        {
            this.modelFilter = modelFilter;
            this.listFilter = listFilter;

            IEnumerable<FileItem> query = allItems;

            if (modelFilter != null)
                query = query.Where(x => modelFilter.Filter(x));

            if (listFilter != null)
                query = listFilter.Filter(query).Cast<FileItem>();

            filteredItems = query.ToList();
        }

        
        public void ResetFilters()
        {
            filteredItems = new List<FileItem>(allItems);
        }
    }

}
public static class AppContext
{
    public static SonicSearchOptions SearchOptions { get; set; } = new SonicSearchOptions();
}
public class FileItem
{
    public string FullName { get; set; }
    public string FileName { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteTime { get; set; }
    public int IconIndex => FileType == "url"
     ? IconHelper.GetIconIndexLarge("*.html")
     : IconHelper.GetIconIndexLarge(FullName);

    public string FileType { get; set; }
}
public class SonicSearchOptions
{
    public bool AutoFocusFolderPath { get; set; }
    public bool AutoSearchSwitchTabs { get; set; }
    public bool ClearSearchWhenFocused { get; set; }

    public bool AutoFocusUrlOnlyChromeOpera { get; set; }
    public int SearchResultForFullText { get; set; }
    public int SearchDelayMs { get; set; }
}