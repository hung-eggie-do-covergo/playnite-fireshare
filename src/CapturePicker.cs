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

namespace FireshareShare
{
    /// <summary>One grid entry: a local file, a Fireshare item, or both once matched.</summary>
    public class Capture
    {
        public string Path;
        public RemoteItem Remote;
        public DateTime Date;
        public string Kind => Path != null ? FireshareClient.KindOf(Path) : Remote.Kind;
    }

    /// <summary>Steam-style capture grid. Mouse and keyboard everywhere, a controller in fullscreen mode.</summary>
    public static class CapturePicker
    {
        static readonly ILogger logger = LogManager.GetLogger();

        // Captures usually sit on a network share; reading more than this per open gets slow.
        const int MaxShown = 60;
        const double TileWidth = 360, TileHeight = 203;

        static readonly string[] Types = FireshareClient.ImageTypes.Concat(FireshareClient.VideoTypes).ToArray();

        static readonly ControlTemplate BareItem = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

        enum Where { Local, Both, Remote }

        public static Capture[] Pick(IPlayniteAPI api, string gameName, string folder, FireshareClient client, MediaIdCache ids)
        {
            if (!Directory.Exists(folder))
            {
                api.Dialogs.ShowErrorMessage($"Captures folder not found: {folder}", "Fireshare Share");
                return new Capture[0];
            }

            var fullscreen = api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            var gameFiles = Find(folder, Prefix(gameName));
            var allFiles = Find(folder, "");
            var showingAll = gameFiles.Count == 0;
            var generation = 0;
            var result = new Capture[0];

            var owner = api.Dialogs.GetCurrentAppWindow();
            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowCloseButton = !fullscreen, ShowMaximizeButton = false, ShowMinimizeButton = false });
            window.Owner = owner;
            window.Title = "Share to Fireshare";

            var list = new ListBox { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
            scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, fullscreen ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
            scroller.SetValue(UIElement.FocusableProperty, false);
            scroller.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroller };
            list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            list.SelectionChanged += (s, e) =>
            {
                foreach (ListBoxItem item in e.AddedItems) Parts(item).Children[2].Visibility = Visibility.Visible;
                foreach (ListBoxItem item in e.RemovedItems) Parts(item).Children[2].Visibility = Visibility.Collapsed;
            };

            var heading = new TextBlock();
            if (fullscreen) heading.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionHeaderText");
            else { heading.FontSize = 20; heading.Margin = new Thickness(0, 0, 0, 12); }

            void Show()
            {
                var gen = ++generation;
                var files = showingAll ? allFiles : gameFiles;
                heading.Text = files.Count == 0 ? "No captures found" : (showingAll ? "All captures" : $"{gameName} captures");
                list.Items.Clear();
                var captures = files.Take(MaxShown).Select(f => new Capture { Path = f, Date = File.GetLastWriteTime(f) }).ToList();
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
                        if (remote == null) continue;

                        string id;
                        try { id = ids.Get(captures[i].Path); }
                        catch (Exception ex) { logger.Warn(ex, $"Could not hash {captures[i].Path}"); continue; }
                        localIds.Add(id);
                        remote.TryGetValue(id, out var match);
                        captures[i].Remote = match;
                        Post(() => SetBadge(tile, match != null ? Where.Both : Where.Local));
                    }
                    if (remote == null) return;

                    foreach (var item in remote.Values.Where(r => !localIds.Contains(r.Id)).OrderByDescending(r => r.Date))
                    {
                        if (gen != generation) return;
                        var capture = new Capture { Remote = item, Date = item.Date };
                        ListBoxItem tile = null;
                        window.Dispatcher.Invoke(() =>
                        {
                            tile = Tile(capture);
                            SetBadge(tile, Where.Remote);
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
                if (gameFiles.Count == 0) return;
                showingAll = !showingAll;
                Show();
            }

            // Playnite raises controller buttons as bubbling KeyDown events (no Preview) carrying a Button property.
            window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) =>
            {
                var button = e.GetType().GetProperty("Button")?.GetValue(e)?.ToString();
                if (button == "A" && Keyboard.FocusedElement is ListBoxItem item) item.IsSelected = !item.IsSelected;
                else if (button == "X" || button == "Start") Confirm();
                else if (button == "Y") SwitchList();
                else if (button == "B" || e.Key == Key.Escape || e.Key == Key.Back) window.Close();
                else return;
                e.Handled = true;
            }), true);

            var content = new DockPanel();
            DockPanel.SetDock(heading, Dock.Top);
            content.Children.Add(heading);
            var footer = fullscreen ? PromptFooter(gameFiles.Count > 0) : ButtonFooter(Confirm, SwitchList, () => window.Close());
            DockPanel.SetDock(footer, Dock.Bottom);
            content.Children.Add(footer);
            list.Margin = new Thickness(10);
            content.Children.Add(list);

            if (fullscreen)
            {
                // Same layout as Playnite's own fullscreen dialogs: a 1920x1080 design scaled to the screen.
                var panel = new Border { Width = 1640, Height = 940, Child = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                panel.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionBorder");
                // Themes size this border for a narrow menu; the grid needs the room.
                panel.MaxWidth = double.PositiveInfinity;
                panel.MaxHeight = double.PositiveInfinity;
                window.Content = new Viewbox { Child = new Grid { Width = 1920, Height = 1080, Children = { panel } } };
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = owner.Left;
                window.Top = owner.Top;
                window.Width = owner.ActualWidth;
                window.Height = owner.ActualHeight;
            }
            else
            {
                content.Margin = new Thickness(20);
                window.Content = content;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Width = 1240;
                window.Height = 780;
            }

            Show();
            window.ShowDialog();
            generation++;
            return result;
        }

        static FrameworkElement PromptFooter(bool canSwitch)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(Prompt("ButtonPromptA", "Select"));
            row.Children.Add(Prompt("ButtonPromptX", "Share"));
            if (canSwitch) row.Children.Add(Prompt("ButtonPromptY", "Switch list"));
            row.Children.Add(Prompt("ButtonPromptB", "Back"));
            var footer = new Border { Child = row };
            footer.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionFooterBorder");
            return footer;
        }

        static FrameworkElement Prompt(string icon, string label)
        {
            var glyph = new ContentControl { Height = 26, Focusable = false, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(ContentControl.ContentTemplateProperty, icon);
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(FrameworkElement.StyleProperty, "TextBlockBaseStyle");
            var prompt = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 24, 0) };
            prompt.Children.Add(glyph);
            prompt.Children.Add(text);
            return prompt;
        }

        static FrameworkElement ButtonFooter(Action share, Action switchList, Action cancel)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            void Add(string label, Action action)
            {
                var button = new Button { Content = label, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
                button.Click += (s, e) => action();
                row.Children.Add(button);
            }
            Add("Share selected", share);
            Add("Switch list", switchList);
            Add("Cancel", cancel);
            return row;
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
            var grid = new Grid { Width = TileWidth, Height = TileHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)) };
            grid.Children.Add(new Image { Stretch = Stretch.UniformToFill });
            grid.Children.Add(new TextBlock
            {
                // A midnight Fireshare date carries no time; showing 12:00 AM would be invented.
                Text = (video ? "▶  " : "") + capture.Date.ToString(capture.Path == null && capture.Date.TimeOfDay == TimeSpan.Zero ? "d" : "g"),
                Foreground = Brushes.White,
                FontSize = 16,
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            grid.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(26, 159, 255)),
                Visibility = Visibility.Collapsed,
                Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Margin = new Thickness(10), Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Child = new TextBlock { Foreground = Brushes.White, FontSize = 14 },
            });

            var frame = new Border { BorderThickness = new Thickness(4), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(4), Child = grid };
            var item = new ListBoxItem { Content = frame, Tag = capture, Margin = new Thickness(6), Template = BareItem, FocusVisualStyle = null };
            item.GotKeyboardFocus += (s, e) => frame.BorderBrush = Brushes.White;
            item.LostKeyboardFocus += (s, e) => frame.BorderBrush = Brushes.Transparent;
            return item;
        }

        static void SetThumb(ListBoxItem tile, BitmapSource thumb) => ((Image)Parts(tile).Children[0]).Source = thumb;

        static void SetBadge(ListBoxItem tile, Where where)
        {
            var pill = (Border)Parts(tile).Children[3];
            ((TextBlock)pill.Child).Text = where == Where.Local ? "Local" : where == Where.Both ? "Local + Fireshare" : "Fireshare";
            pill.Background = new SolidColorBrush(where == Where.Local ? Color.FromArgb(200, 70, 70, 76)
                : where == Where.Both ? Color.FromRgb(46, 158, 91) : Color.FromRgb(139, 92, 246));
            pill.Visibility = Visibility.Visible;
        }

        static Grid Parts(ListBoxItem item) => (Grid)((Border)item.Content).Child;

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
