using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal sealed class CaptureWakeForwarder : IAsyncDisposable
{
    private const int Port = CaptureWakeListener.Port;
    private readonly TcpListener _listener;
    private readonly IPAddress _allowedSource;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _connectionsLock = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _loop;

    private CaptureWakeForwarder(IPAddress bindAddress, IPAddress allowedSource)
    {
        _listener = new TcpListener(bindAddress, Port);
        _allowedSource = allowedSource;
    }

    internal static CaptureWakeForwarder CreateForDefaultRoute()
    {
        (string interfaceName, IPAddress gateway) = ReadDefaultRoute();
        IPAddress bindAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => string.Equals(network.Name, interfaceName, StringComparison.Ordinal))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Single(address => address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address));
        return new CaptureWakeForwarder(bindAddress, gateway);
    }

    internal void Start(CancellationToken cancellationToken = default)
    {
        _listener.Start(backlog: 8);
        _loop = AcceptAsync(cancellationToken);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _stopping.Token, cancellationToken);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                TcpClient source = await _listener.AcceptTcpClientAsync(linked.Token);
                if (source.Client.RemoteEndPoint is not IPEndPoint remote
                    || !remote.Address.MapToIPv4().Equals(_allowedSource.MapToIPv4()))
                {
                    source.Dispose();
                    continue;
                }
                Track(HandleAsync(source, linked.Token));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task HandleAsync(TcpClient source, CancellationToken stopping)
    {
        using (source)
        {
            try
            {
                await RelayAsync(source, stopping);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private void Track(Task connection)
    {
        lock (_connectionsLock)
        {
            _connections.Add(connection);
        }
        _ = connection.ContinueWith(
            completed =>
            {
                lock (_connectionsLock)
                {
                    _connections.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task RelayAsync(TcpClient source, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        NetworkStream sourceStream = source.GetStream();
        byte[] request = new byte[4096];
        int used = 0;
        while (used < request.Length)
        {
            int read = await sourceStream.ReadAsync(request.AsMemory(used), timeout.Token);
            if (read == 0) return;
            used += read;
            if (HasHeaderTerminator(request.AsSpan(0, used))) break;
        }

        using var destination = new TcpClient();
        await destination.ConnectAsync(IPAddress.Loopback, Port, timeout.Token);
        NetworkStream destinationStream = destination.GetStream();
        await destinationStream.WriteAsync(request.AsMemory(0, used), timeout.Token);

        using var requestTail = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task forwardingTail = ForwardRequestTailAsync(
            sourceStream,
            destinationStream,
            request.Length + 1 - used,
            requestTail.Token);
        byte[] response = new byte[512];
        int responseLength = await destinationStream.ReadAsync(response, timeout.Token);
        requestTail.Cancel();
        try
        {
            await forwardingTail;
        }
        catch (OperationCanceledException) when (requestTail.IsCancellationRequested) { }
        if (responseLength > 0)
        {
            await sourceStream.WriteAsync(response.AsMemory(0, responseLength), timeout.Token);
        }
    }

    private static async Task ForwardRequestTailAsync(
        NetworkStream source,
        NetworkStream destination,
        int remaining,
        CancellationToken cancellationToken)
    {
        byte[] tail = new byte[Math.Min(512, remaining)];
        while (remaining > 0)
        {
            int read = await source.ReadAsync(
                tail.AsMemory(0, Math.Min(tail.Length, remaining)), cancellationToken);
            if (read == 0) return;
            await destination.WriteAsync(tail.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static bool HasHeaderTerminator(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
        return bytes.IndexOf(terminator) >= 0;
    }

    private static (string InterfaceName, IPAddress Gateway) ReadDefaultRoute()
    {
        foreach (string line in File.ReadLines("/proc/net/route").Skip(1))
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4
                || columns[1] != "00000000"
                || !uint.TryParse(
                    columns[2], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint gateway)
                || !int.TryParse(
                    columns[3], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int flags)
                || (flags & 0x3) != 0x3)
            {
                continue;
            }
            return (columns[0], new IPAddress(BitConverter.GetBytes(gateway)));
        }
        throw new InvalidOperationException("The wake adapter requires one IPv4 default route.");
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        Task[] connections;
        lock (_connectionsLock)
        {
            connections = [.. _connections];
        }
        await Task.WhenAll(connections);
        _stopping.Dispose();
    }
}
