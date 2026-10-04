using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BwPicker;

/// <summary>
/// Release builds carry their native libraries (SkiaSharp, HarfBuzz, ANGLE) as embedded resources. Before anything
/// loads them, make sure the copies next to the exe exist and are byte-identical to the embedded ones, restoring
/// them otherwise. This lets an update that only replaced BwPicker.exe start correctly, and means a swapped DLL
/// next to the exe is replaced before use. Nothing is extracted to a temp folder.
/// </summary>
static class NativeLibraries
{
    const string Prefix = "native/";

    /// <summary>False if a library couldn't be restored; the user has been told why.</summary>
    public static bool Ensure()
    {
        var assembly = typeof(NativeLibraries).Assembly;
        string folder = AppContext.BaseDirectory;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            string name = resource[Prefix.Length..];
            if (name.Contains('/') || name.Contains('\\') || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string path = Path.Combine(folder, name);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            byte[] expected = new byte[stream.Length];
            stream.ReadExactly(expected);
            if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(expected))) continue;
            try
            {
                string temp = path + ".new";
                File.WriteAllBytes(temp, expected);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MessageBox(IntPtr.Zero,
                    $"BwPicker couldn't restore {name} in {folder}.\n\n{e.Message}\n\nReinstall BwPicker from its release zip.",
                    "BwPicker", 0x10 /* MB_ICONERROR */);
                return false;
            }
        }
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
