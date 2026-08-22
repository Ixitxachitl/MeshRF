// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using MeshRF.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace MeshRF.Tests;

/// <summary>
/// How many times one SendAsync actually reaches the wire. A feed that polls
/// twelve times an hour must not cost more than twelve requests.
/// </summary>
public class ScriptHttpWireCountTests(ITestOutputHelper output)
{
    /// <summary>Serves canned responses and counts request lines received.</summary>
    private sealed class CountingServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Requests;
        public readonly List<string> Lines = [];

        public CountingServer(Func<int, string> respond)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buf = new byte[8192];
                            int n = await stream.ReadAsync(buf);
                            if (n <= 0) return;
                            var text = Encoding.ASCII.GetString(buf, 0, n);
                            int which;
                            lock (Lines) { Lines.Add(text.Split("\r\n")[0]); which = ++Requests; }
                            var body = respond(which);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(body));
                            await stream.FlushAsync();
                        }
                    });
                }
            });
        }

        public int Port { get; }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static string Http(string status, string body, string? location = null)
    {
        var headers = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: application/json\r\nConnection: close\r\n";
        if (location is not null) headers += $"Location: {location}\r\n";
        return headers + "\r\n" + body;
    }

    [Fact]
    public async Task OneSendIsOneRequestWhenTheFeedHasNoData()
    {
        // What Xweather answers when there is no lightning: HTTP 200, success
        // false, an empty response array.
        const string NoData =
            """{"success":false,"error":{"code":"warn_no_data","description":"No data was returned."},"response":[]}""";

        using var server = new CountingServer(_ => Http("200 OK", NoData));
        var client = new ScriptHttpClient();
        var request = new ScriptHttpRequest
        {
            Url = $"http://127.0.0.1:{server.Port}/lightning/closest",
            SaveAs = "body",
            Timeout = TimeSpan.FromSeconds(10),
        };

        var result = await client.SendAsync(request, new ScriptExpansion(new ScriptEvent()));
        await Task.Delay(200);

        output.WriteLine($"ok={result.Ok} status={result.Status} error={result.Error}");
        output.WriteLine($"requests on the wire: {server.Requests}");
        foreach (var l in server.Lines) output.WriteLine($"  {l}");
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task ARedirectIsRefusedRatherThanFollowed()
    {
        // Every response redirects onward, which is what an edge that bounces a
        // request looks like. Following the chain would be invisible from the
        // result and would cost one API request per hop.
        using var server = new CountingServer(n =>
            n < 40 ? Http("302 Found", "", "/again") : Http("200 OK", "{\"response\":[]}"));

        var client = new ScriptHttpClient();
        var request = new ScriptHttpRequest
        {
            Url = $"http://127.0.0.1:{server.Port}/lightning/closest",
            SaveAs = "body",
            Timeout = TimeSpan.FromSeconds(10),
        };

        var result = await client.SendAsync(request, new ScriptExpansion(new ScriptEvent()));
        await Task.Delay(200);

        output.WriteLine($"ok={result.Ok} status={result.Status} error={result.Error}");
        output.WriteLine($"requests on the wire for ONE SendAsync: {server.Requests}");

        Assert.Equal(1, server.Requests);
        Assert.False(result.Ok);
        Assert.Equal(302, result.Status);
        Assert.Contains("/again", result.Error);
    }
}
