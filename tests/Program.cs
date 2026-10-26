using System;
using System.IO;
using System.Linq;
using FireshareShare;

// Reference ids from Python: xxhash.xxh3_128_hexdigest(data[:16 MiB]), as Fireshare computes them.
var small = Path.GetTempFileName();
File.WriteAllText(small, "hello fireshare");
Assert(FireshareClient.MediaId(small) == "851938af740afbfa0ed990202c4588e7", "small file id");

var big = Path.GetTempFileName();
File.WriteAllBytes(big, Enumerable.Range(0, 20 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray());
Assert(FireshareClient.MediaId(big) == "3d36a2c3df632134658eb3c8c8b637b8", "id covers only the first 16 MiB");

var games = new[] { new GameRef { Id = 7, Name = "ELDEN RING™" }, new GameRef { Id = 9, Name = "Baldur's Gate 3" } };
Assert(FireshareClient.MatchGame("Elden Ring", games) == 7, "match ignores case and symbols");
Assert(FireshareClient.MatchGame("Baldurs Gate 3", games) == 9, "match ignores punctuation");
Assert(FireshareClient.MatchGame("Hades", games) == null, "unknown game is null");

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

Console.WriteLine("ok");

static void Assert(bool ok, string what)
{
    if (!ok) { Console.Error.WriteLine("FAIL: " + what); Environment.Exit(1); }
}
