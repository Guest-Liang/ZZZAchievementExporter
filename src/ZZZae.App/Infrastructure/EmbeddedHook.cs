using System.Reflection;
using System.Security.Cryptography;

namespace ZZZae.App.Infrastructure;

internal static class EmbeddedHook
{
    private const string ResourceName = "ZZZae.Hook.dll";

    public static string? TryExtract()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var source = assembly.GetManifestResourceStream(ResourceName);
        if (source is null)
        {
            return null;
        }

        var data = ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(data));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ZZZae",
            digest[..16]);
        var destination = Path.Combine(directory, ResourceName);

        Directory.CreateDirectory(directory);

        if (!HasSameContent(destination, data))
        {
            File.WriteAllBytes(destination, data);
        }

        return destination;
    }

    private static byte[] ReadAllBytes(Stream source)
    {
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static bool HasSameContent(
        string path,
        ReadOnlySpan<byte> expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var actual = File.ReadAllBytes(path);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
