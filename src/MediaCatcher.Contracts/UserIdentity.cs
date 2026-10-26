using System.Security.Principal;

namespace MediaCatcher.Contracts;

public static class UserIdentity
{
    public static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
    }

    public static string ResolveCurrentUserSid()
    {
        try
        {
            return GetCurrentUserSid();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
