using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Playnite.SDK;

namespace FireshareShare
{
    /// <summary>Reads ScreenshotsVisualizer's per-game index, so its folder settings and overrides decide what belongs to a game.</summary>
    public static class ScreenshotsVisualizerIndex
    {
        static readonly ILogger logger = LogManager.GetLogger();
        const string PluginId = "c6c8276f-91bf-48e5-a1d1-4bee0b493488";

        /// <summary>The game's indexed files Fireshare accepts, newest first; null when ScreenshotsVisualizer has no index for it.</summary>
        public static List<string> Files(IPlayniteAPI api, Guid gameId)
        {
            var file = Path.Combine(api.Paths.ExtensionsDataPath, PluginId, "ScreenshotsVisualizer", gameId + ".json");
            if (!File.Exists(file)) return null;
            try
            {
                using (var stream = File.OpenRead(file))
                {
                    var index = (GameIndex)new DataContractJsonSerializer(typeof(GameIndex)).ReadObject(stream);
                    return (index.Items ?? new List<Item>())
                        .Select(i => i.FileName)
                        .Where(f => FireshareClient.KindOf(f ?? "") != null && File.Exists(f))
                        .OrderByDescending(File.GetLastWriteTime)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Could not read ScreenshotsVisualizer's index {file}");
                return null;
            }
        }

        /// <summary>The games each indexed file is listed under, by full path; empty without ScreenshotsVisualizer.</summary>
        /// <remarks>A file can be under several games when one name prefixes another ("Resident Evil" and "Resident Evil 4").</remarks>
        public static Dictionary<string, List<string>> Owners(IPlayniteAPI api)
        {
            var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(api.Paths.ExtensionsDataPath, PluginId, "ScreenshotsVisualizer");
            if (!Directory.Exists(dir)) return owners;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using (var stream = File.OpenRead(file))
                    {
                        var index = (GameIndex)new DataContractJsonSerializer(typeof(GameIndex)).ReadObject(stream);
                        foreach (var item in index.Items ?? new List<Item>())
                        {
                            if (item.FileName == null) continue;
                            if (!owners.TryGetValue(item.FileName, out var names)) owners[item.FileName] = names = new List<string>();
                            names.Add(index.Name);
                        }
                    }
                }
                catch (Exception ex) { logger.Warn(ex, $"Could not read ScreenshotsVisualizer's index {file}"); }
            }
            return owners;
        }

        [DataContract] class GameIndex
        {
            [DataMember(Name = "Name")] public string Name;
            [DataMember(Name = "Items")] public List<Item> Items;
        }

        [DataContract] class Item
        {
            [DataMember(Name = "FileName")] public string FileName;
        }
    }
}
