using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GamesCacheUpdater
{
    class Program
    {
        static void Main(string[] args)
        {
            var updater = new CacheUpdater();
            updater.Initialize();
            updater.DownloadPlays();
            updater.DownloadCollection();
            updater.LoadCachedGameDetails();
            updater.DownloadUpdatedGameDetails();
            updater.ProcessPlays();
            updater.ProcessCollection();
            updater.SaveEverything();
        }
    }
}
