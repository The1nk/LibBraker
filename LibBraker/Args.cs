using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PowerArgs;
using Serilog;
using Serilog.Core;

namespace LibBraker
{
    [TabCompletion]
    public class Args
    {
        private static Args _args = new();
        private static Process _handBrakeProcess;
        private static bool _quitting = false;
        private static string _lastPercent;
        private static DateTime _lastPrint;

        [HelpHook]
        [ArgShortcut("-?")]
        [ArgDescription("This")]
        public bool Help { get; set; }

        [ArgDescription("Log File filename. If omitted no logs are saved to disk")]
        public string? LogFileFilename { get; set; }

        [ArgRequired(PromptIfMissing = false)]
        [ArgDescription("Library paths to search for files")]
        public List<string>? LibraryPath { get; set; }

        [ArgDefaultValue(false)]
        [ArgDescription("Overwrite the original file if necessary")]
        public bool OverwriteOriginal { get; set; }

        [ArgDefaultValue(false)]
        [ArgDescription("Delete original file. Relevant if file extension may change")]
        public bool DeleteOriginalFile { get; set; }

        [ArgDefaultValue(1)]
        [ArgRange(0, 1000000000)]
        [ArgDescription("The cache in the working directory will be >= this size during execution. Specify 0 for unlimited, 1 for [basically] only one file")]
        public int LocalCacheSizeMb { get; set; }

        [ArgDefaultValue(false)]
        [ArgDescription("Recurse through subdirectories, or only process the Library's root")]
        public bool RecurseThroughSubdirectories { get; set; }


        [ArgDefaultValue(".")]
        [ArgDescription("Working directory to store cache files, and temporary output re-encoded files")]
        public string? WorkingDirectory { get; set; }

        [ArgDefaultValue(1)]
        [ArgDescription("Maximum copy-to-cache tasks to run in parallel")]
        public int MaxCopyToCacheTasks { get; set; }

        [ArgDefaultValue(false)]
        [ArgDescription("Encode smaller files first")]
        public bool AscendingOrder { get; set; }

        [ArgDefaultValue("veryslow")]
        [ArgDescription("x264 Preset - Select from ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow. placebo is supported but is not likely to be useful")]
        public string x264Preset { get; set; }

        [ArgDefaultValue("high")]
        [ArgDescription("x264 Profile - Select from auto, baseline, main, high")]
        public string x264Profile { get; set; }

        [ArgDefaultValue("animation")]
        [ArgDescription("x264 Tune - Select from film, animation, grain, stillimage, fastdecode, zerolatency. psnr and ssim are also supported but are not likely to be useful")]
        public string x264Tune { get; set; }

        [ArgDefaultValue("4.1")]
        [ArgDescription("h264 Level - Select from 1.0, 1b, 1.1, 1.2, 1.3, 2.0, 2.1, 2.2, 3.0, 3.1, 3.2, 4.0, 4.1, 4.2, 5.0, 5.1, 5.2, 6.0, 6.1, 6.2")]
        public string h264Level { get; set; }

        public void Main()
        {
            _args = this;
            Directory.SetCurrentDirectory(GetArgs().WorkingDirectory);

            var lc = new LoggerConfiguration().WriteTo.Console();

            if (!string.IsNullOrEmpty(GetArgs().LogFileFilename)) 
                lc.WriteTo.File(GetArgs().LogFileFilename);
            Log.Logger = lc.CreateLogger();

            Console.CancelKeyPress += (sender, args) =>
            {
                Log.Information("Quitting..");
                _quitting = true;
                args.Cancel = true;
            };

            CacheEngine.DeleteOldCaches();
            var jobs = JobEngine.FindJobs();

            var tasks = new List<Task>();
            while (jobs.Any())
            {
                if (_quitting)
                {
                    if (_handBrakeProcess != null)
                    {
                        try
                        {
                            Log.Information("Killing HandBrake due to Control+C");
                            _handBrakeProcess.Kill(true);
                            Log.Information("Killed");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error killing process");
                        }
                    }

                    break;
                }

                Job job = null;

                // Check if cache has room to spare
                var localCacheInUse = jobs
                    .Where(j => j.State is Job.JOB_STATE.InCache or Job.JOB_STATE.CopyingToCache or Job.JOB_STATE.Encoding or Job.JOB_STATE.CopyingBackToLibrary)
                    .Sum(j => j.InputFileSizeBytes) / 1000000;
                var copyingJobs = jobs.Count(j => j.State == Job.JOB_STATE.CopyingToCache);
                if (localCacheInUse < GetArgs().LocalCacheSizeMb && copyingJobs < MaxCopyToCacheTasks)
                {
                    job = jobs.FirstOrDefault(j => j.State == Job.JOB_STATE.Waiting);
                    if (job != null)
                    {
                        job.State = Job.JOB_STATE.CopyingToCache;
                        job.Task = new Task(async () =>
                        {
                            var jobInner = job;
                            jobInner.CacheFilename = CacheEngine.GetCacheFilename();
                            Log.Information("Beginning copy of '{0}', from '{1}' to '{2}'",
                                System.IO.Path.GetFileName(jobInner.InputFilename),
                                System.IO.Path.GetDirectoryName(jobInner.InputFilename), jobInner.CacheFilename);

                            try
                            {
                                System.IO.File.Copy(jobInner.InputFilename!, jobInner.CacheFilename); 
                                jobInner.State = Job.JOB_STATE.InCache;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Failed to copy file '{0}'", jobInner.InputFilename);
                                jobInner.State = Job.JOB_STATE.Canceled;
                            }
                        });

                        tasks.Add(job.Task);
                        job.Task.Start();
                    }
                }

                // Make sure we're encoding something, if we can
                if (jobs.All(j => j.State != Job.JOB_STATE.Encoding))
                {
                    var next = jobs.FirstOrDefault(j => j.State == Job.JOB_STATE.InCache);
                    if (next != null)
                    {
                        next.Task = CreateHandBrakeTask(next);
                        next.State = Job.JOB_STATE.Encoding;
                        tasks.Add(next.Task);
                        next.Task.Start();
                    }
                }

                var doneJobs = jobs.Where(j => j.State == Job.JOB_STATE.Completed).ToList();
                foreach (var doneJob in doneJobs)
                {
                    ExclusionEngine.AddToHistory(new HistoryItem(doneJob.InputFilename, doneJob.InputFileSizeBytes));
                    ExclusionEngine.AddToHistory(new HistoryItem(doneJob.OutputFilename, doneJob.OutputFileSizeBytes));
                    ExclusionEngine.SaveHistory();

                    if (File.Exists(doneJob.CacheFilename))
                    {
                        Log.Information("Removing old cache file '{0}'", doneJob.CacheFilename);
                        try
                        {
                            File.Delete(doneJob.CacheFilename);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unable to delete old cache file '{0}'", doneJob.CacheFilename);
                        }
                    }

                    if (File.Exists(doneJob.EncodeFilename))
                    {
                        Log.Information("Removing old encode file '{0}'", doneJob.EncodeFilename);
                        try
                        {
                            File.Delete(doneJob.EncodeFilename);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unable to delete old encode file '{0}'", doneJob.EncodeFilename);
                        }
                    }
                }

                jobs.RemoveAll(j => doneJobs.Contains(j));

                doneJobs = jobs.Where(j => j.State == Job.JOB_STATE.Canceled).ToList();
                foreach (var doneJob in doneJobs)
                {
                    if (File.Exists(doneJob.CacheFilename))
                    {
                        Log.Information("Removing old cache file '{0}'", doneJob.CacheFilename);
                        try
                        {
                            File.Delete(doneJob.CacheFilename);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unable to delete old cache file '{0}'", doneJob.CacheFilename);
                        }
                    }

                    if (File.Exists(doneJob.EncodeFilename))
                    {
                        Log.Information("Removing old encode file '{0}'", doneJob.EncodeFilename);
                        try
                        {
                            File.Delete(doneJob.EncodeFilename);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unable to delete old encode file '{0}'", doneJob.EncodeFilename);
                        }
                    }
                }

                jobs.RemoveAll(j => doneJobs.Contains(j));
                Thread.Sleep(100);
            }

            Log.Information("Done!");
        }

        private Task CreateHandBrakeTask(Job job)
        {
            var t = new Task(async () =>
            {
                var jobInner = job;
                ProcessStartInfo psi;
                try
                {
                    psi = new ProcessStartInfo
                    {
                        WorkingDirectory = GetArgs().WorkingDirectory,
                        FileName = System.IO.Path.GetFileName("HandBrakeCLI.exe"),
                    };

                    psi.Arguments +=
                        $"-i \"{job.CacheFilename}\" -o \"{job.EncodeFilename}\" -O -f mp4 -O --decomb --modulus 16 -e x264 -q 32 --vfr -E ac3 -6 ac3 -R Auto -B 48 -D 1 --all-audio --all-subtitles --gain 0 --audio-fallback ffac3 --x264-preset=\"{GetArgs().x264Preset}\" --x264-profile=\"{GetArgs().x264Profile}\" --x264-tune=\"{GetArgs().x264Tune}\" --h264-level=\"{GetArgs().h264Level}\"";
                    Log.Information("Running with arguments: {0}", psi.Arguments);
                    psi.CreateNoWindow = true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error creating Process info ...");
                    jobInner.State = Job.JOB_STATE.Canceled;
                    return;
                }

                _handBrakeProcess = new Process() { StartInfo = psi };
                try
                {
                    Log.Information("Beginning encoding for '{0}'.. Output to follow:",
                        System.IO.Path.GetFileName(jobInner.InputFilename));
                    _handBrakeProcess.StartInfo.RedirectStandardError = true;
                    _handBrakeProcess.StartInfo.RedirectStandardOutput = true;
                    _handBrakeProcess.ErrorDataReceived += (sender, e) => HandleHandBrakeOutput(e.Data);
                    _handBrakeProcess.OutputDataReceived += (sender, e) => HandleHandBrakeOutput(e.Data);
                    _handBrakeProcess.Start();
                    jobInner.EncodeStart = DateTime.Now;
                    _handBrakeProcess.BeginErrorReadLine();
                    _handBrakeProcess.BeginOutputReadLine();
                    await _handBrakeProcess.WaitForExitAsync();
                    jobInner.EncodeEnd = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error running HandBrakeCLI ...");
                    jobInner.State = Job.JOB_STATE.Canceled;
                    return;
                }

                if (_handBrakeProcess.ExitCode != 0)
                {
                    Log.Error("HandBrakeCLI returned non-zero error code - {0}..", _handBrakeProcess.ExitCode);
                    jobInner.State = Job.JOB_STATE.Canceled;
                    return;
                }

                // Success!
                jobInner.State = Job.JOB_STATE.CopyingBackToLibrary;
                string tmpFilename = jobInner.OutputFilename;
                try
                {
                    while (File.Exists(tmpFilename))
                    {
                        tmpFilename = Path.Combine(Path.GetDirectoryName(tmpFilename),
                            Path.GetFileNameWithoutExtension(tmpFilename) + "_.mp4");
                    }

                    System.IO.File.Copy(jobInner.EncodeFilename, tmpFilename);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error copying file to Library as temp name '{0}'", tmpFilename);
                    jobInner.State = Job.JOB_STATE.Canceled;
                    return;
                }

                if (File.Exists(jobInner.OutputFilename))
                {
                    if (GetArgs().OverwriteOriginal || GetArgs().DeleteOriginalFile)
                    {
                        try
                        {
                            Log.Information("Deleting original file '{0}'", jobInner.InputFilename);
                            System.IO.File.Delete(jobInner.InputFilename);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error deleting original file '{0}', leaving new copy with suffix - '{1}'",
                                jobInner.InputFilename, tmpFilename);
                        }
                    }
                    else
                    {
                        Log.Information("Output file already exists, leaving new copy with suffix - '{0}'",
                            tmpFilename);
                        jobInner.OutputFilename = tmpFilename;
                    }
                }
                
                if (!File.Exists(jobInner.OutputFilename))
                    try
                    {
                        Log.Information("Renaming temp file to real filename '{0}'", jobInner.OutputFilename);
                        System.IO.File.Move(tmpFilename, jobInner.OutputFilename);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error moving file '{0}' to '{1}'", tmpFilename, jobInner.OutputFilename);
                        jobInner.State = Job.JOB_STATE.Canceled;
                        return;
                    }

                // Done
                jobInner.OutputFileSizeBytes = new FileInfo(jobInner.OutputFilename).Length;
                Log.Information(
                    "Stats:\r\nFile:\t{0}\r\n--\r\nIn:\t{1:#,##0} bytes\r\nOut:\t{2:#,##0} bytes\r\nStart:\t{3:O}\r\nEnd:\t{4:O}\r\n--\r\nSaved:\t{5:#,##0} bytes ({6:00.00%})\r\nTook:\t{7:T}",
                    System.IO.Path.GetFileName(jobInner.InputFilename), jobInner.InputFileSizeBytes,
                    jobInner.OutputFileSizeBytes, jobInner.EncodeStart, jobInner.EncodeEnd,
                    jobInner.InputFileSizeBytes - jobInner.OutputFileSizeBytes,
                    1 - jobInner.OutputFileSizeBytes / (decimal)jobInner.InputFileSizeBytes,
                    jobInner.EncodeEnd - jobInner.EncodeStart);
                jobInner.State = Job.JOB_STATE.Completed;
            });

            return t;
        }

        private static void HandleHandBrakeOutput(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;
            
            var emit = false;
            
            // Encoding: task 1 of 1, 94.89 % (24.66 fps, avg 14.76 fps, ETA 00h11m31s)
            var match = Regex.Match(data, "^Encoding: task \\d of \\d, (.+) %");
            if (match.Success)
            {
                var thisPercent = match.Groups[1].Value[..^1];
                if ((thisPercent != _lastPercent && (DateTime.Now - _lastPrint) > TimeSpan.FromSeconds(10)) ||
                    (DateTime.Now - _lastPrint) > TimeSpan.FromMinutes(10))
                {
                    emit = true;
                    _lastPercent = thisPercent;
                    _lastPrint = DateTime.Now;
                }
            }
            else
            {
                emit = true;
                _lastPercent = string.Empty;
                _lastPrint = DateTime.MinValue;
            }

            if (emit)
                Log.Information("OUT --> {0}", data);
        }

        public static Args GetArgs()
        {
            if (_args == null)
                throw new NullReferenceException("args");
            return _args;
        }
    }
}
