using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LootLogger;

/// <summary>Отправка файла в канал Discord через вебхук (без бота и входа в аккаунт).</summary>
public static class DiscordSender
{
    /// <summary>Лимит Discord на файл в обычном канале — 10 МБ. Больше — сжимаем в zip.</summary>
    private const long MaxPlainFileBytes = 9L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static bool IsValidWebhookUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var isDiscord = host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com";
        return isDiscord && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Отправляет файл. Возвращает null при успехе или текст ошибки.</summary>
    public static async Task<string?> SendFileAsync(string webhookUrl, string fileName, byte[] content, string message)
    {
        if (content.LongLength > MaxPlainFileBytes)
        {
            content = Zip(fileName, content);
            fileName = Path.ChangeExtension(fileName, ".zip");
        }

        var payload = JsonSerializer.Serialize(new
        {
            content = message,
            username = "Albion Loot Logger",
            allowed_mentions = new { parse = Array.Empty<string>() }
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "text/csv");
        form.Add(file, "files[0]", fileName);

        try
        {
            var url = webhookUrl.Trim();
            url += url.Contains('?') ? "&wait=true" : "?wait=true";
            using var response = await Http.PostAsync(url, form).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized => Loc.T("DiscordWebhookGone"),
                HttpStatusCode.RequestEntityTooLarge => Loc.T("DiscordTooLarge"),
                HttpStatusCode.TooManyRequests => Loc.T("DiscordRateLimited"),
                _ => $"HTTP {(int) response.StatusCode}: {await SafeBody(response).ConfigureAwait(false)}"
            };
        }
        catch (TaskCanceledException)
        {
            return Loc.T("DiscordTimeout");
        }
        catch (HttpRequestException ex)
        {
            return Loc.F("DiscordNetwork", ex.Message);
        }
    }

    private static byte[] Zip(string fileName, byte[] content)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return memory.ToArray();
    }

    private static async Task<string> SafeBody(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            return string.Empty;
        }
    }
}
