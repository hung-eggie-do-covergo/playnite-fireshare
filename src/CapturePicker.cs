using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace FireshareShare
{
    /// <summary>One grid entry: a local file, a Fireshare item, or both once matched.</summary>
    public class Capture
    {
        public string Path;
        public RemoteItem Remote;
        /// <summary>Games it may belong to, tried in order to link the upload to a Fireshare game.</summary>
        public List<string> GameNames = new List<string>();
        public DateTime Date;
        public string Kind => Path != null ? FireshareClient.KindOf(Path) : Remote.Kind;
    }

    /// <summary>Steam-style capture grid. Mouse and keyboard everywhere, a controller in fullscreen mode.</summary>
    public static class CapturePicker
    {
        static readonly ILogger logger = LogManager.GetLogger();

        // Captures usually sit on a network share; reading more than this per open gets slow.
        const int MaxShown = 60;
        const double TileWidth = 360, TileHeight = 203, TileRadius = 10;

        static readonly string[] Types = FireshareClient.ImageTypes.Concat(FireshareClient.VideoTypes).ToArray();


        enum Where { Local, Both, Remote }

        public static Capture[] Pick(IPlayniteAPI api, Game game, string folder, FireshareClient client, MediaIdCache ids)
        {
            // No game means the app-level entry: every capture, nothing to switch to.
            var gameName = game?.Name;
            if (!Directory.Exists(folder))
            {
                api.Dialogs.ShowErrorMessage($"Captures folder not found: {folder}", "Fireshare Share");
                return new Capture[0];
            }

            var fullscreen = api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            // ScreenshotsVisualizer's index knows the user's per-game folders and overrides; the filename guess is the fallback.
            var gameFiles = game == null ? new List<string>() : ScreenshotsVisualizerIndex.Files(api, game.Id) ?? Find(folder, Prefix(gameName));
            var owners = ScreenshotsVisualizerIndex.Owners(api);
            var allFiles = Find(folder, "");
            // Start on the game even with nothing local: its Fireshare captures may still be there.
            var showingAll = game == null;
            var generation = 0;
            var result = new Capture[0];

            Window window = null;

            var list = new ListBox { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
            scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, fullscreen ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
            scroller.SetValue(UIElement.FocusableProperty, false);
            // room for the focused tile to grow and show its ring
            var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(16));
            scroller.AppendChild(presenter);
            list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroller };
            var wrap = new FrameworkElementFactory(typeof(WrapPanel));
            wrap.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            list.ItemsPanel = new ItemsPanelTemplate(wrap);
            list.SelectionChanged += (s, e) =>
            {
                foreach (ListBoxItem item in e.AddedItems) Parts(item).Children[2].Visibility = Visibility.Visible;
                foreach (ListBoxItem item in e.RemovedItems) Parts(item).Children[2].Visibility = Visibility.Collapsed;
            };

            var heading = DialogShell.Heading(api);

            void Show()
            {
                var gen = ++generation;
                var files = showingAll ? allFiles : gameFiles;
                heading.Text = files.Count == 0 ? "No captures found" : (showingAll ? "All captures" : $"{gameName} captures");
                list.Items.Clear();
                var captures = files.Take(MaxShown).Select(f => new Capture
                {
                    Path = f,
                    Date = File.GetLastWriteTime(f),
                    GameNames = showingAll ? (owners.TryGetValue(f, out var names) ? names : new List<string>()) : new List<string> { gameName },
                }).ToList();
                var tiles = captures.Select(Tile).ToList();
                tiles.ForEach(t => list.Items.Add(t));
                if (tiles.Count > 0) window.Dispatcher.BeginInvoke(new Action(() => tiles[0].Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
                // Fireshare lists per game, so the all-captures view stays local.
                var withRemote = !showingAll;

                void Post(Action action) => window.Dispatcher.BeginInvoke(action);

                // WPF decodes images reliably only on STA threads; a newer list stops an older load.
                // The loader reads Capture objects only: the tiles belong to the UI thread.
                var loader = new Thread(() =>
                {
                    Dictionary<string, RemoteItem> remote = null;
                    if (withRemote)
                    {
                        try { remote = client.GameMediaAsync(gameName).GetAwaiter().GetResult().GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First()); }
                        catch (Exception ex) { logger.Warn(ex, "Could not list Fireshare captures"); }
                    }

                    var localIds = new HashSet<string>();
                    for (var i = 0; i < captures.Count; i++)
                    {
                        if (gen != generation) return;
                        var tile = tiles[i];
                        var thumb = Thumbnail(captures[i].Path);
                        if (thumb != null) Post(() => SetThumb(tile, thumb));
                        string id;
                        RemoteItem match = null;
                        try
                        {
                            id = ids.Get(captures[i].Path);
                            // The game view matches against the game's list; elsewhere each file is looked up.
                            if (remote != null) remote.TryGetValue(id, out match);
                            else match = client.LookupAsync(captures[i].Kind, id).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) { logger.Warn(ex, $"Could not check {captures[i].Path} on Fireshare"); continue; }
                        localIds.Add(id);
                        captures[i].Remote = match;
                        Post(() => SetBadge(tile, match != null ? Where.Both : Where.Local, match?.Private ?? false));
                    }
                    if (remote == null) return;

                    foreach (var item in remote.Values.Where(r => !localIds.Contains(r.Id)).OrderByDescending(r => r.Date))
                    {
                        if (gen != generation) return;
                        var capture = new Capture { Remote = item, Date = item.Date, GameNames = { gameName } };
                        ListBoxItem tile = null;
                        window.Dispatcher.Invoke(() =>
                        {
                            tile = Tile(capture);
                            // WPF shows tooltips on keyboard focus, which a controller always has
                            ToolTipService.SetIsEnabled(tile, !fullscreen);
                            SetBadge(tile, Where.Remote, item.Private);
                            // Newest first, matching the local order.
                            var at = list.Items.Cast<ListBoxItem>().TakeWhile(t => ((Capture)t.Tag).Date >= capture.Date).Count();
                            list.Items.Insert(at, tile);
                        });
                        byte[] bytes = null;
                        try { bytes = client.ThumbnailAsync(item).GetAwaiter().GetResult(); }
                        catch (Exception ex) { logger.Warn(ex, $"No Fireshare thumbnail for {item.Id}"); }
                        var thumb = Decode(bytes);
                        if (thumb != null) Post(() => SetThumb(tile, thumb));
                    }
                }) { IsBackground = true };
                loader.SetApartmentState(ApartmentState.STA);
                loader.Start();
            }

            void Confirm()
            {
                var picked = list.SelectedItems.Cast<ListBoxItem>().ToList();
                // Nothing ticked shares the focused capture, so one quick share is one button.
                if (picked.Count == 0 && Keyboard.FocusedElement is ListBoxItem focused) picked.Add(focused);
                result = picked.Select(i => (Capture)i.Tag).ToArray();
                window.Close();
            }

            void SwitchList()
            {
                if (game == null) return;
                showingAll = !showingAll;
                Show();
            }

            list.Margin = new Thickness(10);
            var hints = new List<Hint> { new Hint("ButtonPromptA", "Select"), new Hint("ButtonPromptX", "Share", Confirm) };
            if (game != null) hints.Add(new Hint("ButtonPromptY", "Switch list", SwitchList));
            hints.Add(new Hint("ButtonPromptB", "Back", () => window.Close()));
            window = DialogShell.Create(api, "Share to Fireshare", heading, list, DialogShell.Footer(api, hints.ToArray()), new Size(1640, 940), new Size(1240, 780));

            window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) =>
            {
                var button = DialogShell.ControllerButton(e);
                if (button == "A" && Keyboard.FocusedElement is ListBoxItem item) item.IsSelected = !item.IsSelected;
                else if (button == "X" || button == "Start") Confirm();
                else if (button == "Y") SwitchList();
                else if (button == "B" || e.Key == Key.Escape || e.Key == Key.Back) window.Close();
                else return;
                e.Handled = true;
            }), true);

            Show();
            window.ShowDialog();
            generation++;
            return result;
        }

        /// <summary>Game Bar names files after the window title and swaps characters it cannot use (":" becomes "_").</summary>
        static string Prefix(string gameName) =>
            new string(gameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '?' : c).ToArray()).Trim();

        static List<string> Find(string folder, string prefix) =>
            Types.SelectMany(t => Directory.EnumerateFiles(folder, $"{prefix}*.{t}"))
                 .Select(f => new FileInfo(f))
                 .OrderByDescending(f => f.LastWriteTime)
                 .Select(f => f.FullName)
                 .ToList();

        static ListBoxItem Tile(Capture capture)
        {
            var video = capture.Kind == "video";
            var grid = new Grid { Width = TileWidth, Height = TileHeight, Clip = new RectangleGeometry(new Rect(0, 0, TileWidth, TileHeight), TileRadius, TileRadius) };
            DialogShell.Themed(grid, Panel.BackgroundProperty, "ControlBackgroundBrush", new SolidColorBrush(Color.FromRgb(36, 36, 40)));
            grid.Children.Add(new Image { Stretch = Stretch.UniformToFill });
            var date = new TextBlock
            {
                // A midnight Fireshare date carries no time; showing 12:00 AM would be invented.
                Text = (video ? "▶  " : "") + capture.Date.ToString(capture.Path == null && capture.Date.TimeOfDay == TimeSpan.Zero ? "d" : "g"),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            DialogShell.Themed(date, FrameworkElement.StyleProperty, "TextBlockBaseStyle", null);
            DialogShell.Themed(date, TextBlock.FontSizeProperty, "FontSizeSmall", 16.0);
            DialogShell.Themed(date, TextBlock.ForegroundProperty, "TextBrush", Brushes.White);
            DialogShell.Themed(date, TextBlock.BackgroundProperty, "OverlayBrush", new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
            grid.Children.Add(date);
            var check = new TextBlock { Text = "✓", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DialogShell.Themed(check, TextBlock.ForegroundProperty, "ControlBackgroundBrush", Brushes.White);
            var mark = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Child = check,
            };
            DialogShell.Themed(mark, Border.BackgroundProperty, "GlyphBrush", new SolidColorBrush(Color.FromRgb(26, 159, 255)));
            grid.Children.Add(mark);
            grid.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            });

            return DialogShell.Item(grid, capture, TileRadius);
        }

        static void SetThumb(ListBoxItem tile, BitmapSource thumb) => ((Image)Parts(tile).Children[0]).Source = thumb;

        // Windows' own icon fonts: Fluent on 11, MDL2 on 10. Same code points in both.
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        const string DriveGlyph = "\uEDA2", CloudGlyph = "\uE753", LockGlyph = "\uE72E";

        static void SetBadge(ListBoxItem tile, Where where, bool isPrivate)
        {
            var chips = (StackPanel)Parts(tile).Children[3];
            chips.Children.Clear();
            if (where != Where.Remote) chips.Children.Add(Chip(DriveGlyph, "TextBrush"));
            if (where != Where.Local) chips.Children.Add(Chip(CloudGlyph, "GlyphBrush"));
            if (isPrivate) chips.Children.Add(Chip(LockGlyph, "TextBrush"));
            tile.ToolTip = (where == Where.Local ? "On this PC" : where == Where.Both ? "On this PC and Fireshare" : "Only on Fireshare")
                + (isPrivate ? ", private" : "");
        }

        static Border Chip(string glyph, string brushKey)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DialogShell.Themed(icon, TextBlock.ForegroundProperty, brushKey, Brushes.White);
            var chip = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 6, 0), Child = icon };
            DialogShell.Themed(chip, Border.BackgroundProperty, "OverlayBrush", new SolidColorBrush(Color.FromArgb(200, 40, 40, 46)));
            return chip;
        }

        static Grid Parts(ListBoxItem item) => (Grid)((Grid)item.Content).Children[0];

        static BitmapSource Decode(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.DecodePixelWidth = 640;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not decode a Fireshare thumbnail");
                return null;
            }
        }

        static BitmapSource Thumbnail(string path)
        {
            try
            {
                if (FireshareClient.KindOf(path) == "video") return ShellThumbnail(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 640;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"No thumbnail for {path}");
                return null;
            }
        }

        /// <summary>The frame Explorer shows for a clip; WPF cannot decode video itself.</summary>
        static BitmapSource ShellThumbnail(string path)
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try
            {
                const int ThumbnailOnly = 0x8;
                if (factory.GetImage(new NativeSize { Width = 640, Height = 360 }, ThumbnailOnly, out var hbitmap) != 0) return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(hbitmap); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeSize { public int Width, Height; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);
    }
}
