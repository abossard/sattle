using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The HTTP skin (Server.cs) on a real listener: who may call it from a browser, what a bad body and an unknown graph answer, and that a served replay adapts its input under the host's lock.</summary>
public class ServerTests
{
    static async Task<T> Serving<T>(Func<HttpClient, Task<T>> call)
    {
        using var host = HostTests.NewHost();
        await using var app = Server.Build(host, 0);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        try { return await call(http); } finally { await app.StopAsync(); }
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? origin, string? body = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (origin is not null) r.Headers.Add("Origin", origin);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "text/plain");      // a simple request: a browser sends it across origins without a preflight
        return r;
    }

    public static TheoryData<string, string, string?, int, string?> Origins => new()
    {
        { "GET", "/space", null, 200, null },                                                    // no browser: curl, the CLI
        { "GET", "/space", "http://localhost:5173", 200, "http://localhost:5173" },              // the canvas in development
        { "GET", "/space", "https://elsewhere.example", 403, null },                             // a foreign page may not read
        { "POST", "/drafts", "https://elsewhere.example", 403, null },                           // nor change
        { "POST", "/check", "https://elsewhere.example", 403, null },
        { "POST", "/kql/analyse", "https://elsewhere.example", 403, null },
    };

    [Theory, MemberData(nameof(Origins))]
    public async Task A_request_is_served_to_this_machine_and_refused_to_a_foreign_origin(string method, string path, string? origin, int status, string? allowOrigin)
    {
        var response = await Serving(http => http.SendAsync(Request(new HttpMethod(method), path, origin, method == "POST" ? "{}" : null)));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(allowOrigin, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null);
    }

    [Theory]
    [InlineData("/check")]
    [InlineData("/drafts")]
    [InlineData("/graphs/serviceTree/edits")]
    public async Task A_body_that_is_not_JSON_is_a_bad_request(string path)
    {
        var (status, body) = await Serving(async http => { var r = await http.SendAsync(Request(HttpMethod.Post, path, null, "{ not json")); return ((int)r.StatusCode, await r.Content.ReadAsStringAsync()); });
        Assert.Equal(400, status);
        Assert.Equal("BadRequest", JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/graphs/nope/descriptor", 404, "UnknownGraph")]
    [InlineData("/graphs/nope/meters", 404, "UnknownGraph")]
    [InlineData("/graphs/serviceTree/descriptor", 200, null)]
    [InlineData("/drafts/nope", 404, "UnknownDraft")]                                            // another missing key keeps its own code
    public async Task Only_an_unknown_graph_is_reported_as_one(string path, int expected, string? code)
    {
        var (status, body) = await Serving(async http => { var r = await http.GetAsync(path); return ((int)r.StatusCode, await r.Content.ReadAsStringAsync()); });
        Assert.Equal(expected, status);
        if (code is not null) Assert.Equal(code, JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>());
    }

    const string Deps = "acme.svc.serviceDeps";
    static string DepsText => File.ReadAllText(Path.Combine(Repo.Definitions, Deps + ".kql"));

    static async Task<(int Status, JsonNode Body)> Send(HttpClient http, HttpMethod method, string path, JsonNode? body = null)
    {
        var r = await http.SendAsync(Request(method, path, null, body?.ToJsonString()));
        return ((int)r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!);
    }

    /// <summary>The editors read a projection's text with its digest (research 11, section 2), and each call on that text answers with the same digest; the shipped text has nothing wrong at either depth.</summary>
    [Theory]
    [InlineData("/kql/analyse", "{}")]
    [InlineData("/kql/analyse", "{\"depth\":\"full\"}")]
    [InlineData("/kql/complete", "{\"offset\":0}")]
    [InlineData("/kql/hover", "{\"offset\":10}")]
    [InlineData("/kql/related", "{\"offset\":10}")]
    public async Task A_call_on_a_projections_text_answers_with_the_digest_of_the_text_it_read(string path, string fields) => await Serving(async http =>
    {
        var (_, read) = await Send(http, HttpMethod.Get, "/kql/" + Deps);
        Assert.Equal(DepsText, read["text"]!.GetValue<string>());
        var body = JsonNode.Parse(fields)!; body["projection"] = Deps; body["text"] = DepsText;
        var (status, answer) = await Send(http, HttpMethod.Post, path, body);
        Assert.Equal((200, read["digest"]!.GetValue<string>()), (status, answer["digest"]!.GetValue<string>()));
        if (path == "/kql/analyse") Assert.Empty(answer["diagnostics"]!.AsArray());
        return 0;
    });

    /// <summary>A draft's text of a projection is read from the draft; outside it the file's text stays.</summary>
    [Fact]
    public async Task A_projections_text_in_a_draft_is_read_from_the_draft() => await Serving(async http =>
    {
        var (_, draft) = await Send(http, HttpMethod.Post, "/drafts", new JsonObject());
        var id = draft["draft"]!.GetValue<string>(); var edited = DepsText + "// edited\n";
        var (put, _) = await Send(http, HttpMethod.Put, $"/drafts/{id}/text/{Deps}.kql", new JsonObject { ["text"] = edited, ["expectedRevision"] = "0" });
        var (_, inDraft) = await Send(http, HttpMethod.Get, $"/drafts/{id}/kql/{Deps}");
        var (_, atHead) = await Send(http, HttpMethod.Get, "/kql/" + Deps);
        Assert.Equal((200, edited, DepsText), (put, inDraft["text"]!.GetValue<string>(), atHead["text"]!.GetValue<string>()));
        return 0;
    });

    /// <summary>What a call on a projection's text refuses, with the code it answers.</summary>
    [Theory]
    [InlineData("GET", "/kql/acme.svc.nosuch", null, 404, "UnknownModule")]
    [InlineData("POST", "/kql/analyse", "{\"projection\":\"nosuch.thing\",\"text\":\"\"}", 404, "UnknownModule")]
    [InlineData("POST", "/kql/analyse", "{\"projection\":\"acme.svc.serviceDeps\"}", 400, "BadRequest")]
    [InlineData("POST", "/kql/analyse", "{\"projection\":\"acme.svc.serviceDeps\",\"text\":\"\",\"depth\":\"deep\"}", 400, "BadRequest")]
    [InlineData("POST", "/kql/complete", "{\"projection\":\"acme.svc.serviceDeps\",\"text\":\"ab\",\"offset\":3}", 400, "BadRequest")]
    [InlineData("POST", "/kql/hover", "{\"projection\":\"acme.svc.serviceDeps\",\"text\":\"ab\",\"offset\":1,\"depth\":\"full\"}", 400, "BadRequest")]
    [InlineData("POST", "/kql/hover", "{\"projection\":\"acme.svc.serviceDeps\",\"text\":\"ab\",\"offset\":\"1\"}", 400, "BadRequest")]
    [InlineData("POST", "/kql/analyse", "{\"projection\":5,\"text\":\"\"}", 400, "BadRequest")]
    public async Task A_call_on_a_projections_text_refuses_what_it_cannot_read(string method, string path, string? body, int expected, string code)
    {
        var (status, answer) = await Serving(http => Send(http, new HttpMethod(method), path, body is null ? null : JsonNode.Parse(body)));
        Assert.Equal((expected, code), (status, answer["error"]!["code"]!.GetValue<string>()));
    }

    /// <summary>Review: Server.cs:182. The replay of `serve` runs on its own thread while request handlers intern under the host's lock. Given that lock, the replay interns only while it holds it: nothing is interned while the test holds the lock.</summary>
    [Fact]
    public async Task A_replay_that_is_given_the_lock_of_its_host_interns_only_while_it_holds_it()
    {
        using var host = HostTests.NewHost();
        var replay = new Replay(host, host.Space, host.Adapters, new ReplayOptions { Cluster = "synth" }, host.Gate);
        int before, during; Exception? failure = null;
        var run = new Thread(() => { try { replay.Run(EstateTests.EstateDir); } catch (Exception e) { failure = e; } });
        lock (host.Gate)
        {
            before = host.Interner.Count;
            run.Start();
            // wait until the replay stands at the lock this test holds (not for a fixed time): whatever it interned before that shows in the count
            Assert.True(SpinWait.SpinUntil(() => run.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(120)), "the replay never waited for the lock of its host");
            during = host.Interner.Count;
        }
        await Task.Run(run.Join);
        Assert.Null(failure);
        Assert.Equal(before, during);
        Assert.True(host.Interner.Count > before, "the replay interned nothing at all");
    }
}
