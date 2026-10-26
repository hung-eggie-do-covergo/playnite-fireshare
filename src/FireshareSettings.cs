using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace FireshareShare
{
    public class FireshareSettings : ObservableObject
    {
        string serverUrl = "";
        string protectedToken = "";
        string capturesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Captures");

        public string ServerUrl { get => serverUrl; set => SetValue(ref serverUrl, value); }
        public string CapturesFolder { get => capturesFolder; set => SetValue(ref capturesFolder, value); }

        // DPAPI, current user: the settings file alone cannot replay the token.
        public string ProtectedToken { get => protectedToken; set => SetValue(ref protectedToken, value); }

        [DontSerialize]
        public string Token
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedToken)) return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser)); }
                catch (Exception) { return ""; }
            }
            set => ProtectedToken = string.IsNullOrEmpty(value) ? "" :
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }

        [DontSerialize]
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrEmpty(ProtectedToken);
    }

    public class FireshareSettingsViewModel : ObservableObject, ISettings
    {
        readonly FireshareSharePlugin plugin;
        FireshareSettings editingClone;
        FireshareSettings settings;

        public FireshareSettings Settings { get => settings; set => SetValue(ref settings, value); }

        public FireshareSettingsViewModel(FireshareSharePlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<FireshareSettings>() ?? new FireshareSettings();
        }

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);

        public void CancelEdit() => Settings = editingClone;

        public void EndEdit() => plugin.SavePluginSettings(Settings);

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.ServerUrl) &&
                !(Uri.TryCreate(Settings.ServerUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")))
                errors.Add("Fireshare URL must start with https:// or http://");
            return errors.Count == 0;
        }
    }
}
