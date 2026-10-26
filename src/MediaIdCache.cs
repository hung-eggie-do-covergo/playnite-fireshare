using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FireshareShare
{
    /// <summary>Fireshare ids of local files, keyed by path, size and write time, so each file is hashed once.</summary>
    public class MediaIdCache
    {
        readonly string file;
        readonly Dictionary<string, string> ids = new Dictionary<string, string>();
        bool dirty;

        public MediaIdCache(string file)
        {
            this.file = file;
            try
            {
                foreach (var line in File.Exists(file) ? File.ReadAllLines(file) : new string[0])
                {
                    var tab = line.LastIndexOf('\t');
                    if (tab > 0) ids[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch (Exception) { ids.Clear(); }
        }

        public string Get(string path)
        {
            var info = new FileInfo(path);
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (ids)
                if (ids.TryGetValue(key, out var known)) return known;

            var id = FireshareClient.MediaId(path);
            lock (ids) { ids[key] = id; dirty = true; }
            return id;
        }

        public void Save()
        {
            lock (ids)
            {
                if (!dirty) return;
                // Entries for files that are gone would only grow the file.
                var live = ids.Where(kv => File.Exists(kv.Key.Substring(0, kv.Key.IndexOf('|'))));
                File.WriteAllLines(file, live.Select(kv => kv.Key + "\t" + kv.Value));
                dirty = false;
            }
        }
    }
}
