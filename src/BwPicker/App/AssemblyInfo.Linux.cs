using System.Runtime.Versioning;

// The Linux build only runs on Linux (the Windows build is a separate net10.0-windows target).
[assembly: SupportedOSPlatform("linux")]
