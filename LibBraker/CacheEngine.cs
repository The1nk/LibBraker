using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LibBraker
{
    internal static class CacheEngine
    {
        private const string Prefix = "cache_";
        private const string Suffix = ".tmp";
        private static Guid _guid = Guid.NewGuid();
        
        internal static void DeleteOldCaches()
        {
            var workingDirectory = Args.GetArgs().WorkingDirectory;
            if (workingDirectory == null) return;

            Log.Information("Deleting old cache files if necessary..");

            var filesToDelete = System.IO.Directory
                .GetFiles(workingDirectory, $"{Prefix}*{Suffix}", SearchOption.TopDirectoryOnly)
                .ToList().Union(System.IO.Directory
                    .GetFiles(workingDirectory, $"{Prefix}*{Suffix}.mp4", SearchOption.TopDirectoryOnly)
                    .ToList());

            foreach (var f in filesToDelete)
            {
                try
                {
                    Log.Debug("Deleting old cache file {0}", f);
                    System.IO.File.Delete(f);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unable to delete file", f);
                }
            }
        }

        internal static string GetCacheFilename()
        {
            string path = string.Empty;
            do
            {
                var workingDirectory = Args.GetArgs().WorkingDirectory;
                if (workingDirectory == null) throw new Exception("Working Directory is null!");

                path = System.IO.Path.Combine(workingDirectory,
                    $"{Prefix}{_guid.ToString()[^12..]}{Suffix}");

                _guid = Guid.NewGuid();
            } while (System.IO.File.Exists(path));

            return path;
        }
    }
}
