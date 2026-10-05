using System.Runtime.Versioning;

// The Linux and macOS test builds only run there (the Windows build is a separate net10.0-windows target).
#if MACOS
[assembly: SupportedOSPlatform("macos")]
#else
[assembly: SupportedOSPlatform("linux")]
#endif
