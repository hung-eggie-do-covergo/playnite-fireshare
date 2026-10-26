using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FireshareShare;

// Reference ids from Python: xxhash.xxh3_128_hexdigest(data[:16 MiB]), as Fireshare computes them.
var small = Path.GetTempFileName();
File.WriteAllText(small, "hello fireshare");
Assert(FireshareClient.MediaId(small) == "851938af740afbfa0ed990202c4588e7", "small file id");

var big = Path.GetTempFileName();
File.WriteAllBytes(big, Enumerable.Range(0, 20 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray());
Assert(FireshareClient.MediaId(big) == "3d36a2c3df632134658eb3c8c8b637b8", "id covers only the first 16 MiB");

// Every length class of XXH3-128 (0-16, 17-128, 129-240, long), against Python's xxhash.
var lens = Enumerable.Range(0, 301).Concat(new[] { 511, 512, 1023, 1024, 1025, 4096, 10000, 65536, 1048577 }).ToArray();
var data = Enumerable.Range(0, lens.Max()).Select(i => (byte)((i * 31 + 7) % 256)).ToArray();
var all = string.Concat(lens.Select(n => Xxh3.Hash128Hex(data, n)));
var digest = string.Concat(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)).Select(b => b.ToString("x2")));
Assert(digest == "b2185d9b3b1b3c33da6c5fbc81fedc871d42519b58effeec06d61206bb876387", "xxh3-128 matches the reference at every length");

var games = new[] { new GameRef { Id = 7, Name = "ELDEN RING™" }, new GameRef { Id = 9, Name = "Baldur's Gate 3" } };
Assert(FireshareClient.MatchGame("Elden Ring", games) == 7, "match ignores case and symbols");
Assert(FireshareClient.MatchGame("Baldurs Gate 3", games) == 9, "match ignores punctuation");
Assert(FireshareClient.MatchGame("Hades", games) == null, "unknown game is null");

var fsGames = new[] { new GameRef { Id = 1, Name = "Resident Evil" }, new GameRef { Id = 4, Name = "Resident Evil 4" }, new GameRef { Id = 7, Name = "007 First Light" } };
Assert(FireshareClient.MatchCapture(new[] { "Resident Evil Village" }, @"Z:\c\Resident Evil 4 9_26_2026 7_54_21 PM.png", fsGames) == 4, "longest filename prefix wins");
Assert(FireshareClient.MatchCapture(new[] { "Resident Evil" }, @"Z:\c\Resident Evil 4 9_26_2026.png", fsGames) == 4, "filename beats a shorter candidate");
Assert(FireshareClient.MatchCapture(new[] { "Unknown", "007 First Light" }, @"Z:\c\20260920_1.png", fsGames) == 7, "candidates cover filenames without the game");
Assert(FireshareClient.MatchCapture(new string[0], @"Z:\c\PRAGMATA 10_18_2026.png", fsGames) == null, "no match leaves the game unset");

// 2005 and 2023 releases share the name; the year decides, and without one nothing is guessed.
var re4 = new[] { new GridGame { Id = 10, Name = "Resident Evil 4", ReleaseDateUnix = 1105228800 }, new GridGame { Id = 11, Name = "Resident Evil 4", ReleaseDateUnix = 1679616000 }, new GridGame { Id = 12, Name = "Resident Evil 4 VR" } };
Assert(FireshareClient.PickGame(re4, "Resident Evil 4", 2023)?.Id == 11, "same name, the year decides");
Assert(FireshareClient.PickGame(re4, "Resident Evil 4", null) == null, "same name without a year is not guessed");
Assert(FireshareClient.PickGame(re4, "PRAGMATA", 2026) == null, "no exact name is not guessed");
Assert(FireshareClient.PickGame(new[] { new GridGame { Id = 5, Name = "PRAGMATA" } }, "Pragmata", null)?.Id == 5, "a single exact name is used");
Assert(FireshareClient.RankGames(re4, "Resident Evil 4", 2023)[0].Id == 11, "the likeliest match is offered first");

Assert(FireshareClient.KindOf("a.PNG") == "image" && FireshareClient.KindOf("a.mp4") == "video" && FireshareClient.KindOf("a.jxr") == null, "kind by extension");

// Live round trip, only when pointed at a real instance.
var url = Environment.GetEnvironmentVariable("FIRESHARE_URL");
var token = Environment.GetEnvironmentVariable("FIRESHARE_TOKEN");
if (url != null && token != null && args.Length > 0)
{
    var client = new FireshareClient(url, token);
    Console.WriteLine($"token ok: {(await client.CheckAsync()).Username}");
    foreach (var f in args)
    {
        var r = await client.ShareAsync(f, null);
        Console.WriteLine($"{f} -> {r.Link}{(r.AlreadyThere ? " (already there)" : "")}");
    }
}

// Live sign-in, only when asked: secrets come from files so they never reach the command line.
if (args.Length > 0 && args[0] == "login")
{
    var dir = Environment.GetEnvironmentVariable("FIRESHARE_SECRETS");
    var login = await FireshareClient.LoginAsync(url, File.ReadAllText(Path.Combine(dir, "user")).Trim(), File.ReadAllText(Path.Combine(dir, "pass")).TrimEnd('\r', '\n'));
    if (login.MfaRequired)
    {
        Console.WriteLine("mfa required, waiting for code file");
        var codeFile = Path.Combine(dir, "code");
        for (var i = 0; i < 180 && !File.Exists(codeFile); i++) await Task.Delay(1000);
        login = await FireshareClient.VerifyMfaAsync(login, File.ReadAllText(codeFile));
    }
    if (login.Session == null) { Console.WriteLine("login failed: " + login.Error); Environment.Exit(1); }
    var info = await new FireshareClient(url, "", login.Session).SessionAsync();
    Console.WriteLine(info == null ? "session not accepted" : $"signed in as {info.Username}, sees private: {info.SeesPrivate}");
    File.WriteAllText(Path.Combine(dir, "session"), login.Session);
    return;
}

Console.WriteLine("ok");

static void Assert(bool ok, string what)
{
    if (!ok) { Console.Error.WriteLine("FAIL: " + what); Environment.Exit(1); }
}
