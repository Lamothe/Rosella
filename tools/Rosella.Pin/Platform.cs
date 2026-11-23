using System.Runtime.Versioning;

// PAM, /etc/rosella/pin and libxcrypt are Linux-only; declaring it here keeps
// CA1416 honest instead of suppressing it per call site.
[assembly: SupportedOSPlatform("linux")]