import os
import shutil

src_dir = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch'

# 1. Overwrite SonicSearch.csproj
csproj_content = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <LangVersion>9.0</LangVersion>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>sonic-icon.ico</ApplicationIcon>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\NtfsReader\\NtfsReader.csproj" />
  </ItemGroup>
</Project>'''

with open(os.path.join(src_dir, 'SonicSearch.csproj'), 'w', encoding='utf-8') as f:
    f.write(csproj_content)

# 2. Create App.xaml
app_xaml = '''<Application x:Class="SonicSearch.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>'''

with open(os.path.join(src_dir, 'App.xaml'), 'w', encoding='utf-8') as f:
    f.write(app_xaml)

# 3. Create App.xaml.cs
app_xaml_cs = '''using System.Windows;

namespace SonicSearch
{
    public partial class App : Application
    {
    }
}'''

with open(os.path.join(src_dir, 'App.xaml.cs'), 'w', encoding='utf-8') as f:
    f.write(app_xaml_cs)

# 4. Create MainWindow.xaml
main_xaml = '''<Window x:Class="SonicSearch.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="SonicSearch" Height="600" Width="1000"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        WindowStartupLocation="CenterScreen" MouseLeftButtonDown="Window_MouseLeftButtonDown">
    
    <Border Background="#1E1E1E" CornerRadius="12" Margin="30">
        <Border.Effect>
            <DropShadowEffect Color="#9b51e0" BlurRadius="30" ShadowDepth="0" Opacity="0.8"/>
        </Border.Effect>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            
            <TextBox x:Name="txtSearch" Grid.Row="0" Margin="30,30,30,10" 
                     Background="#2D2D30" Foreground="White" BorderThickness="0"
                     FontSize="28" Padding="15,10" TextChanged="txtSearch_TextChanged">
                <TextBox.Resources>
                    <Style TargetType="Border">
                        <Setter Property="CornerRadius" Value="8"/>
                    </Style>
                </TextBox.Resources>
            </TextBox>
            
            <TextBlock x:Name="lblStatus" Grid.Row="1" Margin="35,0,30,15" Foreground="#AAAAAA" FontSize="14" Text="Ready"/>
            
            <Grid Grid.Row="2" Margin="30,0,30,30">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="280"/>
                </Grid.ColumnDefinitions>
                
                <ListView x:Name="listView" Grid.Column="0" 
                          Background="Transparent" BorderThickness="0" Foreground="White"
                          VirtualizingStackPanel.IsVirtualizing="True" 
                          VirtualizingStackPanel.VirtualizationMode="Recycling"
                          SelectionChanged="listView_SelectionChanged"
                          ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Margin="5">
                                <TextBlock Text="{Binding FileName}" FontSize="16" FontWeight="SemiBold" Width="250" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Text="{Binding FullName}" FontSize="14" Foreground="#888888" Margin="15,2,0,0" TextTrimming="CharacterEllipsis"/>
                            </StackPanel>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                    <ListView.ItemContainerStyle>
                        <Style TargetType="ListViewItem">
                            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                            <Setter Property="Template">
                                <Setter.Value>
                                    <ControlTemplate TargetType="ListViewItem">
                                        <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Padding="8,4">
                                            <ContentPresenter/>
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsSelected" Value="True">
                                                <Setter TargetName="Bd" Property="Background" Value="#3F3F46"/>
                                            </Trigger>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter TargetName="Bd" Property="Background" Value="#2D2D30"/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </ListView.ItemContainerStyle>
                </ListView>
                
                <Border Grid.Column="1" Background="#252526" CornerRadius="10" Margin="15,0,0,0" Padding="20">
                    <StackPanel x:Name="previewPanel" Visibility="Hidden">
                        <Image x:Name="picPreviewIcon" Width="100" Height="100" Margin="0,10,0,25" HorizontalAlignment="Center"/>
                        <TextBlock x:Name="lblPreviewName" FontSize="18" FontWeight="Bold" TextWrapping="Wrap" Foreground="White" TextAlignment="Center"/>
                        <TextBlock x:Name="lblPreviewDetails" FontSize="13" Foreground="#AAAAAA" TextWrapping="Wrap" Margin="0,25,0,25" LineHeight="20"/>
                        <Button x:Name="btnOpenFile" Content="Open File" Click="btnOpenFile_Click" 
                                Background="#3F3F46" Foreground="White" BorderThickness="0" Padding="12,8" Margin="0,0,0,12" FontSize="14">
                            <Button.Resources>
                                <Style TargetType="Border">
                                    <Setter Property="CornerRadius" Value="6"/>
                                </Style>
                            </Button.Resources>
                        </Button>
                        <Button x:Name="btnOpenFolder" Content="Show in Folder" Click="btnOpenFolder_Click" 
                                Background="#3F3F46" Foreground="White" BorderThickness="0" Padding="12,8" FontSize="14">
                            <Button.Resources>
                                <Style TargetType="Border">
                                    <Setter Property="CornerRadius" Value="6"/>
                                </Style>
                            </Button.Resources>
                        </Button>
                    </StackPanel>
                </Border>
            </Grid>
            
            <Button Content="?" Click="CloseButton_Click" HorizontalAlignment="Right" VerticalAlignment="Top" 
                    Margin="0,10,10,0" Width="30" Height="30" Background="Transparent" Foreground="#AAAAAA" BorderThickness="0" FontSize="16"/>
        </Grid>
    </Border>
</Window>'''

with open(os.path.join(src_dir, 'MainWindow.xaml'), 'w', encoding='utf-8') as f:
    f.write(main_xaml)

print("Files created.")
