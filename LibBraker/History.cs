using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LibBraker
{
    public class History
    {
        public List<HistoryItem> HistoryItems { get; set; }

        public History()
        {
            HistoryItems = new List<HistoryItem>();
        }
    }

    public struct HistoryItem
    {
        public string Filename { get; set; }
        public long FileSize { get; set; }

        public HistoryItem(string filename, long fileSize)
        {
            Filename = filename;
            FileSize = fileSize;
        }
    }
}
