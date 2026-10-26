using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;

namespace FireshareShare
{
    /// <summary>One footer entry: a controller prompt in fullscreen, a button on the desktop when it has an action.</summary>
    public class Hint
    {
        public string Prompt;
        public string Label;
        public Action Action;

        public Hint(string prompt, string label, Action action = null)
        {
            Prompt = prompt;
            Label = label;
            Action = action;
        }
    }

    /// <summary>The window around the plugin's dialogs: Playnite's fullscreen dialog layout, or a plain window on the desktop.</summary>
    public static class DialogShell
    {
        public static bool IsFullscreen(IPlayniteAPI api) => api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;

        /// <summary>Playnite raises controller buttons as bubbling KeyDown events (no Preview) carrying a Button property.</summary>
        public static string ControllerButton(KeyEventArgs e) => e.GetType().GetProperty("Button")?.GetValue(e)?.ToString();

        public static TextBlock Heading(IPlayniteAPI api)
        {
            var heading = new TextBlock();
            if (IsFullscreen(api)) heading.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionHeaderText");
            else { heading.FontSize = 20; heading.Margin = new Thickness(0, 0, 0, 12); }
            return heading;
        }

        public static FrameworkElement Footer(IPlayniteAPI api, params Hint[] hints)
        {
            if (!IsFullscreen(api))
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                foreach (var hint in hints)
                {
                    if (hint.Action == null) continue;
                    var button = new Button { Content = hint.Label, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
                    var action = hint.Action;
                    button.Click += (s, e) => action();
                    buttons.Children.Add(button);
                }
                return buttons;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var hint in hints)
            {
                var glyph = new ContentControl { Height = 26, Focusable = false, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(ContentControl.ContentTemplateProperty, hint.Prompt);
                var text = new TextBlock { Text = hint.Label, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(FrameworkElement.StyleProperty, "TextBlockBaseStyle");
                var prompt = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 24, 0) };
                prompt.Children.Add(glyph);
                prompt.Children.Add(text);
                row.Children.Add(prompt);
            }
            var footer = new Border { Child = row };
            footer.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionFooterBorder");
            return footer;
        }

        /// <param name="panel">Fullscreen panel size, in Playnite's 1920x1080 design units.</param>
        /// <param name="desktop">Window size on the desktop.</param>
        public static Window Create(IPlayniteAPI api, string title, TextBlock heading, FrameworkElement body, FrameworkElement footer, Size panel, Size desktop)
        {
            var fullscreen = IsFullscreen(api);
            var owner = api.Dialogs.GetCurrentAppWindow();
            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowCloseButton = !fullscreen, ShowMaximizeButton = false, ShowMinimizeButton = false });
            window.Owner = owner;
            window.Title = title;

            var content = new DockPanel();
            DockPanel.SetDock(heading, Dock.Top);
            content.Children.Add(heading);
            DockPanel.SetDock(footer, Dock.Bottom);
            content.Children.Add(footer);
            content.Children.Add(body);

            if (fullscreen)
            {
                // Same layout as Playnite's own fullscreen dialogs: a 1920x1080 design scaled to the screen.
                var border = new Border { Width = panel.Width, Height = panel.Height, Child = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                border.SetResourceReference(FrameworkElement.StyleProperty, "ItemSelectionBorder");
                // Themes size this border for a narrow menu; the dialogs need the room.
                border.MaxWidth = double.PositiveInfinity;
                border.MaxHeight = double.PositiveInfinity;
                window.Content = new Viewbox { Child = new Grid { Width = 1920, Height = 1080, Children = { border } } };
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
                window.Width = desktop.Width;
                window.Height = desktop.Height;
            }
            return window;
        }
    }
}
