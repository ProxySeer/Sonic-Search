 I built an open-source tool called **Sonic Search** – it's a super-fast file search and folder size analysis tool specifically built for NTFS file systems. It leverages the Master File Table (MFT) for raw, blazing-fast performance.


- MFT-based indexing (millions of files in seconds)
- Instant file search with:
  - Wildcard
  - Regex
  - Fuzzy/partial matching
- Folder size calculation across the entire system
- Clean modern UI with right-click file actions
- Fully open source







**1. Indexing 1.8M files in 6 seconds**  
![Performance](screenshots/performance1.png)

**2. Instant search results, even for fuzzy/wildcard queries**  
![Search](screenshots/performance2.png)

**3. Full disk folder size calculations**  
![Folder Size](screenshots/foldersize1.png)

  
- [ObjectListView](https://objectlistview.sourceforge.net/cs/index.html) (📦 Requires installing [NuGet package](https://www.nuget.org/packages/ObjectListView.Official/) before compiling)



