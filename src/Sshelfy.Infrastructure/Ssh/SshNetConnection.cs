using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

internal sealed class SshNetConnection : ISshConnection
{
    private readonly SshClient _client;
    private readonly JumpChain? _chain;
    private readonly ILogger? _logger;
    private readonly List<ShellChannel> _shells = [];
    private readonly List<IPortForward> _forwards = [];
    private int _disconnected;

    public SshNetConnection(SshClient client, JumpChain? chain = null, ILogger? logger = null)
    {
        _client = client;
        _chain = chain;
        _logger = logger;
        _client.ErrorOccurred += (_, e) => RaiseDisconnected(e.Exception.Message);
    }

    public bool IsConnected => _client.IsConnected;

    public string? CipherName => _client.ConnectionInfo.CurrentServerEncryption;

    public event EventHandler<string?>? Disconnected;

    public Task<IShellChannel> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        var modes = new Dictionary<TerminalModes, uint>
        {
            [TerminalModes.ECHO] = 1,
            [TerminalModes.IUTF8] = 1,
        };

        var stream = _client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 64 * 1024, modes);
        var shell = new ShellChannel(stream);
        shell.Closed += (_, _) =>
        {
            if (!_client.IsConnected)
                RaiseDisconnected(null);
        };
        lock (_shells)
            _shells.Add(shell);
        shell.Start();
        return Task.FromResult<IShellChannel>(shell);
    }

    public Task<IPortForward> StartForwardAsync(PortForwardRule rule, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // Binding a listener blocks briefly (and remote forwards wait for the server), so stay off the UI thread.
            try
            {
                var forward = SshNetPortForward.Start(_client, rule);
                lock (_forwards)
                    _forwards.Add(forward);
                _logger?.LogInformation("Tunnel started: {Rule}", rule.Describe());
                forward.Failed += (_, error) => _logger?.LogWarning("Tunnel {Rule}: {Error}", rule.Describe(), error);
                return forward;
            }
            catch (PortForwardException ex)
            {
                _logger?.LogWarning("Tunnel {Rule} could not start: {Error}", rule.Describe(), ex.Message);
                throw;
            }
        }, cancellationToken);

    public async Task<SshCommandResult> RunCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        using var ssh = _client.CreateCommand(command);
        try
        {
            await ssh.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            throw new SshConnectionFailedException($"The command could not run: {ex.Message}", ex);
        }

        return new SshCommandResult(ssh.ExitStatus ?? -1, ssh.Result, ssh.Error);
    }

    public async ValueTask DisposeAsync()
    {
        List<IPortForward> forwards;
        lock (_forwards)
            forwards = [.. _forwards];
        foreach (var forward in forwards)
            await forward.DisposeAsync().ConfigureAwait(false);

        List<ShellChannel> shells;
        lock (_shells)
            shells = [.. _shells];

        foreach (var shell in shells)
            await shell.DisposeAsync().ConfigureAwait(false);

        await Task.Run(() =>
        {
            try
            {
                _client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }

            _client.Dispose();
            _chain?.Dispose();
        }).ConfigureAwait(false);
    }

    private void RaiseDisconnected(string? message)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            return;

        if (message is null)
            _logger?.LogInformation("Disconnected from {Host}", _client.ConnectionInfo.Host);
        else
            _logger?.LogWarning("Connection to {Host} dropped: {Error}", _client.ConnectionInfo.Host, message);
        Disconnected?.Invoke(this, message);
    }

    /// <summary>
    /// Wraps a ShellStream with a read loop (so SSH.NET's internal buffer never grows) and an
    /// ordered write queue (ShellStream buffers writes until flushed).
    /// </summary>
    private sealed class ShellChannel : IShellChannel
    {
        private readonly ShellStream _stream;
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new();
        private Task? _readLoop;
        private Task? _writeLoop;
        private int _closed;
        private int _disposed;

        public ShellChannel(ShellStream stream)
        {
            _stream = stream;
        }

        public event Action<byte[]>? DataReceived;

        public event EventHandler? Closed;

        public void Start()
        {
            _readLoop = Task.Run(ReadLoopAsync);
            _writeLoop = Task.Run(WriteLoopAsync);
        }

        public void Write(byte[] data)
        {
            if (data.Length > 0)
                _writes.Writer.TryWrite(data);
        }

        public void Resize(int columns, int rows)
        {
            try
            {
                _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
                // Channel closing; nothing to resize.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _writes.Writer.TryComplete();
            await _cts.CancelAsync().ConfigureAwait(false);
            _stream.Dispose();
            try
            {
                if (_readLoop is not null)
                    await _readLoop.ConfigureAwait(false);
                if (_writeLoop is not null)
                    await _writeLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[32 * 1024];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    DataReceived?.Invoke(buffer.AsSpan(0, read).ToArray());
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException or IOException)
            {
                // Closed.
            }
            finally
            {
                RaiseClosed();
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var data in _writes.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    _stream.Write(data, 0, data.Length);
                    // Coalesce whatever is already queued before flushing (fast typing, paste).
                    while (_writes.Reader.TryRead(out var more))
                        _stream.Write(more, 0, more.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException or IOException)
            {
                RaiseClosed();
            }
        }

        private void RaiseClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
