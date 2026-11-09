// Independent V2 provider: never wraps, filters, or replaces Windows providers.
#include <windows.h>
#include <credentialprovider.h>
#include <wincred.h>
#include <ntsecapi.h>
#include <sddl.h>
#include <shlwapi.h>
#include <propkey.h>
#include <atomic>
#include <mutex>
#include <thread>
#include <vector>
#include <string>
#include <memory>
#include <new>
#include <cstring>
#include <cwchar>
#include "PipeConnection.h"

namespace {
constexpr GUID ProviderId = {0xd53c2d8e,0x51cc,0x4f5d,{0x88,0xe6,0xa0,0xcd,0xf5,0xb3,0x9c,0x54}};
constexpr wchar_t PipeName[] = L"\\\\.\\pipe\\MopeLab.YubiLatch.Logon.v1";
constexpr DWORD Magic = 0x314c4259;
std::atomic<long> Objects{0};

// Fixed stages and numeric status codes only; never record account or credential data.
void Trace(PCWSTR stage, HRESULT result = S_OK, DWORD detail = 0) noexcept {
    const DWORD saved = GetLastError();
    wchar_t message[192]{};
    swprintf_s(message, L"Stage=%ls; HRESULT=0x%08lX; Detail=0x%08lX", stage, static_cast<unsigned long>(result), detail);
    HANDLE source = RegisterEventSourceW(nullptr, L"YubiLatch");
    if (source) {
        PCWSTR strings[]{message};
        ReportEventW(source, EVENTLOG_INFORMATION_TYPE, 0, 2200, nullptr, 1, 0, strings, nullptr);
        DeregisterEventSource(source);
    }
    SetLastError(saved);
}

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE v = INVALID_HANDLE_VALUE) : value(v) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value != nullptr) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

struct Secret {
    std::vector<BYTE> bytes;
    ~Secret() { clear(); }
    void clear() { if (!bytes.empty()) SecureZeroMemory(bytes.data(), bytes.size()); bytes.clear(); }
};

bool IsTrustedServer(HANDLE pipe) {
    ULONG serverPid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &serverPid)) return false;
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!manager) return false;
    SC_HANDLE service = OpenServiceW(manager, L"YubiLatch", SERVICE_QUERY_STATUS);
    SERVICE_STATUS_PROCESS status{};
    DWORD needed = 0;
    bool trusted = service && QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
        reinterpret_cast<BYTE*>(&status), sizeof(status), &needed) && status.dwCurrentState == SERVICE_RUNNING && status.dwProcessId == serverPid;
    if (service) CloseServiceHandle(service);
    CloseServiceHandle(manager);
    if (!trusted) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, serverPid));
    HANDLE tokenValue = nullptr;
    if (!OpenProcessToken(process.value, TOKEN_QUERY, &tokenValue)) return false;
    Handle token(tokenValue);
    alignas(TOKEN_USER) BYTE tokenBuffer[512]{};
    if (!GetTokenInformation(token.value, TokenUser, tokenBuffer, sizeof(tokenBuffer), &needed)) return false;
    return IsWellKnownSid(reinterpret_cast<TOKEN_USER*>(tokenBuffer)->User.Sid, WinLocalSystemSid) != FALSE;
}

bool Transfer(HANDLE pipe, bool write, void* buffer, DWORD size, HANDLE cancel, ULONGLONG deadline) {
    auto cursor = static_cast<BYTE*>(buffer);
    while (size) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.value) return false;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event.value;
        DWORD done = 0;
        BOOL ok = write ? WriteFile(pipe, cursor, size, &done, &overlapped) : ReadFile(pipe, cursor, size, &done, &overlapped);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            HANDLE waits[]{cancel, event.value};
            const auto now = GetTickCount64();
            const auto remaining = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
            if (WaitForMultipleObjects(2, waits, FALSE, remaining) != WAIT_OBJECT_0 + 1) {
                CancelIoEx(pipe, &overlapped);
                GetOverlappedResult(pipe, &overlapped, &done, TRUE); // drain before destroying OVERLAPPED
                return false;
            }
            ok = GetOverlappedResult(pipe, &overlapped, &done, FALSE);
        }
        if (!ok || done == 0 || done > size) return false;
        cursor += done;
        size -= done;
    }
    return true;
}

bool AskService(const std::wstring& sid, DWORD operation, HANDLE cancel, Secret& password) {
    const auto deadline = GetTickCount64() + (operation == 1 ? 1800 : 75000);
    const auto connectDeadline = GetTickCount64() + 1800;
    Handle pipe(ConnectLocalPipe(PipeName, cancel, connectDeadline < deadline ? connectDeadline : deadline));
    if (pipe.value == INVALID_HANDLE_VALUE) { Trace(L"ProviderPipeConnectFailed", HRESULT_FROM_WIN32(GetLastError())); return false; }
    if (!IsTrustedServer(pipe.value)) { Trace(L"ProviderServerRejected", E_ACCESSDENIED); return false; }
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || session == 0 || sid.size() * sizeof(wchar_t) > 184) return false;
    DWORD request[]{Magic, operation, session, static_cast<DWORD>(sid.size() * sizeof(wchar_t))};
    if (!Transfer(pipe.value, true, request, sizeof(request), cancel, deadline)
        || !Transfer(pipe.value, true, const_cast<wchar_t*>(sid.data()), request[3], cancel, deadline)) { Trace(L"ProviderRequestFailed", HRESULT_FROM_WIN32(GetLastError())); return false; }
    DWORD reply[3]{};
    if (!Transfer(pipe.value, false, reply, sizeof(reply), cancel, deadline)) { Trace(L"ProviderReplyFailed", HRESULT_FROM_WIN32(GetLastError())); return false; }
    if (reply[0] != Magic || reply[1] != 0) { Trace(L"ProviderReplyRejected", E_FAIL); return false; }
    if (operation == 1) return reply[2] == 0;
    if (reply[2] < 2 || reply[2] > 1024 || reply[2] % 2 != 0) return false;
    password.bytes.resize(static_cast<size_t>(reply[2]) + sizeof(wchar_t), 0);
    if (!Transfer(pipe.value, false, password.bytes.data(), reply[2], cancel, deadline)) { password.clear(); return false; }
    // Embedded nulls could cause a different credential to be submitted.
    auto chars = reinterpret_cast<const wchar_t*>(password.bytes.data());
    for (DWORD i = 0; i < reply[2] / sizeof(wchar_t); ++i) if (chars[i] == L'\0') { password.clear(); return false; }
    return true;
}

HRESULT CopyString(PCWSTR text, PWSTR* result) { if (!result) return E_POINTER; *result = nullptr; return SHStrDupW(text, result); }

enum class State { Idle, Waiting, Ready, Failed };

class Credential final : public ICredentialProviderCredential2 {
    std::atomic<ULONG> refs_{1};
    std::wstring sid_, user_;
    std::mutex mutex_;
    Secret password_;
    std::atomic<State> state_{State::Idle};
    std::atomic<bool> selected_{false};
    std::atomic<ULONGLONG> generation_{0};
    std::shared_ptr<Handle> activeCancel_;
    ULONGLONG readyUntil_ = 0;
    ICredentialProviderEvents* providerEvents_ = nullptr; // accessed on the LogonUI COM thread only
    ICredentialProviderCredentialEvents* credentialEvents_ = nullptr; // same COM-thread ownership
    UINT_PTR context_ = 0;

    void Start() {
        std::lock_guard startLock(mutex_);
        if (!selected_) return;
        State expected = State::Idle;
        if (!state_.compare_exchange_strong(expected, State::Waiting)) return;
        auto cancel = std::make_shared<Handle>(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!cancel->value) { state_ = State::Failed; return; }
        activeCancel_ = cancel;
        auto generation = ++generation_;
        IStream* marshaled = nullptr;
        if (!providerEvents_ || FAILED(CoMarshalInterThreadInterfaceInStream(__uuidof(ICredentialProviderEvents), providerEvents_, &marshaled))) {
            state_ = State::Failed; return;
        }
        IStream* fieldStream = nullptr;
        if (credentialEvents_ && FAILED(CoMarshalInterThreadInterfaceInStream(__uuidof(ICredentialProviderCredentialEvents), credentialEvents_, &fieldStream))) {
            marshaled->Release(); state_ = State::Failed; return;
        }
        AddRef();
        auto context = context_;
        try {
            std::thread([this, generation, marshaled, fieldStream, context, cancel]() {
                HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                ICredentialProviderEvents* events = nullptr;
                HRESULT unmarshal = CoGetInterfaceAndReleaseStream(marshaled, __uuidof(ICredentialProviderEvents), reinterpret_cast<void**>(&events));
                ICredentialProviderCredentialEvents* fields = nullptr;
                if (fieldStream) CoGetInterfaceAndReleaseStream(fieldStream, __uuidof(ICredentialProviderCredentialEvents), reinterpret_cast<void**>(&fields));
                Secret answer;
                bool success = false;
                Trace(L"ProviderAcquireStarted");
                try { success = SUCCEEDED(unmarshal) && AskService(sid_, 2, cancel->value, answer); }
                catch (...) { success = false; }
                bool notify = false;
                {
                    std::lock_guard lock(mutex_);
                    if (selected_ && generation_ == generation) {
                        password_.clear();
                        if (success) password_.bytes.swap(answer.bytes);
                        readyUntil_ = GetTickCount64() + 15000;
                        state_ = success ? State::Ready : State::Failed;
                        notify = true;
                    }
                }
                if (notify) {
                    Trace(success ? L"ProviderProofReady" : L"ProviderProofFailed");
                    if (success && events) Trace(L"ProviderRefreshRequested", events->CredentialsChanged(context));
                    else if (fields) fields->SetFieldString(this, 2, L"认证未完成。请只连接一把已绑定钥匙并重试，或选择其他登录方式。");
                }
                // An unconsumed credential expires even if LogonUI never requests serialization.
                if (success && WaitForSingleObject(cancel->value, 15000) == WAIT_TIMEOUT) {
                    std::lock_guard lock(mutex_);
                    if (generation_ == generation && state_ == State::Ready) { password_.clear(); state_ = State::Failed; }
                }
                if (events) events->Release();
                if (fields) fields->Release();
                if (SUCCEEDED(initialized)) CoUninitialize();
                Release();
            }).detach();
        }
        catch (...) { marshaled->Release(); if (fieldStream) fieldStream->Release(); state_ = State::Failed; Release(); }
    }

public:
    Credential(std::wstring sid, std::wstring user) : sid_(std::move(sid)), user_(std::move(user)) { ++Objects; }
    ~Credential() { if (providerEvents_) providerEvents_->Release(); if (credentialEvents_) credentialEvents_->Release(); --Objects; }
    void SetProviderEvents(ICredentialProviderEvents* events, UINT_PTR context) {
        if (providerEvents_) providerEvents_->Release();
        providerEvents_ = events;
        if (providerEvents_) providerEvents_->AddRef();
        context_ = context;
    }
    bool Ready() {
        std::lock_guard lock(mutex_);
        return selected_ && state_ == State::Ready && !password_.bytes.empty() && GetTickCount64() <= readyUntil_;
    }
    bool IsAccount(PCWSTR sid, PCWSTR user) const { return sid_ == sid && user_ == user; }
    void Stop() {
        std::lock_guard lock(mutex_);
        selected_ = false;
        ++generation_;
        if (activeCancel_) SetEvent(activeCancel_->value);
        password_.clear();
        state_ = State::Idle;
    }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(ICredentialProviderCredential) || iid == __uuidof(ICredentialProviderCredential2))
            *result = static_cast<ICredentialProviderCredential2*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    IFACEMETHODIMP_(ULONG) Release() override { auto n = --refs_; if (!n) delete this; return n; }
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* events) override {
        if (credentialEvents_) credentialEvents_->Release();
        credentialEvents_ = events;
        if (credentialEvents_) credentialEvents_->AddRef();
        return S_OK;
    }
    IFACEMETHODIMP UnAdvise() override {
        Trace(L"ProviderCredentialUnadvised");
        Stop();
        if (credentialEvents_) { credentialEvents_->Release(); credentialEvents_ = nullptr; }
        return S_OK;
    }
    IFACEMETHODIMP SetSelected(BOOL* automatic) override {
        if (!automatic) return E_POINTER;
        *automatic = FALSE;
        selected_ = true;
        // A successful proof survives re-enumeration of this same selected credential.
        // Explicit deselection/UnAdvise still cancels and wipes it in Stop().
        *automatic = Ready() ? TRUE : FALSE;
        Trace(*automatic ? L"ProviderSelectedReady" : L"ProviderSelected");
        if (*automatic) return S_OK;
        try { Start(); } catch (...) { state_ = State::Failed; return E_OUTOFMEMORY; }
        return S_OK;
    }
    IFACEMETHODIMP SetDeselected() override { Trace(L"ProviderDeselected"); Stop(); return S_OK; }
    IFACEMETHODIMP GetFieldState(DWORD id, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override {
        if (!state || !interactive) return E_POINTER;
        if (id > 3) return E_INVALIDARG;
        *state = id < 2 ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE;
        *interactive = CPFIS_NONE;
        return S_OK;
    }
    IFACEMETHODIMP GetStringValue(DWORD id, PWSTR* text) override {
        if (id == 1) return CopyString(L"YubiLatch", text);
        if (id == 3) return CopyString(L"重试", text);
        if (id != 2) return E_INVALIDARG;
        return CopyString(state_ == State::Failed
            ? L"未完成认证。请只连接一把已绑定钥匙，选择重试，或改用 PIN、密码、Windows Hello。"
            : L"插入已绑定的 YubiKey，灯闪烁时触摸金属触点。可随时切换其他登录方式。", text);
    }
    IFACEMETHODIMP GetBitmapValue(DWORD id, HBITMAP* bitmap) override {
        if (!bitmap) return E_POINTER;
        *bitmap = nullptr;
        if (id != 0) return E_INVALIDARG;
        HDC screen = GetDC(nullptr);
        HDC dc = CreateCompatibleDC(screen);
        *bitmap = CreateCompatibleBitmap(screen, 96, 96);
        if (dc && *bitmap) {
            auto old = SelectObject(dc, *bitmap);
            RECT rect{0,0,96,96};
            HBRUSH background = CreateSolidBrush(RGB(27, 92, 84));
            FillRect(dc, &rect, background);
            DeleteObject(background);
            HPEN pen = CreatePen(PS_SOLID, 7, RGB(239, 248, 231));
            auto oldPen = SelectObject(dc, pen);
            auto oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
            Ellipse(dc, 25, 18, 66, 59);
            MoveToEx(dc, 46, 59, nullptr); LineTo(dc, 46, 81);
            MoveToEx(dc, 46, 69, nullptr); LineTo(dc, 62, 69);
            SelectObject(dc, oldPen); SelectObject(dc, oldBrush); SelectObject(dc, old);
            DeleteObject(pen);
        }
        if (dc) DeleteDC(dc);
        ReleaseDC(nullptr, screen);
        return *bitmap ? S_OK : E_OUTOFMEMORY;
    }
    IFACEMETHODIMP GetSubmitButtonValue(DWORD id, DWORD* adjacent) override { if (!adjacent) return E_POINTER; *adjacent = 2; return id == 3 ? S_OK : E_INVALIDARG; }
    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL*, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetStringValue(DWORD, PCWSTR) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetUserSid(PWSTR* sid) override { return CopyString(sid_.c_str(), sid); }
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override {
        if (!response || !serialization || !text || !icon) return E_POINTER;
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED; *serialization = {}; *text = nullptr; *icon = CPSI_NONE;
        std::unique_lock lock(mutex_);
        Trace(L"ProviderSerializationRequested");
        if (!selected_) return S_OK;
        if (state_ != State::Ready) {
            // Explicit submit retries only after the previous worker has completed.
            if (state_ == State::Failed) {
                state_ = State::Idle; lock.unlock();
                try { Start(); } catch (...) { state_ = State::Failed; return E_OUTOFMEMORY; }
            }
            return S_OK;
        }
        if (GetTickCount64() > readyUntil_) { password_.clear(); state_ = State::Failed; return S_OK; }
        const DWORD flags = CRED_PACK_PROTECTED_CREDENTIALS | CRED_PACK_ID_PROVIDER_CREDENTIALS;
        DWORD length = 0;
        auto password = reinterpret_cast<PWSTR>(password_.bytes.data());
        CredPackAuthenticationBufferW(flags, user_.data(), password, nullptr, &length);
        HRESULT result = HRESULT_FROM_WIN32(GetLastError());
        if (result == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) && length > 0 && length <= 65536) {
            auto buffer = static_cast<BYTE*>(CoTaskMemAlloc(length));
            if (!buffer) result = E_OUTOFMEMORY;
            else {
                if (!CredPackAuthenticationBufferW(flags, user_.data(), password, buffer, &length)) result = HRESULT_FROM_WIN32(GetLastError());
                else {
                    HANDLE lsa = nullptr;
                    NTSTATUS status = LsaConnectUntrusted(&lsa);
                    ULONG package = 0;
                    if (status == 0) {
                        char name[] = "Negotiate";
                        LSA_STRING packageName{9,10,name};
                        status = LsaLookupAuthenticationPackage(lsa, &packageName, &package);
                        LsaDeregisterLogonProcess(lsa);
                    }
                    result = status == 0 ? S_OK : HRESULT_FROM_WIN32(LsaNtStatusToWinError(status));
                    if (SUCCEEDED(result)) {
                        serialization->ulAuthenticationPackage = package;
                        serialization->clsidCredentialProvider = ProviderId;
                        serialization->cbSerialization = length;
                        serialization->rgbSerialization = buffer;
                        buffer = nullptr;
                        *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
                        Trace(L"ProviderCredentialSerialized");
                    }
                }
                if (buffer) { SecureZeroMemory(buffer, length); CoTaskMemFree(buffer); }
            }
        }
        password_.clear(); state_ = State::Failed;
        if (FAILED(result)) { Trace(L"ProviderSerializationFailed", result); *icon = CPSI_ERROR; CopyString(L"无法提交凭据，请使用其他 Windows 登录方式。", text); }
        return result;
    }
    IFACEMETHODIMP ReportResult(NTSTATUS status, NTSTATUS substatus, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override {
        Trace(L"ProviderWindowsResult", static_cast<HRESULT>(status), static_cast<DWORD>(substatus));
        if (!text || !icon) return E_POINTER;
        *text = nullptr; *icon = CPSI_NONE;
        { std::lock_guard lock(mutex_); password_.clear(); state_ = State::Failed; }
        if (status != 0) {
            *icon = CPSI_ERROR;
            return CopyString(L"Windows 未接受保存的凭据。请用 PIN/密码登录后，在 YubiLatch 更新账户密码；不会自动重复尝试。", text);
        }
        return S_OK;
    }
};

class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray {
    std::atomic<ULONG> refs_{1};
    std::vector<Credential*> credentials_;
    ICredentialProviderEvents* events_ = nullptr;
    UINT_PTR context_ = 0;
    bool supported_ = false;
    void Clear() { for (auto item : credentials_) if (item) { item->Stop(); item->SetProviderEvents(nullptr, 0); item->Release(); } credentials_.clear(); }
public:
    Provider() { ++Objects; }
    ~Provider() { Clear(); if (events_) events_->Release(); --Objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(ICredentialProvider)) *result = static_cast<ICredentialProvider*>(this);
        else if (iid == __uuidof(ICredentialProviderSetUserArray)) *result = static_cast<ICredentialProviderSetUserArray*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    IFACEMETHODIMP_(ULONG) Release() override { auto n = --refs_; if (!n) delete this; return n; }
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, DWORD) override {
        supported_ = (scenario == CPUS_LOGON || scenario == CPUS_UNLOCK_WORKSTATION) && !GetSystemMetrics(SM_REMOTESESSION);
        return supported_ ? S_OK : E_NOTIMPL;
    }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }
    IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR context) override {
        if (events_) events_->Release();
        events_ = events; context_ = context;
        if (events_) events_->AddRef();
        for (auto item : credentials_) item->SetProviderEvents(events_, context_);
        return S_OK;
    }
    IFACEMETHODIMP UnAdvise() override {
        for (auto item : credentials_) { item->Stop(); item->SetProviderEvents(nullptr, 0); }
        if (events_) { events_->Release(); events_ = nullptr; }
        return S_OK;
    }
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override { if (!count) return E_POINTER; *count = 4; return S_OK; }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (index > 3) return E_INVALIDARG;
        auto descriptor = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!descriptor) return E_OUTOFMEMORY;
        *descriptor = {};
        descriptor->dwFieldID = index;
        constexpr CREDENTIAL_PROVIDER_FIELD_TYPE types[]{CPFT_TILE_IMAGE,CPFT_LARGE_TEXT,CPFT_SMALL_TEXT,CPFT_SUBMIT_BUTTON};
        descriptor->cpft = types[index];
        auto hr = CopyString(L"YubiLatch", &descriptor->pszLabel);
        if (FAILED(hr)) { CoTaskMemFree(descriptor); return hr; }
        *result = descriptor; return S_OK;
    }
    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* defaultIndex, BOOL* automatic) override {
        if (!count || !defaultIndex || !automatic) return E_POINTER;
        *count = supported_ ? static_cast<DWORD>(credentials_.size()) : 0;
        *defaultIndex = CREDENTIAL_PROVIDER_NO_DEFAULT; *automatic = FALSE;
        for (DWORD i = 0; i < *count; ++i) if (credentials_[i]->Ready()) { *defaultIndex = i; *automatic = TRUE; Trace(L"ProviderAutoLogonReady"); break; }
        return S_OK;
    }
    IFACEMETHODIMP GetCredentialAt(DWORD index, ICredentialProviderCredential** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (index >= credentials_.size()) return E_INVALIDARG;
        return credentials_[index]->QueryInterface(IID_PPV_ARGS(result));
    }
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users) override {
        if (!supported_ || !users) { Clear(); return S_OK; }
        DWORD count = 0;
        if (FAILED(users->GetCount(&count)) || count > 64) { Clear(); return S_OK; }
        std::vector<Credential*> next;
        try { next.reserve(count); } catch (...) { return E_OUTOFMEMORY; }
        Handle cancel(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        for (DWORD i = 0; i < count; ++i) {
            ICredentialProviderUser* user = nullptr;
            if (FAILED(users->GetAt(i, &user))) continue;
            PWSTR sid = nullptr, qualified = nullptr;
            user->GetSid(&sid);
            user->GetStringValue(PKEY_Identity_QualifiedUserName, &qualified);
            try {
                Secret unused;
                Credential** previous = nullptr;
                if (sid && qualified) for (auto& item : credentials_) if (item && item->IsAccount(sid, qualified)) { previous = &item; break; }
                // Reuse an enumerated account without a second query racing the completed reply.
                // Every new Acquire still checks enabled/revoked state in the service.
                if (sid && qualified && _wcsnicmp(qualified, L"MicrosoftAccount\\", 17) == 0
                    && (previous || AskService(sid, 1, cancel.value, unused))) {
                    Credential* credential = previous ? *previous : nullptr;
                    if (previous) { *previous = nullptr; Trace(L"ProviderCredentialReused"); }
                    if (!credential) credential = new Credential(sid, qualified);
                    credential->SetProviderEvents(events_, context_);
                    next.push_back(credential); // capacity reserved above; no allocation here
                }
            } catch (...) { /* Never throw across the LogonUI COM boundary. */ }
            CoTaskMemFree(sid); CoTaskMemFree(qualified); user->Release();
        }
        Clear();
        credentials_.swap(next);
        return S_OK;
    }
};

class Factory final : public IClassFactory {
    std::atomic<ULONG> refs_{1};
public:
    Factory() { ++Objects; }
    ~Factory() { --Objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    IFACEMETHODIMP_(ULONG) Release() override { auto n = --refs_; if (!n) delete this; return n; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try { auto provider = new Provider(); auto hr = provider->QueryInterface(iid, result); provider->Release(); return hr; }
        catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) ++Objects; else --Objects; return S_OK; }
};
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return Objects == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != ProviderId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    auto hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
