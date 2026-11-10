using System.Reflection;
using System.Security.Principal;

namespace Sulu.Infrastructure;

internal static class WindowsInfo
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
}
