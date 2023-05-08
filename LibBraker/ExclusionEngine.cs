using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Serilog;

namespace LibBraker;

public static class ExclusionEngine
{
    private static History _history = null;
    private static bool _willSave = true;
    static ExclusionEngine()
    {
        if (!File.Exists(".history"))
        {
            _history = new History();
            return;
        }

        Log.Information("Reading in history file..");
        string json = string.Empty;
        try
        {
            using var fs = File.Open(".history", FileMode.Open, FileAccess.Read);
            using var sr = new StreamReader(fs);
            json = sr.ReadToEnd();
            sr.Close();
            fs.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reading in history. Continuing with new history. Won't save history. Remove .history file to reset.");
            _history = new History();
            _willSave = false;
            return;
        }

        try
        {
            _history = JsonConvert.DeserializeObject<History>(json) ?? new History();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deserializing history. Won't save. Remove .history file to reset.");
            _history = new History();
            _willSave = false;
        }
    }

    public static List<Job> RemoveExcludedFiles(List<Job> files)
    {
        var before = files.Count;
        var extensions = new List<string>()
        {
            "mp4", "m4v", "mkv", "mov", "mpg", "mpeg", "avi", "wmv", "flv", "webm"
        };

        var ret = (from file in files
            let extension = System.IO.Path.GetExtension(file.InputFilename)[1..]
            where extensions.Contains(extension?.ToLower() ?? "not gonna work")
            select file).ToList();
        Log.Information("Skipping {0:#,##0} files due to extension..", (before - ret.Count));
        
        before = ret.Count;
        lock (_history)
        {
            ret.RemoveAll(r =>
                _history.HistoryItems.Any(h => h.Filename == r.InputFilename && h.FileSize == r.InputFileSizeBytes));
        }
        Log.Information("Skipping {0:#,##0} files due to history..", (before - ret.Count));

        CleanUpHistory(files);
        return ret;
    }

    private static void CleanUpHistory(List<Job> files)
    {
        var removed = 0;
        lock (_history)
        {
            var toRemove = _history.HistoryItems.Where(h =>
                files.All(f =>
                    !(f.InputFilename.Equals(h.Filename, StringComparison.CurrentCultureIgnoreCase) &&
                      f.InputFileSizeBytes == h.FileSize))).ToList();

            var idx = 0;
            var max = toRemove.Count;
            while (idx < max)
            {
                try
                {
                    if (File.Exists(toRemove[idx].Filename))
                    {
                        if (new FileInfo(toRemove[idx].Filename).Length == toRemove[idx].FileSize) continue;

                        toRemove.RemoveAt(idx);
                        max--;
                    }
                    else
                        idx++;
                    
                }
                catch
                {
                    idx++;
                }
            }

            removed += toRemove.Count;
            toRemove.ForEach(h => _history.HistoryItems.Remove(h));
        }
        
        SaveHistory();
        if (removed != 0) 
            Log.Information("Removed {0:#,##0} history records for files that no longer exist", removed);
    }

    public static void AddToHistory(HistoryItem item)
    {
        lock (_history)
        {
            _history.HistoryItems.Add(item);
        }
    }

    public static void SaveHistory()
    {
        var json = string.Empty;
        lock (_history)
        {
            json = JsonConvert.SerializeObject(_history);
        }

        if (string.IsNullOrEmpty(json))
            return;

        using var fs = File.Open(".history", FileMode.Create, FileAccess.Write);
        using var sw = new StreamWriter(fs);
        sw.Write(json);
        sw.Close();
        fs.Close();
    }
}