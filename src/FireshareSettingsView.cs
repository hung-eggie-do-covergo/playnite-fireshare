using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FireshareShare
{
    // Built in code rather than XAML so the plugin compiles without the Windows desktop SDK.
    public class FireshareSettingsView : UserControl
    {
        readonly FireshareSettingsViewModel vm;
        readonly TextBlock status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        public FireshareSettingsView(FireshareSettingsViewModel vm)
        {
            this.vm = vm;
            DataContext = vm;

            var token = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
            token.Loaded += (s, e) => token.Password = vm.Settings.Token;
            token.PasswordChanged += (s, e) => vm.Settings.Token = token.Password;

            var test = new Button { Content = "Test connection", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
            test.Click += async (s, e) =>
            {
                test.IsEnabled = false;
                status.Text = "Checking…";
                try
                {
                    var check = await new FireshareClient(vm.Settings.ServerUrl, vm.Settings.Token).CheckAsync();
                    status.Text = $"Connected as {check.Username}." + (check.ImagesEnabled ? "" : " Images are turned off on this instance, so only clips can be shared.");
                }
                catch (Exception ex) { status.Text = ex.Message; }
                finally { test.IsEnabled = true; }
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(Label("Fireshare URL"));
            panel.Children.Add(Bound("Settings.ServerUrl"));
            panel.Children.Add(Label("Upload token (Fireshare → Settings → Security → Upload Tokens)"));
            panel.Children.Add(token);
            panel.Children.Add(Label("Captures folder"));
            panel.Children.Add(Bound("Settings.CapturesFolder"));
            panel.Children.Add(test);
            panel.Children.Add(status);
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 16, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Text = "Fireshare makes new uploads private by default, and private links ask viewers to log in. " +
                       "To share with anyone, set the default privacy for videos and images to public in Fireshare's settings."
            });
            Content = panel;
        }

        static TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };

        static TextBox Bound(string path)
        {
            var box = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            box.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return box;
        }
    }
}
