using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipNest.Infrastructure;

internal static class AtomicFile
{
    public static async Task<byte[]> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public static async Task WriteAllBytesAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The target path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await destination
                    .WriteAsync(contents, 0, contents.Length, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temporaryPath, path, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(temporaryPath, path);
                    }

                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
