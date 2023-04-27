using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace LibBraker
{
    public static class JobEngine
    {
        public static List<Job> FindJobs()
        {
            Log.Information("Finding files to be re-encoded..");

            var args = Args.GetArgs();
            if (args.LibraryPath == null)
                throw new Exception("Library path is null!");

            var eo = new EnumerationOptions
            {
                MatchCasing = System.IO.MatchCasing.PlatformDefault,
                MatchType = MatchType.Simple,
                ReturnSpecialDirectories = true,
                RecurseSubdirectories = args.RecurseThroughSubdirectories
            };

            var files = System.IO.Directory.GetFiles(args.LibraryPath, "*", eo)
                .Select(f => new Job()
                {
                    InputFilename = f, InputFileSizeBytes = new FileInfo(f!).Length, State = Job.JOB_STATE.Waiting,
                    OutputFilename = Path.Combine(Path.GetDirectoryName(f)!,
                        Path.GetFileNameWithoutExtension(f) + ".mp4")
                })
                .ToList();

            files = ExclusionEngine.RemoveExcludedFiles(files);

            Log.Information("Found {0} files to potentially be re-encoded, totaling {1:#,##0} bytes", files.Count,
                files.Sum(f => f.InputFileSizeBytes));

            files = args.AscendingOrder
                ? files.OrderBy(f => f.InputFileSizeBytes).ToList()
                : files.OrderByDescending(f => f.InputFileSizeBytes).ToList();

            return files;
        }
    }
}