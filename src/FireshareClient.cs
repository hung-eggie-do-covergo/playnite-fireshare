using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireshareShare
{
    /// <summary>Talks to Fireshare's upload-token API. No Playnite types, so it can be tested alone.</summary>
    public class FireshareClient
    {
        public static readonly string[] VideoTypes = { "mp4", "m4v", "mov", "webm" };
        public static readonly string[] ImageTypes = { "jpg", "jpeg", "png", "webp", "gif" };

        const int ChunkSize = 8 * 1024 * 1024;
        const int IdBytes = 16 * 1024 * 1024;

        // UseCookies off: on .NET Framework the cookie jar silently drops the Cookie header carrying the session.
        static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };

        readonly Uri baseUri;
        readonly string token;
        readonly string session;

        /// <param name="session">Cookie header from <see cref="LoginAsync"/>; adds private items to listings.</param>
        public FireshareClient(string serverUrl, string token, string session = null)
        {
            baseUri = new Uri(serverUrl.TrimEnd('/') + "/");
            this.token = token;
            this.session = string.IsNullOrEmpty(session) ? null : session;
        }

        /// <summary>Signs in as a Fireshare user. Returns the session cookie, or a pending login when a TOTP code is needed.</summary>
        public static async Task<LoginResult> LoginAsync(string serverUrl, string username, string password)
        {
            var pending = new LoginResult { BaseUri = new Uri(serverUrl.TrimEnd('/') + "/"), Cookies = new CookieContainer() };
            var json = "{\"username\":" + JsonString(username) + ",\"password\":" + JsonString(password) + "}";
            return await pending.PostAsync("api/login", json);
        }

        public static Task<LoginResult> VerifyMfaAsync(LoginResult pending, string code)
            => pending.PostAsync("api/login/mfa", "{\"code\":" + JsonString(code.Trim()) + "}");

        /// <summary>Who the stored session belongs to, or null when it has expired or was never set.</summary>
        public async Task<SessionInfo> SessionAsync(CancellationToken ct = default)
        {
            if (session == null) return null;
            var info = await GetAsync<SessionInfo>("api/loggedin", ct);
            return info.Authenticated ? info : null;
        }

        static string JsonString(string value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(string)).WriteObject(ms, value ?? "");
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static string KindOf(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (VideoTypes.Contains(ext)) return "video";
            if (ImageTypes.Contains(ext)) return "image";
            return null;
        }

        /// <summary>Fireshare's media id: xxh3-128 of the first 16 MB, as lowercase hex.</summary>
        public static string MediaId(string path)
        {
            var buffer = new byte[IdBytes];
            int read = 0, n;
            using (var f = File.OpenRead(path))
                while (read < buffer.Length && (n = f.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            return Xxh3.Hash128Hex(buffer, read);
        }

        /// <summary>Fireshare only links games it already has; compare names ignoring case, symbols and spacing.</summary>
        public static int? MatchGame(string name, IEnumerable<GameRef> games) => MatchGameRef(name, games)?.Id;

        public static GameRef MatchGameRef(string name, IEnumerable<GameRef> games)
        {
            var key = Key(name);
            return key.Length == 0 ? null : games.FirstOrDefault(g => Key(g.Name) == key);
        }

        /// <summary>The Fireshare game for a capture: the longest game name its filename starts with, else the first candidate Fireshare knows.</summary>
        /// <remarks>Filename first, because candidates can be wrong when one game name prefixes another.</remarks>
        public static int? MatchCapture(IEnumerable<string> candidates, string path, IList<GameRef> games)
        {
            var file = Key(Path.GetFileNameWithoutExtension((path ?? "").Split('\\', '/').Last()));
            var byName = games.Where(g => Key(g.Name).Length > 0 && file.StartsWith(Key(g.Name)))
                              .OrderByDescending(g => Key(g.Name).Length)
                              .FirstOrDefault();
            return byName?.Id ?? candidates.Select(n => MatchGameRef(n, games)).FirstOrDefault(g => g != null)?.Id;
        }

        static string Key(string n) => new string((n ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        public async Task<TokenCheck> CheckAsync(CancellationToken ct = default)
            => await GetAsync<TokenCheck>("api/upload/token", ct);

        public async Task<List<GameRef>> GamesAsync(CancellationToken ct = default)
            => (await GetAsync<UploadOptions>("api/upload/token/options", ct)).Games ?? new List<GameRef>();

        /// <summary>The game's public images and clips on Fireshare; empty when Fireshare has no such game.</summary>
        public async Task<List<RemoteItem>> GameMediaAsync(string gameName, CancellationToken ct = default)
        {
            var items = new List<RemoteItem>();
            var game = MatchGameRef(gameName, await GamesAsync(ct));
            if (game?.SteamGridDbId == null) return items;

            // The token is not a session: without a signed-in cookie these list public items only.
            foreach (var media in await GetListAsync<MediaJson>($"api/games/{game.SteamGridDbId}/images", ct))
                items.Add(media.ToRemote("image"));
            foreach (var media in await GetListAsync<MediaJson>($"api/games/{game.SteamGridDbId}/videos", ct))
                items.Add(media.ToRemote("video"));
            return items;
        }

        /// <summary>One item by id, whether or not it is linked to a game; null when Fireshare does not have it.</summary>
        public async Task<RemoteItem> LookupAsync(string kind, string id, CancellationToken ct = default)
        {
            using (var res = await SendAsync(HttpMethod.Get, $"api/{kind}/details/{id}", null, ct))
            {
                // Fireshare answers 401 for a private image when the caller is not signed in.
                if (res.StatusCode == HttpStatusCode.Unauthorized) return new RemoteItem { Id = id, Kind = kind, Private = true };
                var body = res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync() : "";
                return string.IsNullOrWhiteSpace(body) ? null : Parse<MediaJson>(body).ToRemote(kind);
            }
        }

        /// <summary>SteamGridDB search through Fireshare, the user's custom games first. Needs a signed-in session.</summary>
        public async Task<List<GridGame>> SearchGamesAsync(string query, CancellationToken ct = default)
            => await GetAsync<List<GridGame>>("api/steamgrid/search?query=" + Uri.EscapeDataString(query), ct) ?? new List<GridGame>();

        /// <summary>Adds a SteamGridDB game to Fireshare, which also fetches its artwork. An existing one is returned as is.</summary>
        public async Task<GameRef> CreateGameAsync(GridGame game, CancellationToken ct = default)
        {
            var body = "{\"steamgriddb_id\":" + game.Id + ",\"name\":" + JsonString(game.Name)
                + ",\"release_date\":" + (game.Year.HasValue ? JsonString(game.ReleaseDate) : "null") + "}";
            using (var res = await SendAsync(HttpMethod.Post, "api/games", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct))
            {
                var text = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) throw Fail(res, text);
                return Parse<GameRef>(text);
            }
        }

        /// <summary>The search result to use without asking: the only exact-name match, or the only one of those from the same year.</summary>
        public static GridGame PickGame(IList<GridGame> results, string name, int? year)
        {
            var exact = results.Where(r => Key(r.Name) == Key(name)).ToList();
            if (exact.Count == 1) return exact[0];
            var sameYear = exact.Where(r => year.HasValue && r.Year == year).ToList();
            return sameYear.Count == 1 ? sameYear[0] : null;
        }

        /// <summary>Results in the order to offer them: exact name, then same year, then verified.</summary>
        public static List<GridGame> RankGames(IEnumerable<GridGame> results, string name, int? year)
            => results.OrderByDescending(r => Key(r.Name) == Key(name))
                      .ThenByDescending(r => year.HasValue && r.Year == year)
                      .ThenByDescending(r => r.Verified)
                      .ToList();

        public async Task<string> LinkAsync(string kind, string id, CancellationToken ct = default)
            => await LinkBaseAsync(ct) + (kind == "video" ? "/w/" : "/i/") + id;

        public async Task<byte[]> ThumbnailAsync(RemoteItem item, CancellationToken ct = default)
        {
            var path = item.Kind == "video" ? "api/video/poster?id=" : "api/image/thumbnail?id=";
            using (var res = await SendAsync(HttpMethod.Get, path + item.Id, null, ct))
                return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null;
        }

        /// <summary>Uploads one file unless Fireshare already has it, and returns its share link.</summary>
        public async Task<ShareResult> ShareAsync(string path, int? gameId, CancellationToken ct = default)
        {
            var kind = KindOf(path) ?? throw new InvalidOperationException("Fireshare does not accept " + Path.GetExtension(path) + " files.");
            var size = new FileInfo(path).Length;
            if (size == 0) throw new InvalidOperationException("The file is empty.");

            var id = MediaId(path);
            var link = await LinkAsync(kind, id, ct);

            var exists = await GetAsync<ExistsResponse>($"api/upload/token/exists?{kind}_id={id}", ct);
            if (exists.Exists) return new ShareResult { Link = link, AlreadyThere = true };

            var total = (int)Math.Max(1, (size + ChunkSize - 1) / ChunkSize);
            // Unique per attempt, so a retry never mixes with a lost earlier set.
            var checkSum = id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var buffer = new byte[ChunkSize];

            using (var f = File.OpenRead(path))
            {
                for (var part = 1; part <= total; part++)
                {
                    int len = 0, n;
                    while (len < buffer.Length && (n = f.Read(buffer, len, buffer.Length - len)) > 0) len += n;

                    var form = new MultipartFormDataContent
                    {
                        { new ByteArrayContent(buffer, 0, len), "blob", "blob" },
                        { new StringContent(part.ToString()), "chunkPart" },
                        { new StringContent(total.ToString()), "totalChunks" },
                        { new StringContent(checkSum), "checkSum" },
                        { new StringContent(Path.GetFileName(path)), "fileName" },
                        { new StringContent(size.ToString()), "fileSize" },
                    };
                    if (gameId.HasValue) form.Add(new StringContent(gameId.Value.ToString()), "game_id");

                    using (var res = await SendAsync(HttpMethod.Post, "api/upload/token/chunked", form, ct))
                    {
                        var body = await res.Content.ReadAsStringAsync();
                        if (res.StatusCode == HttpStatusCode.Conflict) return new ShareResult { Link = link, AlreadyThere = true };
                        if (res.StatusCode == HttpStatusCode.Created) return new ShareResult { Link = link };
                        if (res.StatusCode != HttpStatusCode.Accepted) throw Fail(res, body);
                        // Fewer parts held than sent means the server dropped the set.
                        if (Parse<PartialResponse>(body).Received < part)
                            throw new InvalidOperationException("Fireshare lost the partial upload. Try again.");
                    }
                }
            }
            throw new InvalidOperationException("Fireshare never confirmed the upload.");
        }

        string linkBase;

        async Task<string> LinkBaseAsync(CancellationToken ct)
        {
            if (linkBase != null) return linkBase;
            var domain = (await GetAsync<PublicConfig>("api/config", ct)).ShareableLinkDomain;
            return linkBase = string.IsNullOrWhiteSpace(domain) ? baseUri.ToString().TrimEnd('/') : domain.TrimEnd('/');
        }

        async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            using (var res = await SendAsync(HttpMethod.Get, path, null, ct))
            {
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) throw Fail(res, body);
                return Parse<T>(body);
            }
        }

        async Task<List<T>> GetListAsync<T>(string path, CancellationToken ct)
        {
            using (var res = await SendAsync(HttpMethod.Get, path, null, ct))
            {
                var body = await res.Content.ReadAsStringAsync();
                if (res.StatusCode == HttpStatusCode.NotFound) return new List<T>();
                if (!res.IsSuccessStatusCode) throw Fail(res, body);
                return Parse<List<T>>(body) ?? new List<T>();
            }
        }

        Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct)
        {
            var req = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (session != null) req.Headers.Add("Cookie", session);
            return http.SendAsync(req, ct);
        }

        static Exception Fail(HttpResponseMessage res, string body)
        {
            switch (res.StatusCode)
            {
                case HttpStatusCode.Unauthorized: return new InvalidOperationException("Fireshare rejected the upload token.");
                case (HttpStatusCode)429: return new InvalidOperationException("Too many failed token attempts. Wait a few minutes.");
                case HttpStatusCode.ServiceUnavailable: return new InvalidOperationException("This Fireshare instance has images turned off.");
                default:
                    var detail = body.Length > 200 ? body.Substring(0, 200) : body;
                    return new InvalidOperationException($"Fireshare answered {(int)res.StatusCode}: {detail}");
            }
        }

        static T Parse<T>(string json)
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }
    }

    public class ShareResult
    {
        public string Link;
        public bool AlreadyThere;
    }

    [DataContract] public class TokenCheck
    {
        [DataMember(Name = "username")] public string Username;
        [DataMember(Name = "images_enabled")] public bool ImagesEnabled;
    }

    [DataContract] public class GameRef
    {
        [DataMember(Name = "id")] public int Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "steamgriddb_id")] public int? SteamGridDbId;
    }

    [DataContract] public class GridGame
    {
        [DataMember(Name = "id")] public int Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "release_date")] public long? ReleaseDateUnix;
        [DataMember(Name = "verified")] public bool Verified;

        public int? Year => ReleaseDateUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ReleaseDateUnix.Value).Year : (int?)null;
        public string ReleaseDate => ReleaseDateUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ReleaseDateUnix.Value).ToString("yyyy-MM-dd") : null;
    }

    public class RemoteItem
    {
        public string Id;
        public string Kind;
        public DateTime Date;
        public string Title;
        public bool Private;
    }

    public class LoginResult
    {
        internal Uri BaseUri;
        internal CookieContainer Cookies;
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>The Cookie header to store once signed in.</summary>
        public string Session;
        public bool MfaRequired;
        public string Error;

        internal async Task<LoginResult> PostAsync(string path, string json)
        {
            // Its own cookie jar: the MFA step must carry the half-finished login from the first request.
            using (var http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies }) { Timeout = Timeout })
            using (var res = await http.PostAsync(new Uri(BaseUri, path), new StringContent(json, System.Text.Encoding.UTF8, "application/json")))
            {
                var body = await res.Content.ReadAsStringAsync();
                var result = new LoginResult { BaseUri = BaseUri, Cookies = Cookies };
                if (res.IsSuccessStatusCode && body.Contains("\"mfa_required\"")) result.MfaRequired = true;
                else if (res.IsSuccessStatusCode) result.Session = Cookies.GetCookieHeader(BaseUri);
                else result.Error = (int)res.StatusCode == 429 ? "Too many attempts. Wait a few minutes." : (body.Length > 0 && body.Length < 200 ? body : $"Sign-in failed ({(int)res.StatusCode}).");
                return result;
            }
        }
    }

    [DataContract] public class SessionInfo
    {
        [DataMember(Name = "authenticated")] public bool Authenticated;
        [DataMember(Name = "username")] public string Username;
        [DataMember(Name = "admin")] public bool Admin;
        [DataMember(Name = "permissions")] public List<string> Permissions;

        public bool SeesPrivate => Admin || (Permissions?.Contains("view_private") ?? false);
    }

    [DataContract] class MediaJson
    {
        [DataMember(Name = "image_id")] public string ImageId;
        [DataMember(Name = "video_id")] public string VideoId;
        [DataMember(Name = "recorded_at")] public string RecordedAt;
        [DataMember(Name = "created_at")] public string CreatedAt;
        [DataMember(Name = "info")] public MediaInfoJson Info;

        public RemoteItem ToRemote(string kind)
        {
            // Fireshare usually dates captures from the filename, so the timestamp is local, often midnight.
            DateTime.TryParse(RecordedAt ?? CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date);
            return new RemoteItem { Id = ImageId ?? VideoId, Kind = kind, Date = date, Title = Info?.Title, Private = Info?.Private ?? false };
        }
    }

    [DataContract] class MediaInfoJson
    {
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "private")] public bool? Private;
    }

    [DataContract] class UploadOptions
    {
        [DataMember(Name = "games")] public List<GameRef> Games;
    }

    [DataContract] class ExistsResponse
    {
        [DataMember(Name = "exists")] public bool Exists;
    }

    [DataContract] class PartialResponse
    {
        [DataMember(Name = "received")] public int Received;
    }

    [DataContract] class PublicConfig
    {
        [DataMember(Name = "shareable_link_domain")] public string ShareableLinkDomain;
    }
}
