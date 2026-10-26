using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK;

namespace FireshareShare
{
    /// <summary>Asks which SteamGridDB game a Playnite game is, when the name and year do not settle it.</summary>
    public static class GameChooser
    {
        static readonly ControlTemplate BareItem = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

        /// <summary>The chosen game, or null to upload without one.</summary>
        public static GridGame Choose(IPlayniteAPI api, string gameName, int? year, List<GridGame> ranked)
        {
            var fullscreen = DialogShell.IsFullscreen(api);
            GridGame result = null;
            Window window = null;

            var heading = DialogShell.Heading(api);
            heading.Text = ranked.Count == 0
                ? $"SteamGridDB has no game called {gameName}"
                : $"Which game is {gameName}{(year.HasValue ? $" ({year})" : "")}?";

            var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(10) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            foreach (var game in ranked) list.Items.Add(Row(game, fullscreen));

            void Choose(ListBoxItem item)
            {
                if (item == null) return;
                result = (GridGame)item.Tag;
                window.Close();
            }

            var hints = new List<Hint>();
            if (ranked.Count > 0) hints.Add(new Hint("ButtonPromptA", "Use this game", () => Choose(list.SelectedItem as ListBoxItem ?? Keyboard.FocusedElement as ListBoxItem)));
            hints.Add(new Hint("ButtonPromptB", "Upload without a game", () => window.Close()));
            window = DialogShell.Create(api, "Fireshare game", heading, list, DialogShell.Footer(api, hints.ToArray()), new Size(1100, 760), new Size(720, 560));

            list.MouseDoubleClick += (s, e) => Choose(list.SelectedItem as ListBoxItem);
            window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) =>
            {
                var button = DialogShell.ControllerButton(e);
                if (button == "A" || (button == null && e.Key == Key.Enter)) Choose(Keyboard.FocusedElement as ListBoxItem);
                else if (button == "B" || e.Key == Key.Escape || e.Key == Key.Back) window.Close();
                else return;
                e.Handled = true;
            }), true);
            if (list.Items.Count > 0)
                window.Dispatcher.BeginInvoke(new System.Action(() => ((ListBoxItem)list.Items[0]).Focus()), System.Windows.Threading.DispatcherPriority.Loaded);

            window.ShowDialog();
            return result;
        }

        static ListBoxItem Row(GridGame game, bool fullscreen)
        {
            var name = new TextBlock { Text = game.Name, FontSize = fullscreen ? 26 : 16 };
            var details = new TextBlock
            {
                Text = string.Join("  ·  ", new[] { game.Year?.ToString(), game.Verified ? "Verified" : null }.Where(t => t != null)),
                Opacity = 0.7,
                FontSize = fullscreen ? 18 : 12,
            };
            if (fullscreen)
            {
                name.SetResourceReference(FrameworkElement.StyleProperty, "TextBlockBaseStyle");
                details.SetResourceReference(FrameworkElement.StyleProperty, "TextBlockBaseStyle");
            }
            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(details);

            var frame = new Border { BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 8, 14, 8), Child = stack };
            var item = new ListBoxItem { Content = frame, Tag = game, Margin = new Thickness(4), Template = BareItem, FocusVisualStyle = null };
            item.GotKeyboardFocus += (s, e) => { frame.BorderBrush = Brushes.White; item.IsSelected = true; };
            item.LostKeyboardFocus += (s, e) => frame.BorderBrush = Brushes.Transparent;
            return item;
        }
    }
}
