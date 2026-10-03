using System.Runtime.InteropServices;

// All native imports are Windows system APIs; never search the writable app/current directories.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
