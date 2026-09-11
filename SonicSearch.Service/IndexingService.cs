using System.ServiceProcess;

namespace SonicSearch.Service
{
    /// <summary>
    /// Runs as LocalSystem (the default account for a service with none configured), so it
    /// always has the raw-volume/USN-journal access SonicSearch's GUI otherwise needs
    /// Administrator for - installed/removed via `sc create`/`sc delete` from the GUI's Settings
    /// (see SonicSearch's ServiceInstallHelper.cs), not via InstallUtil, so no ProjectInstaller
    /// is needed here.
    /// </summary>
    public class IndexingService : ServiceBase
    {
        private PipeServer _pipeServer;

        public IndexingService()
        {
            ServiceName = "SonicSearchService";
        }

        protected override void OnStart(string[] args)
        {
            _pipeServer = new PipeServer();
            _pipeServer.Start();
        }

        protected override void OnStop()
        {
            _pipeServer?.Stop();
            _pipeServer = null;
        }
    }
}
