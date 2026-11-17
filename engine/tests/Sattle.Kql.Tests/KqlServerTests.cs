using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>The `/kql/*` routes of the server on a real listener: the calls of the editors on a projection's KQL text (research 11, section 2). They read the text with its digest, answer with the digest of the text they read, read a draft's text from the draft, and refuse what they cannot read and a foreign origin.</summary>
public class KqlServerTests
{
    static async Task<T> Serving<T>(Func<HttpClient, Task<T>> call)
    {
        await using var app = Server.Build(KqlHostTests.Served.Value, 0);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = Timeout.InfiniteTimeSpan };      // no time limit of the client: on a loaded machine a call is slow, not failed (the default gives up after 100 s)
        try { return await call(http); } finally { await app.StopAsync(); }
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? origin, string? body = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (origin is not null) r.Headers.Add("Origin", origin);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "text/plain");      // a simple request: a browser sends it across origins without a preflight
        return r;
    }

    [Fact]
    public async Task A_call_on_a_projections_text_is_refused_to_a_foreign_origin()
    {
        var response = await Serving(http => http.SendAsync(Request(HttpMethod.Post, "/kql/analyse", "https://elsewhere.example", "{}")));
        Assert.Equal(403, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    const string Deps = "acme.svc.serviceDeps";
    static string DepsText => KqlHostTests.Text(Deps + ".kql");

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
        await http.DeleteAsync($"/drafts/{id}");
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
}
