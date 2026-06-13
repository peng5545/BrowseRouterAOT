using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using System;
using System.Text.Json;

namespace BrowseRouter.Tests;

public class OpenUrlRequestTests
{
    [Fact]
    public void Direct_construction_with_empty_url_throws()
    {
        // The [JsonConstructor] enforces non-empty Url so a malformed pipe
        // payload (or a misbehaving direct caller) can't produce a request
        // with Url=null that would propagate to UriFactory.TryParse(null).
        Assert.Throws<ArgumentException>(() => new OpenUrlRequest(""));
        Assert.Throws<ArgumentException>(() => new OpenUrlRequest("   "));
    }

    [Fact]
    public void Direct_construction_with_valid_url_succeeds()
    {
        var req = new OpenUrlRequest("https://example.com/x")
        {
            SourceProcessName = "TEAMS.EXE",
            SourcePid = 1234,
            LauncherPid = 5678
        };
        Assert.Equal("https://example.com/x", req.Url);
        Assert.Equal("TEAMS.EXE", req.SourceProcessName);
        Assert.Equal(1234, req.SourcePid);
        Assert.Equal(5678, req.LauncherPid);
    }

    [Fact]
    public void Empty_url_in_json_payload_throws_during_deserialization()
    {
        // STJ routes through the [JsonConstructor] which validates; a missing
        // or empty url comes out as a JsonException so the Host's pipe
        // handler logs a clear "malformed request" instead of routing null.
        const string payload = """{"url": "", "type": "openUrl"}""";
        var ex = Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize(payload, AppJsonContext.Default.OpenUrlRequest));
        Assert.Contains("Url", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_url_in_json_payload_throws_during_deserialization()
    {
        const string payload = """{"type": "openUrl"}""";
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(payload, AppJsonContext.Default.OpenUrlRequest));
    }

    [Fact]
    public void Well_formed_payload_round_trips_with_new_field_names()
    {
        const string payload = """
                               {
                                 "type": "openUrl",
                                 "url": "https://example.com/x",
                                 "sourceProcessName": "TEAMS.EXE",
                                 "sourcePid": 1234,
                                 "launcherPid": 5678,
                                 "launcherSessionId": 2
                               }
                               """;
        var req = JsonSerializer.Deserialize<OpenUrlRequest>(payload, AppJsonContext.Default.OpenUrlRequest);
        Assert.NotNull(req);
        Assert.Equal("https://example.com/x", req.Url);
        Assert.Equal("TEAMS.EXE", req.SourceProcessName);
        Assert.Equal(1234, req.SourcePid);
        Assert.Equal(5678, req.LauncherPid);
        Assert.Equal(2, req.LauncherSessionId);
    }
}