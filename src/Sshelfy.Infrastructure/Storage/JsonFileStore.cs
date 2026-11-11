using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sshelfy.Infrastructure.Storage;

/// <summary>
/// Reads and writes a single JSON document. Writes go to a temporary file first and are then
/// moved over the target, so a crash mid-write never leaves a truncated file behind.
/// </summary>
internal sealed class JsonFileStore<T> where T : class
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Func<T> _defaultFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileStore(string path, Func<T> defaultFactory)
    {
        _path = path;
        _defaultFactory = defaultFactory;
    }

    public async Task<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return _defaultFactory();

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false)
                   ?? _defaultFactory();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it.
            File.Copy(_path, _path + ".corrupt", overwrite: true);
            return _defaultFactory();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(T value, CancellationToken cancellationToken = default)
    {
        // Serialize before the first await so the snapshot reflects the caller's state,
        // even if the object is mutated while the write is in flight.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
