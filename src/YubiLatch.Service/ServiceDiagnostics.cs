using System.Diagnostics;

namespace YubiLatch.Service;

internal enum ServiceStage
{
    ManagementRequestReceived, VaultReadStarted, VaultReadCompleted,
    DeviceListStarted, DeviceListCompleted,
    LogonAcquireStarted, LogonTouchStarted, LogonProofVerified, LogonProofFailed, LogonResponseSent,
    PasswordValidationStarted, PasswordValidationCompleted, EmptySlotConfirmed,
    PendingBindingSaved, KeyProgrammingStarted, KeyProgrammingCompleted,
    TouchVerificationStarted, TouchVerificationCompleted, BindingActivated,
    ManagementFailed, PipeFailed, HostFailed, UnhandledException
}

internal static class ServiceDiagnostics
{
    internal static string Format(ServiceStage stage, Exception? error = null) => error == null
        ? $"Stage={stage}"
        : $"Stage={stage}; ErrorType={error.GetType().Name}; HRESULT=0x{error.HResult:X8}";

    public static void Record(ServiceStage stage, Exception? error = null)
    {
        try
        {
            EventLog.WriteEntry("YubiLatch", Format(stage, error),
                error == null ? EventLogEntryType.Information : EventLogEntryType.Error,
                error == null ? 2100 : 2101);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Diagnostics must not break authentication. */ }
    }
}
