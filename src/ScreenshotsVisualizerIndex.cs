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

        [DataContract] class GameIndex
        {
            [DataMember(Name = "Items")] public List<Item> Items;
        }

        [DataContract] class Item
        {
            [DataMember(Name = "FileName")] public string FileName;
        }
    }
}
