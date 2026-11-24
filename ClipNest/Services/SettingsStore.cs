using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ClipNest.Infrastructure;
using ClipNest.Models;

namespace ClipNest.Services;

public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastSaveVersion;
    private long _lastWrittenVersion;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = await AtomicFile
                .ReadAllBytesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            var settings = await Task.Run(() => Deserialize(json), cancellationToken)
                .ConfigureAwait(false);
            settings.Normalize();
            return settings;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or SerializationException
            or System.Xml.XmlException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var snapshot = settings.CreateSnapshot();
        snapshot.Normalize();
        var saveVersion = Interlocked.Increment(ref _lastSaveVersion);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (saveVersion < Interlocked.Read(ref _lastWrittenVersion))
            {
                return;
            }

            var json = await Task.Run(() => Serialize(snapshot), cancellationToken)
                .ConfigureAwait(false);
            await AtomicFile
                .WriteAllBytesAsync(_path, json, cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _lastWrittenVersion, saveVersion);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }

    private static byte[] Serialize(AppSettings settings)
    {
        var serializer = new DataContractJsonSerializer(typeof(AppSettings));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, settings);
        return stream.ToArray();
    }

    private static AppSettings Deserialize(byte[] json)
    {
        var serializer = new DataContractJsonSerializer(typeof(AppSettings));
        using var stream = new MemoryStream(json, writable: false);
        return serializer.ReadObject(stream) as AppSettings ?? new AppSettings();
    }
}
