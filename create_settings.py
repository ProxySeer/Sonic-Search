import os
import json

src_dir = 'c:/Users/Fuu/Downloads/Sonic-Search-main/SonicSearch'

settings_cs = '''using System;
using System.IO;
using System.Text.Json;

namespace SonicSearch
{
    public class AppSettings
    {
        public int MaxResults { get; set; } = 100;
        public string ExcludedExtensions { get; set; } = ".dll, .sys, .cache, .tmp";
        public string PrioritizedExtensions { get; set; } = ".exe, .sln, .md, .txt";
        
        public static AppSettings Instance { get; set; } = new AppSettings();
        
        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        
        public static void Load()
        {
            try {
                if (File.Exists(ConfigPath)) {
                    string json = File.ReadAllText(ConfigPath);
                    Instance = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            } catch { }
        }
        
        public static void Save()
        {
            try {
                string json = JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            } catch { }
        }
    }
}'''
with open(os.path.join(src_dir, 'AppSettings.cs'), 'w', encoding='utf-8') as f:
    f.write(settings_cs)

options_xaml = '''<Window x:Class="SonicSearch.OptionsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Settings" Height="350" Width="450"
        WindowStartupLocation="CenterOwner" Background="#1E1E1E" Foreground="White"
        WindowStyle="ToolWindow">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <TextBlock Text="Max Search Results (Top N):" Margin="0,0,0,5"/>
        <TextBox x:Name="txtMaxResults" Grid.Row="1" Background="#2D2D30" Foreground="White" Padding="5" Margin="0,0,0,15" BorderThickness="1" BorderBrush="#555"/>
        
        <TextBlock Grid.Row="2" Text="Prioritized Extensions (comma separated, e.g. .exe, .sln):" Margin="0,0,0,5"/>
        <TextBox x:Name="txtPrioritize" Grid.Row="3" Background="#2D2D30" Foreground="White" Padding="5" Margin="0,0,0,15" BorderThickness="1" BorderBrush="#555"/>
        
        <TextBlock Grid.Row="4" Text="Excluded Extensions (comma separated, e.g. .dll, .sys):" Margin="0,0,0,5"/>
        <TextBox x:Name="txtExclude" Grid.Row="5" Background="#2D2D30" Foreground="White" Padding="5" Margin="0,0,0,15" BorderThickness="1" BorderBrush="#555"/>
        
        <StackPanel Grid.Row="8" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Cancel" Click="BtnCancel_Click" Width="80" Padding="5" Background="#3F3F46" Foreground="White" BorderThickness="0" Margin="0,0,10,0"/>
            <Button Content="Save" Click="BtnSave_Click" Width="80" Padding="5" Background="#9b51e0" Foreground="White" BorderThickness="0"/>
        </StackPanel>
    </Grid>
</Window>'''
with open(os.path.join(src_dir, 'OptionsWindow.xaml'), 'w', encoding='utf-8') as f:
    f.write(options_xaml)

options_cs = '''using System.Windows;

namespace SonicSearch
{
    public partial class OptionsWindow : Window
    {
        public OptionsWindow()
        {
            InitializeComponent();
            txtMaxResults.Text = AppSettings.Instance.MaxResults.ToString();
            txtPrioritize.Text = AppSettings.Instance.PrioritizedExtensions;
            txtExclude.Text = AppSettings.Instance.ExcludedExtensions;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtMaxResults.Text, out int max)) AppSettings.Instance.MaxResults = max;
            AppSettings.Instance.PrioritizedExtensions = txtPrioritize.Text;
            AppSettings.Instance.ExcludedExtensions = txtExclude.Text;
            AppSettings.Save();
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}'''
with open(os.path.join(src_dir, 'OptionsWindow.xaml.cs'), 'w', encoding='utf-8') as f:
    f.write(options_cs)

print("Settings files created.")
