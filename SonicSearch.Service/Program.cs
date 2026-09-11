using System.ServiceProcess;

namespace SonicSearch.Service
{
    internal static class Program
    {
        private static void Main()
        {
            ServiceBase.Run(new ServiceBase[] { new IndexingService() });
        }
    }
}
