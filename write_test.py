import os

cs = '''
using System;
using System.IO;
using System.Linq;
using NtfsReader;

public class Program
{
    public static void Main()
    {
        try {
            var drive = new DriveInfo("C");
            var ntfsReader = new NtfsReader.NtfsReader(drive, RetrieveMode.All);
            var nodes = ntfsReader.GetNodes(drive.Name);
            Console.WriteLine($"Total nodes on C:\\\\: {nodes.Count}");
            
            var battleNodes = nodes.Where(n => n.FullName.IndexOf("battle", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Console.WriteLine($"Battle nodes: {battleNodes.Count}");
            foreach(var b in battleNodes.Take(10)) {
                Console.WriteLine(b.FullName);
            }
        } catch (Exception ex) {
            Console.WriteLine("Error: " + ex.Message);
        }
    }
}
'''
with open('test_ntfs.cs', 'w', encoding='utf-8') as f:
    f.write(cs)
