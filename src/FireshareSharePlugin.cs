using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace FireshareShare
{
    public class FireshareSharePlugin : GenericPlugin
    {
        static readonly ILogger logger = LogManager.GetLogger();

        readonly FireshareSettingsViewModel settings;
        readonly Lazy<MediaIdCache> ids;

        public override Guid Id { get; } = Guid.Parse("b79d614b-e840-4041-b2ed-13cd5c6d061d");

        /// <summary>Bound by themes as {PluginSettings Plugin=FireshareShare, Path=ShareCommand}.</summary>
        public ThemeCommands Commands { get; }

        public FireshareSharePlugin(IPlayniteAPI api) : base(api)
        {
            // .NET Framework 4.6.2 does not offer TLS 1.2 unless asked.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            settings = new FireshareSettingsViewModel(this);
            ids = new Lazy<MediaIdCache>(() => new MediaIdCache(Path.Combine(GetPluginUserDataPath(), "media-ids.tsv")));
            Properties = new GenericPluginProperties { HasSettings = true };
            Commands = new ThemeCommands { ShareCommand = new RelayCommand<object>(ShareFromTheme) };
            AddSettingsSupport(new AddSettingsSupportArgs { SourceName = "FireshareShare", SettingsRoot = nameof(Commands) });
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Controller X on a ScreenshotsVisualizer screenshot shares it, in any theme, without editing theme files.
            EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown));
        }

        /// <summary>Runs only for keys nothing else handled, so a theme that binds X itself keeps it.</summary>
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.GetType().GetProperty("Button")?.GetValue(e)?.ToString() != "X") return;
            var item = (Keyboard.FocusedElement as FrameworkElement)?.DataContext;
            if (item?.GetType().FullName != "ScreenshotsVisualizer.Models.Screenshot")
            {
                logger.Debug($"X on {item?.GetType().FullName ?? "nothing"}, not a ScreenshotsVisualizer screenshot");
                return;
            }
            e.Handled = true;
            ShareFromTheme(item);
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new FireshareSettingsView(settings);

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count != 1) yield break;
            var game = args.Games[0];
            yield return new GameMenuItem { Description = "Share captures to Fireshare…", Action = _ => Share(game) };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // No section puts it at the top of the main menu rather than under Extensions.
            yield return new MainMenuItem { Description = "Share captures to Fireshare…", Action = _ => Share(null) };
        }

        /// <summary>Opens the capture grid for one game, or for every capture when <paramref name="game"/> is null.</summary>
        void Share(Game game)
        {
            if (!EnsureConfigured()) return;
            var s = settings.Settings;
            var picked = CapturePicker.Pick(PlayniteApi, game, s.CapturesFolder, new FireshareClient(s.ServerUrl, s.Token, s.Session), ids.Value);
            if (picked.Length > 0) Upload(game?.Name, picked);
            try { ids.Value.Save(); }
            catch (Exception ex) { logger.Warn(ex, "Could not save the media id cache"); }
        }

        /// <summary>Shares one screenshot a theme passes in: a path, or an item with a FileName (ScreenshotsVisualizer's).</summary>
        void ShareFromTheme(object item)
        {
            var path = item as string ?? item?.GetType().GetProperty("FileName")?.GetValue(item) as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !EnsureConfigured()) return;
            // ScreenshotsVisualizer items say which game they belong to; otherwise use the selected one.
            var gameId = item?.GetType().GetProperty("GameId")?.GetValue(item) as Guid?;
            var game = (gameId.HasValue ? PlayniteApi.Database.Games.Get(gameId.Value) : null) ?? PlayniteApi.MainView.SelectedGames?.FirstOrDefault();
            Upload(game?.Name, new[] { new Capture { Path = path } });
        }

        bool EnsureConfigured()
        {
            if (settings.Settings.IsConfigured) return true;
            PlayniteApi.Dialogs.ShowErrorMessage("Set the Fireshare URL and upload token first, under Add-ons → Extension settings → Fireshare Share.", "Fireshare Share");
            return false;
        }

        void Upload(string gameName, Capture[] files)
        {
            var s = settings.Settings;
            var client = new FireshareClient(s.ServerUrl, s.Token, s.Session);
            var links = new List<string>();
            var errors = new List<string>();
            var added = new List<string>();
            var skipped = 0;

            IEnumerable<string> Candidates(Capture f) => f.GameNames.Concat(new[] { gameName }).Where(n => n != null);
            string Owner(Capture f) => Candidates(f).FirstOrDefault();
            List<GameRef> games = null;
            int? Link(Capture f) => FireshareClient.MatchCapture(Candidates(f), f.Path, games);

            // Read on the UI thread: the year tells same-named SteamGridDB games apart.
            var years = files.Select(Owner).Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n, n => PlayniteApi.Database.Games.FirstOrDefault(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase))?.ReleaseYear, StringComparer.OrdinalIgnoreCase);
            var resolved = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            var unclear = new Dictionary<string, List<GridGame>>(StringComparer.OrdinalIgnoreCase);

            int? Create(GridGame game, CancellationToken ct)
            {
                var created = client.CreateGameAsync(game, ct).GetAwaiter().GetResult();
                games.Add(created);
                added.Add(created.Name);
                return created.Id;
            }

            // Fireshare's games, and for the ones it lacks, SteamGridDB. Creating needs a signed-in session.
            var canCreate = s.CreateMissingGames && !string.IsNullOrEmpty(s.ProtectedSession);
            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                try { games = client.GamesAsync(p.CancelToken).GetAwaiter().GetResult(); }
                catch (Exception ex) { errors.Add(ex.Message); return; }
                if (!canCreate) return;

                foreach (var name in files.Where(f => f.Path != null && Link(f) == null).Select(Owner).Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    p.Text = $"Looking up {name}";
                    try
                    {
                        var results = client.SearchGamesAsync(name, p.CancelToken).GetAwaiter().GetResult();
                        var pick = FireshareClient.PickGame(results, name, years[name]);
                        if (pick != null) resolved[name] = Create(pick, p.CancelToken);
                        else unclear[name] = FireshareClient.RankGames(results, name, years[name]);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"Could not add {name} to Fireshare");
                        errors.Add($"{name}: {ex.Message}");
                        resolved[name] = null;
                    }
                }
            }, new GlobalProgressOptions("Checking games on Fireshare", false) { IsIndeterminate = true });
            if (games == null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(string.Join(Environment.NewLine, errors), "Fireshare Share");
                return;
            }

            // Asked only where the name and year did not settle it.
            var chosen = unclear.ToDictionary(kv => kv.Key, kv => GameChooser.Choose(PlayniteApi, kv.Key, years[kv.Key], kv.Value), StringComparer.OrdinalIgnoreCase);

            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                p.ProgressMaxValue = files.Length;
                foreach (var kv in chosen)
                {
                    resolved[kv.Key] = null;
                    if (kv.Value == null) continue;
                    p.Text = $"Adding {kv.Value.Name} to Fireshare";
                    try { resolved[kv.Key] = Create(kv.Value, p.CancelToken); }
                    catch (Exception ex) { errors.Add($"{kv.Value.Name}: {ex.Message}"); }
                }

                for (var i = 0; i < files.Length && !p.CancelToken.IsCancellationRequested; i++)
                {
                    var name = files[i].Path != null ? Path.GetFileName(files[i].Path) : files[i].Remote.Title ?? files[i].Remote.Id;
                    p.Text = $"Sharing {name} ({i + 1}/{files.Length})";
                    p.CurrentProgressValue = i;
                    try
                    {
                        ShareResult result;
                        if (files[i].Path == null)
                        {
                            // Already on Fireshare only: nothing to upload, just its link.
                            result = new ShareResult { Link = client.LinkAsync(files[i].Remote.Kind, files[i].Remote.Id, p.CancelToken).GetAwaiter().GetResult(), AlreadyThere = true };
                        }
                        else
                        {
                            var owner = Owner(files[i]);
                            var gameId = Link(files[i]) ?? (owner != null && resolved.TryGetValue(owner, out var id) ? id : null);
                            result = client.ShareAsync(files[i].Path, gameId, p.CancelToken).GetAwaiter().GetResult();
                        }
                        links.Add(result.Link);
                        if (result.AlreadyThere) skipped++;
                    }
                    // An HTTP timeout is also an OperationCanceledException; only the user's cancel stops the batch.
                    catch (OperationCanceledException) when (p.CancelToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Fireshare upload failed: {name}");
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
                    if (added.Count > 0) msg.Append($" Added {string.Join(", ", added)} to Fireshare.");
                    // Fullscreen hides notifications until opened, so confirm in a dialog the controller can dismiss.
                    if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                        PlayniteApi.Dialogs.ShowMessage(msg + Environment.NewLine + Environment.NewLine + text, "Shared to Fireshare");
                    else
                        PlayniteApi.Notifications.Add(new NotificationMessage("fireshare-share-" + Guid.NewGuid(), msg.ToString(), NotificationType.Info));
                }
                // Another app can hold the clipboard; show the links instead of losing them.
                catch (Exception) { PlayniteApi.Dialogs.ShowMessage(text, "Fireshare links"); }
            }
            if (errors.Count > 0)
                PlayniteApi.Dialogs.ShowErrorMessage(string.Join(Environment.NewLine, errors), "Fireshare Share");
        }
    }

    public class ThemeCommands
    {
        public RelayCommand<object> ShareCommand { get; set; }
    }
}
