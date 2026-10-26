using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FireshareShare
{
    public class FireshareSharePlugin : GenericPlugin
    {
        static readonly ILogger logger = LogManager.GetLogger();

        readonly FireshareSettingsViewModel settings;

        public override Guid Id { get; } = Guid.Parse("b79d614b-e840-4041-b2ed-13cd5c6d061d");

        public FireshareSharePlugin(IPlayniteAPI api) : base(api)
        {
            // .NET Framework 4.6.2 does not offer TLS 1.2 unless asked.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            settings = new FireshareSettingsViewModel(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new FireshareSettingsView(settings);

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count != 1) yield break;
            var game = args.Games[0];
            yield return new GameMenuItem { Description = "Share captures to Fireshare…", Action = _ => Share(game) };
        }

        void Share(Game game)
        {
            var s = settings.Settings;
            if (!s.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Set the Fireshare URL and upload token first, under Add-ons → Extension settings → Fireshare Share.", "Fireshare Share");
                return;
            }

            var files = PickCaptures(game, s.CapturesFolder);
            if (files.Length == 0) return;

            var client = new FireshareClient(s.ServerUrl, s.Token);
            var links = new List<string>();
            var errors = new List<string>();
            var skipped = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                p.ProgressMaxValue = files.Length;
                int? gameId = null;
                try { gameId = FireshareClient.MatchGame(game.Name, client.GamesAsync(p.CancelToken).GetAwaiter().GetResult()); }
                catch (Exception ex) { errors.Add(ex.Message); return; }

                for (var i = 0; i < files.Length && !p.CancelToken.IsCancellationRequested; i++)
                {
                    var name = Path.GetFileName(files[i]);
                    p.Text = $"Sharing {name} ({i + 1}/{files.Length})";
                    p.CurrentProgressValue = i;
                    try
                    {
                        var result = client.ShareAsync(files[i], gameId, p.CancelToken).GetAwaiter().GetResult();
                        links.Add(result.Link);
                        if (result.AlreadyThere) skipped++;
                    }
                    // An HTTP timeout is also an OperationCanceledException; only the user's cancel stops the batch.
                    catch (OperationCanceledException) when (p.CancelToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Fireshare upload failed: {files[i]}");
                        errors.Add($"{name}: {ex.Message}");
                    }
                }
            }, new GlobalProgressOptions("Sharing to Fireshare", true) { IsIndeterminate = false });

            if (links.Count > 0)
            {
                var text = string.Join(Environment.NewLine, links);
                try
                {
                    Clipboard.SetText(text);
                    var msg = new StringBuilder($"{links.Count} link{(links.Count == 1 ? "" : "s")} copied to the clipboard.");
                    if (skipped > 0) msg.Append($" {skipped} already on Fireshare.");
                    PlayniteApi.Notifications.Add(new NotificationMessage("fireshare-share-" + Guid.NewGuid(), msg.ToString(), NotificationType.Info));
                }
                // Another app can hold the clipboard; show the links instead of losing them.
                catch (Exception) { PlayniteApi.Dialogs.ShowMessage(text, "Fireshare links"); }
            }
            if (errors.Count > 0)
                PlayniteApi.Dialogs.ShowErrorMessage(string.Join(Environment.NewLine, errors), "Fireshare Share");
        }

        string[] PickCaptures(Game game, string folder)
        {
            var types = FireshareClient.ImageTypes.Concat(FireshareClient.VideoTypes).ToArray();
            // Game Bar names captures after the window title, which may differ from Playnite's name.
            var prefix = new string(game.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ';' && c != '|').ToArray()).Trim();
            var gameFilter = string.Join(";", types.Select(t => $"{prefix}*.{t}"));
            var allFilter = string.Join(";", types.Select(t => $"*.{t}"));
            var hasMatches = Directory.Exists(folder) && types.Any(t => Directory.EnumerateFiles(folder, $"{prefix}*.{t}").Any());

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Share {game.Name} captures to Fireshare",
                Multiselect = true,
                InitialDirectory = Directory.Exists(folder) ? folder : null,
                Filter = $"{prefix} captures|{gameFilter}|All captures|{allFilter}",
                FilterIndex = hasMatches ? 1 : 2,
            };
            return dialog.ShowDialog(PlayniteApi.Dialogs.GetCurrentAppWindow()) == true ? dialog.FileNames : new string[0];
        }
    }
}
