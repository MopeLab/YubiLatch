#pragma once
#include <windows.h>

// Local named-pipe instances are briefly busy between a reply and DisconnectNamedPipe.
// Include connection retries in the caller's deadline and honor cancellation while waiting.
inline HANDLE ConnectLocalPipe(PCWSTR name, HANDLE cancel, ULONGLONG deadline) noexcept {
    while (GetTickCount64() < deadline) {
        const DWORD cancelled = WaitForSingleObject(cancel, 0);
        if (cancelled != WAIT_TIMEOUT) {
            SetLastError(cancelled == WAIT_OBJECT_0 ? ERROR_OPERATION_ABORTED : ERROR_INVALID_HANDLE);
            return INVALID_HANDLE_VALUE;
        }
        HANDLE pipe = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) return INVALID_HANDLE_VALUE;
        const ULONGLONG now = GetTickCount64();
        const DWORD delay = now < deadline ? static_cast<DWORD>((deadline - now) < 20 ? deadline - now : 20) : 0;
        const DWORD waited = WaitForSingleObject(cancel, delay);
        if (waited != WAIT_TIMEOUT) {
            SetLastError(waited == WAIT_OBJECT_0 ? ERROR_OPERATION_ABORTED : ERROR_INVALID_HANDLE);
            return INVALID_HANDLE_VALUE;
        }
    }
    SetLastError(ERROR_TIMEOUT);
    return INVALID_HANDLE_VALUE;
}
