using System.Net;
using System.Net.Sockets;
using System.Text;
using CaptureAdapters;

internal sealed class CaptureWakeListener : IAsyncDisposable
{
    internal const int Port = 43191;
    private readonly TcpListener _listener;
    private readonly CaptureScanWakeup _wakeup;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    internal CaptureWakeListener(CaptureScanWakeup wakeup)
    {
        _wakeup = wakeup;
        _listener = new TcpListener(IPAddress.Loopback, Port);
    }

    internal void Start()
    {
        _listener.Start(backlog: 8);
        _loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                await HandleAsync(client, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), timeout.Token);
            if (read == 0) return;
            used += read;
            if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string request = Encoding.ASCII.GetString(buffer, 0, used);
        int headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string[] lines = headerEnd >= 0
            ? request[..headerEnd].Split("\r\n", StringSplitOptions.None)
            : [];
        int contentLengthCount = 0;
        bool contentLengthIsZero = true;
        bool transferEncoding = false;
        bool headersAreValid = true;
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0
                || line.AsSpan(0, separator).ContainsAny(' ', '\t'))
            {
                headersAreValid = false;
                continue;
            }

            string name = line[..separator];
            string value = line[(separator + 1)..].Trim(' ', '\t');
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLengthCount++;
                contentLengthIsZero = value.Length > 0
                    && value.AsSpan().IndexOfAnyExcept('0') < 0;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                transferEncoding = true;
            }
        }

        bool accepted = headerEnd >= 0
            && headerEnd + 4 == request.Length
            && lines.Length > 0
            && lines[0] is "POST /wake HTTP/1.1" or "POST /wake HTTP/1.0"
            && headersAreValid
            && contentLengthCount <= 1
            && contentLengthIsZero
            && !transferEncoding;
        if (accepted) _wakeup.Request();
        byte[] response = Encoding.ASCII.GetBytes(accepted
            ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stopping.Dispose();
    }
}
