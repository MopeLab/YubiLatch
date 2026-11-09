#include <windows.h>
#include <thread>
#include <string>
#include <iostream>
#include "../PipeConnection.h"

namespace {
struct Handle {
    HANDLE value;
    explicit Handle(HANDLE handle) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
std::wstring Name(PCWSTR suffix) {
    return L"\\\\.\\pipe\\YubiLatch.TransportTests." + std::to_wstring(GetCurrentProcessId()) + L"."
        + std::to_wstring(GetTickCount64()) + suffix;
}
HANDLE Server(const std::wstring& name) {
    return CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
}
bool BusyInstanceBecomesAvailable() {
    auto name = Name(L".busy");
    HANDLE firstServer = Server(name);
    if (firstServer == INVALID_HANDLE_VALUE) return false;
    HANDLE firstClient = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (firstClient == INVALID_HANDLE_VALUE) { CloseHandle(firstServer); return false; }
    Handle stop(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    Handle cancel(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    bool created = false;
    std::thread release([&]() {
        Sleep(80);
        CloseHandle(firstClient);
        CloseHandle(firstServer);
        Handle replacement(Server(name));
        created = replacement.value != INVALID_HANDLE_VALUE;
        WaitForSingleObject(stop.value, 4000);
    });
    Handle connected(ConnectLocalPipe(name.c_str(), cancel.value, GetTickCount64() + 2500));
    SetEvent(stop.value);
    release.join();
    return created && connected.value != INVALID_HANDLE_VALUE;
}
bool MissingPipeTimesOut() {
    auto name = Name(L".absent");
    Handle cancel(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    const auto start = GetTickCount64();
    Handle connected(ConnectLocalPipe(name.c_str(), cancel.value, start + 150));
    const DWORD error = GetLastError();
    return connected.value == INVALID_HANDLE_VALUE && error == ERROR_TIMEOUT && GetTickCount64() - start < 2000;
}
bool BusyPipeCanBeCancelled() {
    auto name = Name(L".cancel");
    Handle server(Server(name));
    if (server.value == INVALID_HANDLE_VALUE) return false;
    Handle client(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
    if (client.value == INVALID_HANDLE_VALUE) return false;
    Handle cancel(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    std::thread cancelThread([&]() { Sleep(80); SetEvent(cancel.value); });
    const auto start = GetTickCount64();
    Handle connected(ConnectLocalPipe(name.c_str(), cancel.value, start + 4000));
    const DWORD error = GetLastError();
    cancelThread.join();
    return connected.value == INVALID_HANDLE_VALUE && error == ERROR_OPERATION_ABORTED && GetTickCount64() - start < 2000;
}
}
int main() {
    const bool available = BusyInstanceBecomesAvailable();
    const bool timeout = MissingPipeTimesOut();
    const bool cancelled = BusyPipeCanBeCancelled();
    std::cout << "Busy pipe reconnect: " << available << "; missing pipe timeout: " << timeout << "; cancellation: " << cancelled << '\n';
    return available && timeout && cancelled ? 0 : 1;
}
