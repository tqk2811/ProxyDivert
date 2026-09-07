using System;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Native;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Turns on SeDebugPrivilege for this process, once, so the process table can open processes
/// belonging to other users and to the service session.
/// </summary>
/// <remarks>
/// Running as administrator is not the same as holding this privilege: it is present in the token
/// but DISABLED, and a disabled privilege is not used in an access check. Without enabling it,
/// OpenProcess fails for everything in session 0 and for anything running as another user, so
/// their command lines read as null and a filter about arguments quietly stops matching them.
///
/// Enabling costs one call and is idempotent. Failing is not fatal — it simply means fewer
/// processes can be read, exactly as before — so the result is logged rather than thrown.
/// </remarks>
public sealed class DebugPrivilege
{
    private const string SeDebugName = "SeDebugPrivilege";

    private readonly ILogger _logger;

    public DebugPrivilege(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True when the privilege is now enabled. False leaves the tool working, with less reach.</summary>
    public bool TryEnable()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!ProcessNativeMethods.OpenProcessToken(
                    ProcessNativeMethods.GetCurrentProcess(),
                    ProcessNativeMethods.TOKEN_ADJUST_PRIVILEGES | ProcessNativeMethods.TOKEN_QUERY,
                    out token))
            {
                _logger.LogDebug("could not open this process's token to enable {Privilege}", SeDebugName);
                return false;
            }

            if (!ProcessNativeMethods.LookupPrivilegeValue(null, SeDebugName, out ProcessNativeMethods.LUID luid))
            {
                _logger.LogDebug("{Privilege} is unknown on this system", SeDebugName);
                return false;
            }

            var privileges = new ProcessNativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new ProcessNativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = ProcessNativeMethods.SE_PRIVILEGE_ENABLED,
                },
            };

            // AdjustTokenPrivileges reports success even when it enabled NOTHING — a token without
            // the privilege at all comes back true with ERROR_NOT_ALL_ASSIGNED. The last error is
            // the only way to tell the two apart.
            if (!ProcessNativeMethods.AdjustTokenPrivileges(
                    token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                _logger.LogDebug("enabling {Privilege} was refused", SeDebugName);
                return false;
            }

            const int ErrorNotAllAssigned = 1300;
            if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                _logger.LogDebug(
                    "{Privilege} is not in this process's token, so processes of other users will not be read — run as administrator for the full picture",
                    SeDebugName);
                return false;
            }

            _logger.LogDebug("{Privilege} enabled", SeDebugName);
            return true;
        }
        finally
        {
            if (token != IntPtr.Zero) ProcessNativeMethods.CloseHandle(token);
        }
    }
}
