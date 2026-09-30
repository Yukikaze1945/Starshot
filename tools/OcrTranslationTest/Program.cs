using System.Net;
using System.Net.Sockets;
using System.Text;
using Starshot;
using Starshot.Helpers;

string temp = Path.Combine(Path.GetTempPath(), "Starshot-OcrTranslationTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    AppConfig.UserDataFolder = temp;
    var lines = new List<OcrLine>
    {
        new("Most photosynthetic", new(0, 0, 200, 20)),
        new("organisms are plants.", new(0, 23, 200, 20)),
        new("Carbon fixation", new(0, 70, 200, 20))
    };
    Require(OcrTextFormatter.Lines(lines).Split(Environment.NewLine).Length == 3, "original lines");
    Require(OcrTextFormatter.Paragraphs(lines) ==
        "Most photosynthetic organisms are plants." + Environment.NewLine +
        Environment.NewLine + "Carbon fixation", "smart paragraphs");

    OcrTranslationClient.SaveApiKey("test-only-token");
    Require(!OcrTranslationClient.IsValidEndpoint("http://example.com/v1/chat/completions"),
        "remote HTTP rejected");
    byte[] encrypted = File.ReadAllBytes(Path.Combine(temp, "translation-key.dpapi"));
    Require(!Encoding.UTF8.GetString(encrypted).Contains("test-only-token"), "encrypted key");
    Require(OcrTranslationClient.GetModelsUri(
        "https://example.com/custom/v1/chat/completions").AbsoluteUri ==
        "https://example.com/custom/v1/models", "model endpoint derivation");
    using (var modelsServer = new TcpListener(IPAddress.Loopback, 0))
    {
        modelsServer.Start();
        int modelsPort = ((IPEndPoint)modelsServer.LocalEndpoint).Port;
        Task modelsReply = Task.Run(async () =>
        {
            using TcpClient connection = await modelsServer.AcceptTcpClientAsync();
            using NetworkStream stream = connection.GetStream();
            byte[] buffer = new byte[4096];
            int count = await stream.ReadAsync(buffer);
            string request = Encoding.UTF8.GetString(buffer, 0, count);
            Require(request.StartsWith("GET /custom/v1/models HTTP/1.1") &&
                request.Contains("Authorization: Bearer test-only-token", StringComparison.OrdinalIgnoreCase),
                "model discovery request");
            const string json = "{\"data\":[{\"id\":\"chat-b\"},{\"id\":\"chat-a\"},{\"id\":\"chat-a\"}]}";
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(body);
        });
        var models = await OcrTranslationClient.GetModelsAsync(
            $"http://127.0.0.1:{modelsPort}/custom/v1/chat/completions",
            null, CancellationToken.None);
        await modelsReply;
        Require(models.SequenceEqual(new[] { "chat-a", "chat-b" }), "model discovery parsing");
    }
    using var server = new TcpListener(IPAddress.Loopback, 0);
    server.Start();
    int port = ((IPEndPoint)server.LocalEndpoint).Port;
    AppConfig.TranslationApiUrl = $"http://127.0.0.1:{port}/v1/chat/completions";
    AppConfig.TranslationModel = "test-model";
    Task serverTask = Task.Run(async () =>
    {
        using TcpClient connection = await server.AcceptTcpClientAsync();
        using NetworkStream stream = connection.GetStream();
        var bytes = new List<byte>();
        byte[] buffer = new byte[4096];
        int headerEnd = -1, contentLength = 0;
        while (true)
        {
            int count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            if (headerEnd < 0)
            {
                string request = Encoding.UTF8.GetString(bytes.ToArray());
                headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    string length = request.Split("\r\n").First(line =>
                        line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    contentLength = int.Parse(length.Split(':')[1].Trim());
                    headerEnd += 4;
                }
            }
            if (headerEnd >= 0 && bytes.Count >= headerEnd + contentLength) break;
        }
        string raw = Encoding.UTF8.GetString(bytes.ToArray());
        Require(raw.Contains("Authorization: Bearer test-only-token", StringComparison.OrdinalIgnoreCase),
            "bearer header");
        Require(raw.Contains("test-model") && raw.Contains("Most photosynthetic"),
            "request payload");
        const string json = "{\"choices\":[{\"message\":{\"content\":\"大多数光合生物\"}}]}";
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] reply = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(reply);
        await stream.WriteAsync(body);
    });
    string translated = await OcrTranslationClient.TranslateAsync(
        "Most photosynthetic organisms", "简体中文", CancellationToken.None);
    await serverTask;
    Require(translated == "大多数光合生物", "translation response");
    var segments = new[] { new OcrTranslationClient.Segment("s0", "Heading"), new OcrTranslationClient.Segment("s1", "Body") };
    async Task<IReadOnlyList<OcrTranslationClient.Segment>> Formatted(string content)
    {
        using var mock = new TcpListener(IPAddress.Loopback, 0);
        mock.Start();
        AppConfig.TranslationApiUrl = $"http://127.0.0.1:{((IPEndPoint)mock.LocalEndpoint).Port}/v1/chat/completions";
        var replyTask = Task.Run(async () =>
        {
            using var connection = await mock.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            int length = 0;
            while (await reader.ReadLineAsync() is { Length: > 0 } header)
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':')[1]);
            var requestBody = new char[length];
            await reader.ReadBlockAsync(requestBody);
            Require(new string(requestBody).Contains("s0"), "formatted request IDs");
            byte[] body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        try { return await OcrTranslationClient.TranslateFormattedAsync(segments, "简体中文", CancellationToken.None); }
        finally { await replyTask; }
    }
    var formatted = await Formatted("```json\n[{\"id\":\"s1\",\"text\":\"正文\"},{\"id\":\"s0\",\"text\":\"标题\"}]\n```");
    Require(formatted[0].Text == "标题" && formatted[1].Text == "正文", "formatted response order");
    foreach (string invalid in new[] { "not JSON", "[]", "[{\"id\":\"s0\",\"text\":\"一\"},{\"id\":\"s0\",\"text\":\"二\"}]", "[{\"id\":\"s0\",\"text\":\"一\"},{\"id\":\"unknown\",\"text\":\"二\"}]" })
    {
        try { await Formatted(invalid); throw new Exception("invalid formatting accepted"); }
        catch (InvalidDataException) { }
    }
    OcrTranslationClient.ClearApiKey();
    Require(!OcrTranslationClient.HasApiKey, "clear key");
    try
    {
        await OcrTranslationClient.TranslateAsync("hello", "简体中文", CancellationToken.None);
        throw new Exception("missing key accepted");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("API Key")) { }
    Console.WriteLine("OCR layout, DPAPI key, model discovery, translation and strict formatted segment validation passed.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static void Require(bool condition, string name)
{
    if (!condition) throw new Exception(name + " failed");
}

namespace Starshot
{
    public static class AppConfig
    {
        public static string UserDataFolder { get; set; } = "";
        public static string TranslationApiUrl { get; set; } = "";
        public static string TranslationModel { get; set; } = "";
    }
}

namespace Starshot.Helpers
{
    public record TestRect(double Left, double Top, double Width, double Height);
    public record OcrLine(string Text, TestRect Rect);
}
