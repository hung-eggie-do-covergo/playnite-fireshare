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
    /// <summary>Steam-style capture grid. Mouse and keyboard everywhere, a controller in fullscreen mode.</summary>
    public static class CapturePicker
    {
        static readonly ILogger logger = LogManager.GetLogger();

        // Captures usually sit on a network share; reading more than this per open gets slow.
        const int MaxShown = 60;
        const double TileWidth = 360, TileHeight = 203;

        static readonly string[] Types = FireshareClient.ImageTypes.Concat(FireshareClient.VideoTypes).ToArray();

        static readonly ControlTemplate BareItem = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

        public static string[] Pick(IPlayniteAPI api, string gameName, string folder)
        {
            if (!Directory.Exists(folder))
            {
                api.Dialogs.ShowErrorMessage($"Captures folder not found: {folder}", "Fireshare Share");
                return new string[0];
            }

            var fullscreen = api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            var gameFiles = Find(folder, Prefix(gameName));
            var allFiles = Find(folder, "");
            var showingAll = gameFiles.Count == 0;
            var generation = 0;
            string[] result = new string[0];

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
                var tiles = files.Take(MaxShown).Select(Tile).ToList();
                tiles.ForEach(t => list.Items.Add(t));
                if (tiles.Count > 0) window.Dispatcher.BeginInvoke(new Action(() => tiles[0].Focus()), System.Windows.Threading.DispatcherPriority.Loaded);

                // WPF decodes images reliably only on STA threads; a newer list stops an older load.
                // The loader touches only paths: the tiles belong to the UI thread.
                var paths = tiles.Select(t => (string)t.Tag).ToList();
                var loader = new Thread(() =>
                {
                    for (var i = 0; i < paths.Count; i++)
                    {
                        if (gen != generation) return;
                        var thumb = Thumbnail(paths[i]);
                        var tile = tiles[i];
                        if (thumb != null) window.Dispatcher.BeginInvoke(new Action(() => ((Image)Parts(tile).Children[0]).Source = thumb));
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
                result = picked.Select(i => (string)i.Tag).ToArray();
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

        static ListBoxItem Tile(string path)
        {
            var video = FireshareClient.KindOf(path) == "video";
            var grid = new Grid { Width = TileWidth, Height = TileHeight, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)) };
            grid.Children.Add(new Image { Stretch = Stretch.UniformToFill });
            grid.Children.Add(new TextBlock
            {
                Text = (video ? "▶  " : "") + File.GetLastWriteTime(path).ToString("g"),
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

            var frame = new Border { BorderThickness = new Thickness(4), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(4), Child = grid };
            var item = new ListBoxItem { Content = frame, Tag = path, Margin = new Thickness(6), Template = BareItem, FocusVisualStyle = null };
            item.GotKeyboardFocus += (s, e) => frame.BorderBrush = Brushes.White;
            item.LostKeyboardFocus += (s, e) => frame.BorderBrush = Brushes.Transparent;
            return item;
        }

        static Grid Parts(ListBoxItem item) => (Grid)((Border)item.Content).Child;

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
