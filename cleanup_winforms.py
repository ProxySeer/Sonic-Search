import os

src_dir = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch'

# Delete old WinForms entry points to prevent conflicts
old_files = ['Program.cs', 'FormMain.cs', 'FormMain.Designer.cs', 'FileSystemVirtualDataSource.cs']
for file in old_files:
    try:
        os.remove(os.path.join(src_dir, file))
    except:
        pass

# Add System.Drawing reference to csproj
csproj_path = os.path.join(src_dir, 'SonicSearch.csproj')
with open(csproj_path, 'r', encoding='utf-8') as f:
    content = f.read()

if 'System.Drawing' not in content:
    content = content.replace('</Project>', '''
  <ItemGroup>
    <Reference Include="System.Drawing" />
  </ItemGroup>
</Project>''')

with open(csproj_path, 'w', encoding='utf-8') as f:
    f.write(content)
