using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>HTTP and WebSocket skin over SpaceHost, as specified in PROTOCOL.md.</summary>
public static class Server
{
    static IResult Json(JsonNode n, int status = 200) => Results.Text(n.ToJsonString(), "application/json", Encoding.UTF8, status);
    static IResult Ndjson(IEnumerable<JsonNode> n) => Results.Text(string.Join("\n", n.Select(x => x.ToJsonString())), "application/x-ndjson");
    static long? Rev(string? s) => s is null ? null : long.TryParse(s, out var r) ? r : throw new HostError("BadRequest", $"'{s}' is not a revision", 400);
    static long? Instant(string? s) => s is null ? null : Norm.TryParseInstant(s, out var ns) ? ns : throw new HostError("BadRequest", $"'{s}' is not an instant", 400);

    static readonly Etags etags = new();
    static Payload JsonP(JsonNode n) => new(n.ToJsonString(), "application/json");
    static Payload NdjsonP(IEnumerable<JsonNode> n) => new(string.Join("\n", n.Select(x => x.ToJsonString())), "application/x-ndjson");
    static Payload TextP(string t, string type) => new(t, type);

    /// <summary>Strong ETag that is the digest of the body (see `Etags`) with If-None-Match handling. `key` names the resource, `stamp` says when its content can change within this run. Only resources addressed by a content digest in their URL (releases) are cached for a year: a revision number does not identify content across runs, so revision-addressed and head resources are revalidated by their content tag (`no-cache`); a revision-addressed body is remembered for the life of the run.</summary>
    enum Cache { Digest, Revision, Head }

    static IResult Cached(HttpContext c, SpaceHost host, string key, Cache mode, Func<Payload> make)
    {
        var inm = c.Request.Headers.IfNoneMatch.SelectMany(h => (h ?? "").Split(',')).Select(x => x.Trim()).ToList();
        var (body, tag, notModified) = etags.Decide(key, mode == Cache.Head ? host.Stamp : host.Instance, inm, make);
        c.Response.Headers.ETag = tag;
        c.Response.Headers.CacheControl = mode == Cache.Digest ? "public, max-age=31536000, immutable" : "no-cache";
        return notModified ? Results.StatusCode(304) : Results.Text(body!.Text, body.Type, Encoding.UTF8);
    }

    static string Hash(string? s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s ?? "")))[..12];

    static IResult Guard(Func<IResult> f)
    {
        try { return f(); }
        catch (HostError e) { return Json(e.ToJson(), e.Status); }
        catch (Exception e) when (e is not OperationCanceledException) { return Json(new HostError("Internal", e.Message, 500).ToJson(), 500); }
    }

    /// <summary>A route that reads a JSON body: a body that is not JSON is the client's error (400), and what the route does with it runs inside the guard.</summary>
    static async Task<IResult> WithBody(HttpRequest r, Func<JsonNode, IResult> f)
    {
        JsonNode? body;
        try { body = JsonNode.Parse(await new StreamReader(r.Body).ReadToEndAsync()); }
        catch (JsonException e) { return Json(new HostError("BadRequest", "the body is not JSON: " + e.Message, 400).ToJson(), 400); }
        return body is null ? Json(new HostError("BadRequest", "empty body", 400).ToJson(), 400) : Guard(() => f(body));
    }

    /// <summary>An origin a browser page may call from: this machine (the canvas in development) or one named with --allow-origin.</summary>
    static bool Allowed(string origin, IReadOnlySet<string> named) =>
        named.Contains(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback;

    public static int Run(SpaceHost host, Program.Options o, string capture, string cluster, IReadOnlyList<Sattle.Language.SourceFile> files)
    {
        _ = files;
        var port = int.TryParse(o.Get("port"), out var p) ? p : 5080;
        var app = Build(host, port, (o.Get("allow-origin") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        app.Lifetime.ApplicationStarted.Register(() => Task.Run(() =>
        {
            try
            {
                if (o.Flags.Contains("live")) Live.Run(host, o, cluster);
                else if (host.Restart is not null) { if (!host.Done) { host.Finish(); Console.WriteLine("the journal ends before the end of its input: the capture is not resumed, the space is closed"); } }
                else
                {
                    var ropts = Program.ReplayOptionsOf(o, cluster);
                    var replay = new Replay(host, host.Space, host.Adapters, ropts, host.Gate);     // request handlers intern under the same lock
                    var stats = replay.Run(capture);
                    host.Finish();
                    Console.WriteLine($"replay finished: {stats.Records.Values.Sum():N0} records, state digest {host.StateDigest()}");
                }
            }
            catch (Exception e) { Console.Error.WriteLine("input failed: " + e); }
        }));
        Console.WriteLine($"serving http://127.0.0.1:{port} (PROTOCOL.md)");
        app.Run();
        return 0;
    }

    /// <summary>The routes over a host, not started and without an input: `Run` adds the input and blocks; a test starts it on port 0 and calls it.</summary>
    public static WebApplication Build(SpaceHost host, int port, IEnumerable<string>? allowedOrigins = null)
    {
        var named = new HashSet<string>(allowedOrigins ?? [], StringComparer.OrdinalIgnoreCase);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // a page of another site in the user's browser may neither read nor change a local engine: a request that names an origin is served only to an allowed one
            if (ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin)
            {
                if (!Allowed(origin, named)) { await Json(new HostError("ForbiddenOrigin", $"origin {origin} is not allowed (--allow-origin)", 403).ToJson(), 403).ExecuteAsync(ctx); return; }
                ctx.Response.Headers["Access-Control-Allow-Origin"] = origin; ctx.Response.Headers.Vary = "Origin";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type"; ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,DELETE,OPTIONS";
            }
            if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
            // an unknown graph is answered here, so no other missing key is mistaken for one
            if (ctx.Request.Path.Value is { } path && path.StartsWith("/graphs/", StringComparison.Ordinal))
                try { host.Head(Uri.UnescapeDataString(path.Split('/')[2])); }
                catch (HostError e) { await Json(e.ToJson(), e.Status).ExecuteAsync(ctx); return; }
            await next();
        });
        app.UseWebSockets();

        app.MapGet("/space", (HttpContext c) => Guard(() => Cached(c, host, "space", Cache.Head, () => JsonP(host.SpaceInfo()))));
        app.MapGet("/health", (HttpContext c) => Guard(() => { c.Response.Headers.CacheControl = "no-store"; return Json(Health(host)); }));
        app.MapGet("/graphs/{g}/descriptor", (string g) => Guard(() => Json(host.Descriptor(g))));
        app.MapGet("/graphs/{g}/snapshot", (HttpContext c, string g, string? revision, bool? references) => Guard(() =>
        {
            if (Rev(revision) is { } r) return Cached(c, host, $"snap-{g}-{r}-{references ?? true}", Cache.Revision, () => JsonP(host.Snapshot(g, r, references ?? true)));
            long head, seq; lock (host.Gate) { head = host.Head(g); seq = host.Seq; }
            c.Response.Headers["X-Revision"] = head.ToString(); c.Response.Headers["X-Seq"] = seq.ToString(); c.Response.Headers["Content-Location"] = $"/graphs/{g}/snapshot?revision={head}";
            return Cached(c, host, $"snap-{g}-head-{head}-{references ?? true}", Cache.Head, () => JsonP(host.Snapshot(g, head, references ?? true)));
        }));
        app.MapGet("/graphs/{g}/references", (HttpContext c, string g, string? revision, int? limit, string? after) => Guard(() =>
        {
            if (Rev(revision) is { } r) return Cached(c, host, $"refs-{g}-{r}-{limit}-{after}", Cache.Revision, () => JsonP(host.References(g, r, limit ?? 1000, after)));
            var head = host.Head(g);
            return Cached(c, host, $"refs-{g}-head-{head}-{limit}-{after}", Cache.Head, () => JsonP(host.References(g, head, limit ?? 1000, after)));
        }));
        app.MapGet("/graphs/{g}/ontology", (HttpContext c, string g) => Guard(() => Cached(c, host, $"onto-{g}", Cache.Head, () => JsonP(host.Ontology(g)))));
        app.MapGet("/graphs/{g}/patches", (HttpContext c, string g, string? after, string? to, int? limit) => Guard(() =>
        {
            var a = Rev(after) ?? 0; var head = host.Head(g);
            if (Rev(to) is { } t && t <= head) return Cached(c, host, $"patches-{g}-{a}-{t}", Cache.Revision, () => NdjsonP(host.Patches(g, a, (int)(t - a))));
            return Cached(c, host, $"patches-{g}-{a}-head-{head}-{limit}", Cache.Head, () => NdjsonP(host.Patches(g, a, limit ?? int.MaxValue)));
        }));
        app.MapGet("/graphs/{g}/document", (HttpContext c, string g, string? revision, string? format) => Guard(() =>
        {
            var doc = host.Document(g, Rev(revision));
            c.Response.Headers["X-Revision"] = doc.Revision.ToString(); c.Response.Headers["X-Document-Digest"] = doc.Digest;
            if (Rev(revision) is null) c.Response.Headers["Content-Location"] = $"/graphs/{g}/document?revision={doc.Revision}" + (format is null ? "" : "&format=" + format);
            return Cached(c, host, $"doc-{g}-{doc.Revision}-{format ?? "markdown"}", Rev(revision) is not null ? Cache.Revision : Cache.Head, () => format == "json" ? JsonP(doc.Json) : TextP(doc.Markdown, "text/markdown"));
        }));
        app.MapGet("/graphs/{g}/meters", (HttpContext c, string g) => Guard(() => Cached(c, host, $"meters-{g}", Cache.Head, () => JsonP(host.Meters(g)))));
        app.MapGet("/graphs/{g}/telemetry", (HttpContext c, string g, string? from, string? to, string? property, string? element, string? format) =>
            Guard(() => Cached(c, host, $"tele-{g}-{Hash(c.Request.QueryString.Value)}", Cache.Head, () => format == "batches" ? NdjsonP(host.Telemetry(g, Instant(from), Instant(to), property, element, true).AsArray().Select(x => x!)) : JsonP(host.Telemetry(g, Instant(from), Instant(to), property, element, false)))));
        app.MapGet("/graphs/{g}/lineage", (string g, string element, int? limit, string? after) => Guard(() => Json(host.Lineage(g, element, limit ?? LineageReader.DefaultLimit, after))));
        app.MapGet("/graphs/{g}/lineage/reverse", (string g, string element, int? limit, string? after) => Guard(() => Json(host.LineageReverse(g, element, limit ?? LineageReader.DefaultLimit, after))));
        app.MapGet("/graphs/{g}/editability", (string g) => Guard(() => Json(host.Editability(g))));
        app.MapGet("/definitions", (HttpContext c) => Guard(() => Cached(c, host, "defs", Cache.Head, () => JsonP(host.Definitions()))));
        app.MapGet("/definitions/{m}", (string m) => Guard(() => Results.Text(host.Definition(m), "text/plain")));
        app.MapPost("/check", (HttpRequest r) => WithBody(r, b => { return Json(host.Check(b["module"]!.GetValue<string>(), b["text"]!.GetValue<string>())); }));
        app.MapPost("/definitions/{m}", (string m, HttpRequest r) => WithBody(r, b =>
            Json(host.Commit(m, b["text"]!.GetValue<string>(), Rev(b["expectedRevision"]?.GetValue<string>()) ?? throw new HostError("BadRequest", "expectedRevision is required", 400)))));
        app.MapPost("/graphs/{g}/edits", (string g, HttpRequest r) => WithBody(r, b => { return Json(host.ApplyEdits(g, b)); }));
        app.MapGet("/palette", (HttpContext c) => Guard(() => Cached(c, host, "palette", Cache.Head, () => JsonP(Palette.Build()))));
        app.MapPost("/palette/skeleton", (HttpRequest r) => WithBody(r, b => { return Json(host.Skeleton(b)); }));
        app.MapPost("/drafts/{id}/skeleton", (string id, HttpRequest r) => WithBody(r, b => { b["draft"] = id; return Json(host.Skeleton(b)); }));
        app.MapPost("/drafts", (HttpRequest r) => WithBody(r, b => { return Json(host.CreateDraft(Rev(b["base"]?.GetValue<string>()))); }));
        app.MapGet("/drafts", () => Guard(() => Json(host.ListDrafts())));
        app.MapGet("/drafts/{id}", (string id) => Guard(() => Json(host.DraftInfo(id))));
        app.MapDelete("/drafts/{id}", (string id) => Guard(() => { host.DiscardDraft(id); return Json(new JsonObject { ["discarded"] = true }); }));
        app.MapGet("/drafts/{id}/snapshot", (string id) => Guard(() => Json(host.DraftSnapshot(id))));
        app.MapGet("/drafts/{id}/patches", (string id, string? after) => Guard(() => Ndjson(host.DraftPatches(id, Rev(after) ?? 0))));
        app.MapGet("/drafts/{id}/text/{m}", (string id, string m) => Guard(() => Results.Text(host.DraftText(id, m), "text/plain")));
        app.MapPost("/drafts/{id}/patches", (string id, HttpRequest r) => WithBody(r, b => { return Json(host.ApplyDraftPatch(id, b["patch"]!, Rev(b["expectedRevision"]?.GetValue<string>()) ?? throw new HostError("BadRequest", "expectedRevision is required", 400))); }));
        app.MapPost("/drafts/{id}/check", (string id) => Guard(() => Json(host.CheckDraft(id))));
        app.MapPost("/drafts/{id}/commit", (string id, HttpRequest r) => WithBody(r, b => { return Json(host.CommitDraft(id, Rev(b["expectedCatalogRevision"]?.GetValue<string>()) ?? throw new HostError("BadRequest", "expectedCatalogRevision is required", 400))); }));
        app.MapPost("/drafts/{id}/restore", (string id, HttpRequest r) => WithBody(r, b => { return Json(host.Restore(id, b["digest"]!.GetValue<string>())); }));
        app.MapPost("/drafts/{id}/preview", (string id, HttpRequest r) => WithBody(r, b => { return Json(host.Preview(id, b)); }));
        app.MapPost("/drafts/{id}/refactor", (string id, HttpRequest r) => WithBody(r, b => { return Json(host.Refactor(id, b)); }));
        app.MapGet("/releases", (HttpContext c, string? module) => Guard(() => Cached(c, host, $"releases-{module}", Cache.Head, () => JsonP(host.Releases(module)))));
        app.MapGet("/releases/diff", (HttpContext c, string from, string to) => Guard(() => Cached(c, host, $"rdiff-{from}-{to}", Cache.Digest, () => JsonP(host.ReleaseDiff(from, to)))));
        app.MapGet("/releases/{digest}", (HttpContext c, string digest) => Guard(() => Cached(c, host, "rel-" + digest, Cache.Digest, () => JsonP(host.ReleaseSnapshot(digest)))));
        app.MapGet("/releases/{digest}/text", (HttpContext c, string digest) => Guard(() => Cached(c, host, "reltext-" + digest, Cache.Digest, () => TextP(host.ReleaseText(digest), "text/plain"))));
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var send = new SemaphoreSlim(1);
            async Task Send(string text) { await send.WaitAsync(); try { if (ws.State == WebSocketState.Open) await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); } finally { send.Release(); } }
            await Send(new JsonObject { ["type"] = "hello", ["protocol"] = "3", ["space"] = host.SpaceInfo() }.ToJsonString());
            Subscription? sub = null;
            var buf = new byte[1 << 16];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder(); WebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(buf, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) goto done; sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); } while (!r.EndOfMessage);
                    JsonNode? msg; try { msg = JsonNode.Parse(sb.ToString()); } catch (JsonException) { await Send(Err("BadRequest", "not JSON")); continue; }
                    var op = msg?["op"]?.GetValue<string>(); var graph = msg?["graph"]?.GetValue<string>();
                    try
                    {
                        switch (op)
                        {
                            case "subscribe":
                                {
                                    if (sub is not null) host.Unsubscribe(sub);
                                    var gs = msg!["graphs"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? (graph is null ? null : [graph]);
                                    sub = host.Subscribe(gs, msg["drafts"]?.GetValue<bool>() ?? false, Rev(msg["after"]?.GetValue<string>()));
                                    var s = sub;
                                    _ = Task.Run(async () => { try { await foreach (var m in s.Messages.Reader.ReadAllAsync()) await Send(m); } catch (Exception) { /* closed */ } });
                                    break;
                                }
                            case "unsubscribe": if (sub is not null) { host.Unsubscribe(sub); sub = null; } break;
                            case "ping": await Send(new JsonObject { ["type"] = "pong" }.ToJsonString()); break;
                            default: await Send(Err("BadRequest", $"unknown op {op}")); break;
                        }
                    }
                    catch (HostError e) { await Send(new JsonObject { ["type"] = "error", ["code"] = e.Code, ["message"] = e.Message, ["graph"] = graph }.ToJsonString()); }
                }
            done:;
            }
            finally { if (sub is not null) host.Unsubscribe(sub); }
        });

        return app;
    }

    static string Err(string code, string message) => new JsonObject { ["type"] = "error", ["code"] = code, ["message"] = message }.ToJsonString();

    static JsonNode Health(SpaceHost h)
    {
        lock (h.Gate)
        {
            var info = h.SpaceInfo();
            return new JsonObject
            {
                ["mode"] = h.Mode, ["done"] = h.Done, ["seq"] = h.Seq.ToString(), ["digest"] = h.Engine.StateDigest(), ["catalogRevision"] = info["catalogRevision"]?.DeepClone(),
                ["graphs"] = new JsonArray([.. info["graphs"]!.AsArray().Select(g => (JsonNode)new JsonObject { ["name"] = g!["id"]!.GetValue<string>(), ["head"] = g["head"]!.GetValue<string>() })]),
                ["lag"] = info["input"]!["lag"]?.DeepClone(),
            };
        }
    }
}
