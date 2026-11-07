using System.Globalization;
using System.Runtime.InteropServices;

namespace S2ModKit.Desktop;

/// <summary>
/// Plays one decoded sound at a time with Windows' own media player interface (MCI, no extra library): the sound is written
/// to a file in the app's cache folder (WAV, MP3 or AAC as stored in the game) and played asynchronously. Starting a sound
/// stops the previous one. The cache folder is emptied when the player is created and disposed.
/// </summary>
internal sealed partial class SoundPlayer
{
    private const string Alias = "s2mksound";
    private readonly string cache;
    private bool open;
    private int counter;

    public SoundPlayer(string cacheFolder)
    {
        cache = cacheFolder;
        Clear();
    }

    /// <summary>Plays <paramref name="data"/>; returns an error text when Windows cannot play it, else null.</summary>
    public string? Play(byte[] data, string extension)
    {
        ArgumentNullException.ThrowIfNull(data);
        Stop();
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "sound" + (++counter).ToString(CultureInfo.InvariantCulture) + extension);
        File.WriteAllBytes(file, data);
        var error = Send($"open \"{file}\" type mpegvideo alias {Alias}");
        if (error is not null)
        {
            return error;
        }

        open = true;
        return Send($"play {Alias} from 0");
    }

    /// <summary>Opens <paramref name="data"/> as <see cref="Play"/> does and closes it without playing (checks); null when Windows can play it.</summary>
    public string? Probe(byte[] data, string extension)
    {
        ArgumentNullException.ThrowIfNull(data);
        Stop();
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "sound-probe" + extension);
        File.WriteAllBytes(file, data);
        var error = Send($"open \"{file}\" type mpegvideo alias {Alias}");
        if (error is null)
        {
            Send($"close {Alias}");
        }

        File.Delete(file);
        return error;
    }

    public void Stop()
    {
        if (open)
        {
            Send($"close {Alias}");
            open = false;
        }
    }

    /// <summary>Stops playback and empties the cache folder.</summary>
    public void Release()
    {
        Stop();
        Clear();
    }

    private void Clear()
    {
        if (!Directory.Exists(cache))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(cache, "sound*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // still open in another copy of the app: left for the next start
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? Send(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Sound playback needs Windows.";
        }

        var code = MciSendString(command, null, 0, IntPtr.Zero);
        if (code == 0)
        {
            return null;
        }

        unsafe
        {
            const int Length = 256;
            var text = stackalloc char[Length];
            return MciGetErrorString(code, text, Length) != 0 ? new string(text) : $"Windows media error {code}.";
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "mciSendStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MciSendString(string command, char[]? returnValue, int returnLength, IntPtr callback);

    [LibraryImport("winmm.dll", EntryPoint = "mciGetErrorStringW")]
    private static unsafe partial int MciGetErrorString(int error, char* text, int length);
}
