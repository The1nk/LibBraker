using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibBraker
{
    public class Job
    {
        public enum JOB_STATE
        {
            Waiting, CopyingToCache, InCache, Encoding, CopyingBackToLibrary, Completed, Canceled
        }

        public JOB_STATE State { get; set; }
        public string? InputFilename { get; set; }
        public string? CacheFilename { get; set; }
        public string EncodeFilename => CacheFilename + ".mp4";
        public string? OutputFilename { get; set; }
        public long InputFileSizeBytes { get; set; }
        public long OutputFileSizeBytes { get; set; }
        public Task Task { get; set; }
        public DateTime EncodeStart { get; set; }
        public DateTime EncodeEnd { get; set; }
    }
}
