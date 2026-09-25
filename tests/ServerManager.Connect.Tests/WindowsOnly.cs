using System.Runtime.Versioning;

// DPAPI, pipe ACLs, the registry and netsh make this a Windows-only suite, like the library's
// Windows-only types it exercises.
[assembly: SupportedOSPlatform("windows")]
