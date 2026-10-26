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
                    var client = new FireshareClient(vm.Settings.ServerUrl, vm.Settings.Token, vm.Settings.Session);
                    var check = await client.CheckAsync();
                    status.Text = $"Connected as {check.Username}." + (check.ImagesEnabled ? "" : " Images are turned off on this instance, so only clips can be shared.");
                    if (!string.IsNullOrEmpty(vm.Settings.ProtectedSession))
                        status.Text += (await client.SessionAsync()) == null ? " The sign-in has expired; sign in again to see private items." : $" Signed in as {vm.Settings.SessionUser}.";
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
            AddSignIn(panel);
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

        void AddSignIn(Panel panel)
        {
            var user = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            var password = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
            var codeLabel = Label("Authenticator code");
            var code = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            var signIn = new Button { Content = "Sign in", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            var signOut = new Button { Content = "Sign out", Padding = new Thickness(12, 4, 12, 4) };
            var signStatus = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            LoginResult pending = null;

            void ShowCode(bool show) => codeLabel.Visibility = code.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            void Refresh()
            {
                var signedIn = !string.IsNullOrEmpty(vm.Settings.ProtectedSession);
                signStatus.Text = signedIn ? $"Signed in as {vm.Settings.SessionUser}." : "Not signed in. The capture grid shows public Fireshare items only.";
                signOut.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            }

            signIn.Click += async (s, e) =>
            {
                signIn.IsEnabled = false;
                signStatus.Text = "Signing in…";
                try
                {
                    var result = pending != null
                        ? await FireshareClient.VerifyMfaAsync(pending, code.Text)
                        : await FireshareClient.LoginAsync(vm.Settings.ServerUrl, user.Text, password.Password);
                    pending = null;
                    ShowCode(false);
                    if (result.MfaRequired)
                    {
                        pending = result;
                        ShowCode(true);
                        signStatus.Text = "Enter the code from your authenticator app, then click Sign in.";
                        return;
                    }
                    if (result.Session == null) { signStatus.Text = result.Error; return; }

                    var info = await new FireshareClient(vm.Settings.ServerUrl, vm.Settings.Token, result.Session).SessionAsync();
                    if (info == null) { signStatus.Text = "Fireshare accepted the password but not the session. Check that the URL uses https."; return; }
                    vm.Settings.Session = result.Session;
                    vm.Settings.SessionUser = info.Username;
                    password.Clear();
                    Refresh();
                    if (!info.SeesPrivate) signStatus.Text += " This account cannot view private items, so the grid still shows public ones only.";
                }
                catch (Exception ex) { signStatus.Text = ex.Message; }
                finally { signIn.IsEnabled = true; }
            };
            signOut.Click += (s, e) =>
            {
                vm.Settings.Session = "";
                vm.Settings.SessionUser = "";
                Refresh();
            };

            ShowCode(false);
            Refresh();

            panel.Children.Add(new TextBlock { Text = "Private items (optional)", FontSize = 16, Margin = new Thickness(0, 24, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "Sign in as a Fireshare user to see your private captures in the grid. Only the session is kept, encrypted; the password is not stored.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(Label("Username"));
            panel.Children.Add(user);
            panel.Children.Add(Label("Password"));
            panel.Children.Add(password);
            panel.Children.Add(codeLabel);
            panel.Children.Add(code);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(signIn);
            buttons.Children.Add(signOut);
            panel.Children.Add(buttons);
            panel.Children.Add(signStatus);
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
