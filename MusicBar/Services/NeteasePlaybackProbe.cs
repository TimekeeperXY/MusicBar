using System.Net.WebSockets;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace MusicBar.Services;

internal sealed class NeteasePlaybackProbe : IDisposable
{
    internal const int DebuggingPort = 16453;
    private static readonly Uri TargetsUri = new($"http://127.0.0.1:{DebuggingPort}/json");
    private static readonly byte[] EvaluationRequest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        id = 1,
        method = "Runtime.evaluate",
        @params = new
        {
            expression = """
                (() => {
                  const sliders = Array.from(document.querySelectorAll('.slider-default'));
                  const slider = sliders.find(element => element.style.getPropertyValue('--track-precent'));
                  const percent = parseFloat(slider?.style.getPropertyValue('--track-precent') || '');
                  if (!Number.isFinite(percent)) return null;
                  const pauseIcon = document.querySelector('[data-testid="tid_playbar_play_btn"] [title*="暂停"]');
                  const playButton = document.querySelector('[data-testid="tid_playbar_play_btn"]');
                  const fiberKey = playButton && Object.keys(playButton)
                    .find(key => key.startsWith('__reactInternalInstance'));
                  let fiber = fiberKey ? playButton[fiberKey] : null;
                  let durationSeconds = 0;
                  for (let depth = 0; fiber && depth < 40; depth++, fiber = fiber.return) {
                    const candidate = Number(fiber.memoizedProps?.resourceDuration);
                    if (Number.isFinite(candidate) && candidate > 0) {
                      durationSeconds = candidate;
                      break;
                    }
                  }
                  return {
                    progressRatio: Math.max(0, Math.min(1, percent / 100)),
                    isPlaying: Boolean(pauseIcon),
                    durationSeconds
                  };
                })()
                """,
            returnByValue = true
        }
    }));

    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromMilliseconds(500)
    };

    public async Task<NeteasePlaybackState?> TryGetStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(TargetsUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var targets = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            var socketUrl = targets.RootElement.EnumerateArray()
                .Where(target => target.TryGetProperty("url", out var url) &&
                    url.GetString()?.StartsWith("orpheus://", StringComparison.OrdinalIgnoreCase) == true)
                .Select(target => target.TryGetProperty("webSocketDebuggerUrl", out var socket)
                    ? socket.GetString()
                    : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (socketUrl is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(socketUrl), timeout.Token);
            await webSocket.SendAsync(
                EvaluationRequest,
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeout.Token);

            var buffer = new byte[16 * 1024];
            using var result = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                result.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage && result.Length < 64 * 1024);

            return ParseEvaluationResponse(result.ToArray());
        }
        catch (Exception exception) when (exception is HttpRequestException or
            OperationCanceledException or WebSocketException or JsonException or IOException)
        {
            return null;
        }
    }

    public NeteaseWindowTrack? TryGetWindowTrack()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("cloudmusic"))
            {
                using (process)
                {
                    var track = ParseWindowTitle(process.MainWindowTitle);
                    if (track is not null)
                    {
                        return track;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The player may be replacing its main window while starting or closing.
        }

        return null;
    }

    internal static NeteaseWindowTrack? ParseWindowTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        var title = windowTitle.Trim();
        if (title.Equals("网易云音乐", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("NetEase Cloud Music", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var separator in new[] { " - ", " — ", " – " })
        {
            var separatorIndex = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex + separator.Length >= title.Length)
            {
                continue;
            }

            var trackTitle = title[..separatorIndex].Trim();
            var artist = title[(separatorIndex + separator.Length)..].Trim();
            if (trackTitle.Length > 0 && artist.Length > 0)
            {
                return new NeteaseWindowTrack(trackTitle, artist);
            }
        }

        return null;
    }

    internal static NeteasePlaybackState? ParseEvaluationResponse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        if (!document.RootElement.TryGetProperty("result", out var protocolResult) ||
            !protocolResult.TryGetProperty("result", out var runtimeResult) ||
            !runtimeResult.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("progressRatio", out var ratioElement) ||
            !ratioElement.TryGetDouble(out var ratio) ||
            !value.TryGetProperty("isPlaying", out var playingElement) ||
            playingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("durationSeconds", out var durationElement) ||
            !durationElement.TryGetDouble(out var durationSeconds))
        {
            return null;
        }

        return new NeteasePlaybackState(
            Math.Clamp(ratio, 0, 1),
            playingElement.GetBoolean(),
            Math.Max(0, durationSeconds));
    }

    internal static TimeSpan CalculatePosition(TimeSpan duration, double progressRatio) =>
        duration <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(duration.Ticks * Math.Clamp(progressRatio, 0, 1)));

    public void Dispose() => _client.Dispose();
}

internal sealed record NeteasePlaybackState(
    double ProgressRatio,
    bool IsPlaying,
    double DurationSeconds);

internal sealed record NeteaseWindowTrack(string Title, string Artist)
{
    public string Key => $"{Title}\u001f{Artist}";
}
