// SPDX-License-Identifier: GPL-3.0-or-later
// Small SCM host for the pinned Windhawk 1.7.3 engine API used by upstream
// app/engine_control.h. No UI, login script, network client or settings editor.
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <aclapi.h>
#include <bcrypt.h>
#include <tlhelp32.h>
#include <winevt.h>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include "service-session.h"
#include "service-compatibility.h"
#include "service-start-arguments.h"
#include "service-startup-trace.h"
#include "service-cold-diagnostic.h"
#include "shell-fingerprint.h"
#include "service-assets.generated.h"

namespace {
constexpr wchar_t ServiceName[]=L"ClassicDeskShell";
SERVICE_STATUS_HANDLE statusHandle;
HANDLE stopEvent;
std::wstring root;
std::atomic<DWORD> checkpoint{0};
std::atomic<bool> crashTrip{false};
SRWLOCK crashLock=SRWLOCK_INIT;
ClassicDeskService::CrashWindow crashes;
ClassicDeskService::ServiceRuntimeMetadata runtimeMetadata;
void Report(DWORD state,DWORD error=0) {
    SERVICE_STATUS value{};
    value.dwServiceType=SERVICE_WIN32_OWN_PROCESS;value.dwCurrentState=state;
    value.dwControlsAccepted=state==SERVICE_RUNNING?SERVICE_ACCEPT_STOP|SERVICE_ACCEPT_SHUTDOWN:0;
    value.dwWin32ExitCode=error;
    value.dwCheckPoint=state==SERVICE_START_PENDING||state==SERVICE_STOP_PENDING?++checkpoint:0;
    value.dwWaitHint=value.dwCheckPoint?15000:0;
    SetServiceStatus(statusHandle,&value);
}
std::wstring Base() {
    wchar_t path[32768]{};
    DWORD length=GetModuleFileNameW(nullptr,path,ARRAYSIZE(path));
    if(!length || length>=ARRAYSIZE(path)) return {};
    std::wstring result(path,length);auto slash=result.find_last_of(L'\\');
    return slash==result.npos?L"":result.substr(0,slash);
}
bool RegularChain(std::wstring path) {
    while(path.size()>3) {
        DWORD attributes=GetFileAttributesW(path.c_str());
        if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        auto slash=path.find_last_of(L'\\');if(slash==path.npos) return false;
        path.resize(slash);
    }
    return true;
}
bool Protected(std::wstring path) {
    if(!RegularChain(path)) return false;
    PACL acl=nullptr;PSID owner=nullptr;PSECURITY_DESCRIPTOR descriptor=nullptr;
    DWORD result=GetNamedSecurityInfoW(path.c_str(),SE_FILE_OBJECT,DACL_SECURITY_INFORMATION|OWNER_SECURITY_INFORMATION,
        &owner,nullptr,&acl,nullptr,&descriptor);
    if(result!=ERROR_SUCCESS || !acl) {if(descriptor)LocalFree(descriptor);return false;}
    BYTE admin[SECURITY_MAX_SID_SIZE]{},system[SECURITY_MAX_SID_SIZE]{};
    DWORD size=sizeof(admin);CreateWellKnownSid(WinBuiltinAdministratorsSid,nullptr,admin,&size);
    size=sizeof(system);CreateWellKnownSid(WinLocalSystemSid,nullptr,system,&size);
    bool safe=owner && (EqualSid(owner,admin) || EqualSid(owner,system));
    for(DWORD i=0;i<acl->AceCount;i++) {
        void* raw=nullptr;if(!GetAce(acl,i,&raw)){safe=false;break;}
        auto ace=(ACCESS_ALLOWED_ACE*)raw;
        if(ace->Header.AceFlags&INHERIT_ONLY_ACE) continue;
        if(ace->Header.AceType==ACCESS_DENIED_ACE_TYPE) continue;
        if(ace->Header.AceType!=ACCESS_ALLOWED_ACE_TYPE){safe=false;break;}
        constexpr DWORD writes=FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_EA|FILE_WRITE_ATTRIBUTES|DELETE|WRITE_DAC|WRITE_OWNER|GENERIC_WRITE|GENERIC_ALL;
        if((ace->Mask&writes) && !EqualSid(&ace->SidStart,admin) && !EqualSid(&ace->SidStart,system)){safe=false;break;}
    }
    LocalFree(descriptor);return safe;
}
bool ProtectedChain(std::wstring path) {
    wchar_t programFiles[32768]{};
    if(!GetEnvironmentVariableW(L"ProgramW6432",programFiles,ARRAYSIZE(programFiles)))return false;
    const auto base=std::wstring(programFiles)+L"\\ClassicDesk";
    const auto prefix=base+L"\\ShellService\\";
    if(root.size()<=prefix.size() || _wcsnicmp(root.c_str(),prefix.c_str(),prefix.size()))return false;
    while(path.size()>=base.size()) {
        if(!Protected(path))return false;
        if(!_wcsicmp(path.c_str(),base.c_str()))return true;
        auto slash=path.find_last_of(L'\\');if(slash==path.npos)return false;path.resize(slash);
    }
    return false;
}
bool DigestMatches(const std::wstring& path,const char* expected,ULONGLONG length) {
    if(!RegularChain(path)) return false;
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER actual{};
    bool valid=GetFileSizeEx(file,&actual) && actual.QuadPart==(LONGLONG)length;
    BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
    DWORD objectSize=0,received=0;
    if(valid) valid=BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0;
    if(valid) valid=BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,(PUCHAR)&objectSize,sizeof(objectSize),&received,0)>=0;
    std::vector<UCHAR> object(objectSize);
    if(valid) valid=BCryptCreateHash(algorithm,&hash,object.data(),objectSize,nullptr,0,0)>=0;
    BYTE buffer[32768],digest[32]{};DWORD read=0;
    while(valid) {
        if(!ReadFile(file,buffer,sizeof(buffer),&read,nullptr)){valid=false;break;}
        if(!read)break;
        if(BCryptHashData(hash,buffer,read,0)<0){valid=false;break;}
    }
    if(valid) valid=BCryptFinishHash(hash,digest,sizeof(digest),0)>=0;
    char hex[65]{};for(int i=0;i<32;i++)std::sprintf(hex+i*2,"%02x",digest[i]);
    valid=valid && std::string(hex)==expected;
    if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);CloseHandle(file);
    return valid;
}
bool Verify(bool protectedInstall) {
    if(root.empty() || !RegularChain(root)) return false;
    if(protectedInstall && !ProtectedChain(root)) return false;
    for(auto& asset:Assets) {
        auto path=root+L"\\Runtime\\"+asset.path;
        if(!DigestMatches(path,asset.sha256,asset.bytes))return false;
        if(protectedInstall) {
            // Check each executable and its directory; writable caches never contain code assets.
            if(!ProtectedChain(path))return false;
        }
    }
    for(auto file:{L"Runtime\\Engine\\1.7.3\\engine.ini",L"Runtime\\AppData\\Engine\\settings.ini"}) {
        auto path=root+L"\\"+file;
        if(!RegularChain(path) || (protectedInstall && !ProtectedChain(path)))return false;
    }
    // Module configuration is executable policy and must be protected too.
    const auto mods=root+L"\\Runtime\\AppData\\Engine\\Mods";
    WIN32_FIND_DATAW entry{};HANDLE files=FindFirstFileW((mods+L"\\*.ini").c_str(),&entry);
    if(files==INVALID_HANDLE_VALUE)return false;
    bool validMods=true;unsigned count=0;
    do {
        auto path=mods+L"\\"+entry.cFileName;
        if(++count>6 || !RegularChain(path) || (protectedInstall && !ProtectedChain(path))) {validMods=false;break;}
    }while(FindNextFileW(files,&entry));
    DWORD enumerationError=GetLastError();
    FindClose(files);if(!validMods || enumerationError!=ERROR_NO_MORE_FILES || count!=6)return false;
    const auto ini=root+L"\\Runtime\\Engine\\1.7.3\\engine.ini";
    wchar_t data[512]{};
    GetPrivateProfileStringW(L"Storage",L"AppDataPath",L"",data,ARRAYSIZE(data),ini.c_str());
    if(GetPrivateProfileIntW(L"Storage",L"Portable",0,ini.c_str())!=1 || std::wstring(data)!=L"..\\..\\AppData\\Engine")return false;
    const auto settings=root+L"\\Runtime\\AppData\\Engine\\settings.ini";
    GetPrivateProfileStringW(L"Settings",L"Include",L"",data,ARRAYSIZE(data),settings.c_str());
    if(std::wstring(data)!=EngineInclude) return false;
    GetPrivateProfileStringW(L"Settings",L"Exclude",L"",data,ARRAYSIZE(data),settings.c_str());
    return std::wstring(data)==L"*" && GetPrivateProfileIntW(L"Settings",L"SafeMode",1,settings.c_str())==0 &&
        GetPrivateProfileIntW(L"Settings",L"InjectIntoCriticalProcesses",0,settings.c_str())==0;
}
const wchar_t* StartModeName(ClassicDeskService::StartupMode mode) {
    switch(mode) {
        case ClassicDeskService::StartupMode::Normal:return L"normal";
        case ClassicDeskService::StartupMode::SessionOnly:return L"session-only";
        case ClassicDeskService::StartupMode::ColdDiagnostic:return L"cold-diagnostic";
        default:return L"unknown";
    }
}
void WriteState(const wchar_t* state,DWORD error=0) {
    auto file=root+L"\\service-state.ini";
    // A validation failure must not turn an untrusted path into a privileged write.
    if(root.empty() || !ProtectedChain(root))return;
    DWORD attributes=GetFileAttributesW(file.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES) {
        if(GetLastError()!=ERROR_FILE_NOT_FOUND)return;
        // First service start creates the state file. Never overwrite an existing
        // target or follow a reparse point, even if a creator races this check.
        HANDLE created=CreateFileW(file.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,
            CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        if(created==INVALID_HANDLE_VALUE)return;
        CloseHandle(created);
        attributes=GetFileAttributesW(file.c_str());
    }
    if(attributes==INVALID_FILE_ATTRIBUTES ||
        (attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) ||
        !ProtectedChain(file))return;
    wchar_t number[64]{};std::swprintf(number,ARRAYSIZE(number),L"%lu",GetCurrentProcessId());
    WritePrivateProfileStringW(L"Service",L"ProcessId",number,file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%llu",GetTickCount64());
    WritePrivateProfileStringW(L"Service",L"BootElapsedMs",number,file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%lu",error);
    WritePrivateProfileStringW(L"Service",L"Error",number,file.c_str());
    WritePrivateProfileStringW(L"Service",L"State",state,file.c_str());
    if(!std::wcsncmp(state,L"cold-diagnostic-",16) || !std::wcsncmp(state,L"running-cold-diagnostic-",24)) {
        // Preserve the diagnostic decision when generic engine cleanup later
        // changes State to failed/stopped. This is never a compatibility receipt.
        WritePrivateProfileStringW(L"Service",L"DiagnosticState",state,file.c_str());
        WritePrivateProfileStringW(L"Service",L"DiagnosticError",number,file.c_str());
    }
    WritePrivateProfileStringW(L"Service",L"EngineStarted",runtimeMetadata.EngineStarted()?L"1":L"0",file.c_str());
    WritePrivateProfileStringW(L"Service",L"StartMode",StartModeName(runtimeMetadata.Mode()),file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%lu",runtimeMetadata.SessionId());
    WritePrivateProfileStringW(L"Service",L"SessionId",number,file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%lu",runtimeMetadata.ExplorerPid());
    WritePrivateProfileStringW(L"Service",L"ExplorerPid",number,file.c_str());
}
void RecordFailure(ClassicDeskService::StartupTrace& trace,DWORD error) {
    if(!trace.HasFailure())trace.Record(ClassicDeskService::StartupStage::Failure,
        ClassicDeskService::StartupOutcome::Failed,GetTickCount64(),0,error);
}
bool InspectTraceFile(const std::wstring& path,bool& exists) {
    exists=false;
    const DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES) {
        const DWORD error=GetLastError();
        return error==ERROR_FILE_NOT_FOUND || error==ERROR_PATH_NOT_FOUND;
    }
    if((attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) || !ProtectedChain(path))return false;
    HANDLE file=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ,nullptr,OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    if(file==INVALID_HANDLE_VALUE)return false;
    BY_HANDLE_FILE_INFORMATION information{};
    const bool valid=GetFileInformationByHandle(file,&information) &&
        GetFileType(file)==FILE_TYPE_DISK &&
        (static_cast<ULONGLONG>(information.nFileSizeHigh)<<32|information.nFileSizeLow)<=
            ClassicDeskService::StartupTrace::MaximumBytes;
    CloseHandle(file);
    if(!valid)return false;
    exists=true;return true;
}
bool DeleteTraceFile(const std::wstring& path) {
    bool exists=false;
    if(!InspectTraceFile(path,exists))return false;
    return !exists || DeleteFileW(path.c_str())!=FALSE;
}
class StartupTraceFile {
    ClassicDeskService::StartupTrace& trace;
    HANDLE file=INVALID_HANDLE_VALUE;
    std::size_t nextEntry=0,bytesWritten=0;
    bool Write(const char* data,std::size_t length) {
        if(file==INVALID_HANDLE_VALUE || length>ClassicDeskService::StartupTrace::MaximumBytes-bytesWritten)return false;
        std::size_t offset=0;
        while(offset<length) {
            DWORD count=0;
            if(!WriteFile(file,data+offset,static_cast<DWORD>(length-offset),&count,nullptr) || !count)return false;
            offset+=count;bytesWritten+=count;
        }
        return true;
    }
    void Close() {if(file!=INVALID_HANDLE_VALUE){CloseHandle(file);file=INVALID_HANDLE_VALUE;}}
    void Append() {
        if(file==INVALID_HANDLE_VALUE)return;
        char buffer[512]{};
        while(nextEntry<trace.Count()) {
            std::size_t length=0;
            if(!trace.SerializeEntry(nextEntry,buffer,sizeof(buffer),length) || !Write(buffer,length)){Close();return;}
            ++nextEntry;
        }
    }
    static void OnRecord(const ClassicDeskService::StartupTrace&,void* context) {
        static_cast<StartupTraceFile*>(context)->Append();
    }
public:
    explicit StartupTraceFile(ClassicDeskService::StartupTrace& value):trace(value) {
        if(root.empty() || !ProtectedChain(root))return;
        const auto current=root+L"\\startup-trace.log",previous=root+L"\\startup-trace.previous.log";
        bool currentExists=false,previousExists=false;
        if(!InspectTraceFile(current,currentExists) || !InspectTraceFile(previous,previousExists))return;
        if(currentExists) {
            if(previousExists && !DeleteTraceFile(previous))return;
            if(!MoveFileExW(current.c_str(),previous.c_str(),0))return;
        }
        file=CreateFileW(current.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        if(file==INVALID_HANDLE_VALUE)return;
        if(!ProtectedChain(current)){Close();return;}
        char header[512]{};std::size_t length=0;
        if(!trace.SerializeHeader(GetCurrentProcessId(),header,sizeof(header),length) || !Write(header,length)){Close();return;}
        trace.SetSink(OnRecord,this);
        Append();
    }
    // Each bounded event is written before the next engine call, so a hung API
    // leaves a begin marker readable. File I/O is best effort, not hard real-time;
    // there is no per-scan logging, retry worker, or power-loss durability claim.
    ~StartupTraceFile(){trace.SetSink(nullptr);Close();}
    bool Ready() const {return file!=INVALID_HANDLE_VALUE;}
    StartupTraceFile(const StartupTraceFile&)=delete;
    StartupTraceFile& operator=(const StartupTraceFile&)=delete;
};
bool EnableDebugPrivilege() {
    HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_ADJUST_PRIVILEGES|TOKEN_QUERY,&token))return false;
    TOKEN_PRIVILEGES privileges{};privileges.PrivilegeCount=1;
    bool valid=LookupPrivilegeValueW(nullptr,SE_DEBUG_NAME,&privileges.Privileges[0].Luid);
    privileges.Privileges[0].Attributes=SE_PRIVILEGE_ENABLED;
    if(valid){SetLastError(ERROR_SUCCESS);valid=AdjustTokenPrivileges(token,FALSE,&privileges,0,nullptr,nullptr)&&GetLastError()==ERROR_SUCCESS;}
    CloseHandle(token);return valid;
}
bool NoOtherEngine() {
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snapshot==INVALID_HANDLE_VALUE)return false;
    PROCESSENTRY32W entry{sizeof(entry)};bool safe=Process32FirstW(snapshot,&entry);
    if(safe)do {
        if(entry.th32ProcessID!=GetCurrentProcessId() &&
            (!_wcsicmp(entry.szExeFile,L"windhawk.exe") || !_wcsicmp(entry.szExeFile,L"ClassicDeskShell.exe"))){safe=false;break;}
    }while(Process32NextW(snapshot,&entry));
    CloseHandle(snapshot);return safe;
}
DWORD WINAPI OnCrash(EVT_SUBSCRIBE_NOTIFY_ACTION action,PVOID,EVT_HANDLE) {
    if(action==EvtSubscribeActionDeliver) {
        auto now=GetTickCount64();AcquireSRWLockExclusive(&crashLock);
        bool repeated=crashes.Add(now);ReleaseSRWLockExclusive(&crashLock);
        if(!repeated)return 0;
    }
    // A lost subscription cannot establish that restarting Explorer is safe.
    crashTrip=true;if(stopEvent)SetEvent(stopEvent);return 0;
}
DWORD DisableNextBoot() {
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);if(!manager)return GetLastError();
    SC_HANDLE service=OpenServiceW(manager,ServiceName,SERVICE_CHANGE_CONFIG);
    DWORD error=service?ERROR_SUCCESS:GetLastError();
    if(service){if(!ChangeServiceConfigW(service,SERVICE_NO_CHANGE,SERVICE_DISABLED,SERVICE_NO_CHANGE,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr))error=GetLastError();CloseServiceHandle(service);}
    CloseServiceHandle(manager);return error;
}
struct ScopedHandle {
    HANDLE value=nullptr;
    ~ScopedHandle(){if(value && value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
    ScopedHandle()=default;
    ScopedHandle(const ScopedHandle&)=delete;
    ScopedHandle& operator=(const ScopedHandle&)=delete;
};
using ClassicDeskService::TrialIdentity;
ULONGLONG CurrentUtc() {
    FILETIME value{};GetSystemTimeAsFileTime(&value);
    return static_cast<ULONGLONG>(value.dwHighDateTime)<<32|value.dwLowDateTime;
}
std::wstring ColdBinding(const std::wstring& fingerprint) {
    const auto receipt=root+L"\\install-record.json";
    if(!ClassicDeskService::IsFingerprint(fingerprint) || !ProtectedChain(receipt) ||
       !ProtectedChain(root+L"\\ClassicDeskShell.exe") ||
       !ProtectedChain(root+L"\\Runtime\\runtime-assets-manifest.json"))return {};
    ClassicDeskService::FingerprintBuilder builder;
    // A copy in another installation, changed install ownership/profile receipt,
    // or changed effective component/settings fingerprint cannot reuse a permit.
    if(!builder.Text(L"ClassicDesk-cold-diagnostic-v2") || !builder.Text(root) ||
       !builder.Text(fingerprint) || !builder.File(L"install-record",receipt))return {};
    return builder.Finish();
}
bool NoShellProcesses() {
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snapshot==INVALID_HANDLE_VALUE)return false;
    PROCESSENTRY32W entry{sizeof(entry)};bool safe=Process32FirstW(snapshot,&entry)!=FALSE;
    if(safe)do {
        if(!_wcsicmp(entry.szExeFile,L"explorer.exe") || !_wcsicmp(entry.szExeFile,L"StartMenuExperienceHost.exe")){safe=false;break;}
    }while(Process32NextW(snapshot,&entry));
    if(safe && GetLastError()!=ERROR_NO_MORE_FILES)safe=false;
    CloseHandle(snapshot);return safe;
}
#pragma pack(push,1)
struct ColdPermitBlob {
    DWORD magic=0x44434443,schema=2;
    ULONGLONG createdUtc=0,expiresUtc=0;
    ULONGLONG preparedUptimeMs=0,reserved=0;
    TrialIdentity trial{};
    char fingerprint[64]{},binding[64]{};
};
#pragma pack(pop)
static_assert(sizeof(ColdPermitBlob)==184);
std::wstring ColdPermitPath(bool consumed=false) {return root+(consumed?L"\\cold-diagnostic.consumed.bin":L"\\cold-diagnostic.permit.bin");}
DWORD ConsumeColdPermitFile(const std::wstring& active,const std::wstring& consumed) {
    // MoveFileEx on the same source can report success to two racing callers.
    // Hold an exclusive DELETE handle throughout the non-replacing rename so
    // only one caller can claim this file identity, then flush before proceeding.
    ScopedHandle file;file.value=CreateFileW(active.c_str(),DELETE|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_WRITE_THROUGH,nullptr);
    if(file.value==INVALID_HANDLE_VALUE)return GetLastError();
    BY_HANDLE_FILE_INFORMATION information{};
    if(!GetFileInformationByHandle(file.value,&information))return GetLastError();
    if(information.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY) || information.nNumberOfLinks!=1)
        return ERROR_INVALID_DATA;
    const DWORD nameBytes=static_cast<DWORD>(consumed.size()*sizeof(wchar_t));
    const DWORD size=static_cast<DWORD>(offsetof(FILE_RENAME_INFO,FileName))+nameBytes+sizeof(wchar_t);
    std::vector<BYTE> storage(size);auto rename=reinterpret_cast<FILE_RENAME_INFO*>(storage.data());
    rename->ReplaceIfExists=FALSE;rename->RootDirectory=nullptr;rename->FileNameLength=nameBytes;
    std::memcpy(rename->FileName,consumed.data(),nameBytes);
    if(!SetFileInformationByHandle(file.value,FileRenameInfo,rename,size))return GetLastError();
    return FlushFileBuffers(file.value)?ERROR_SUCCESS:GetLastError();
}
bool ColdPermitPresent() {
    const auto attributes=GetFileAttributesW(ColdPermitPath().c_str());
    // Access errors must not silently bypass the diagnostic path.
    return attributes!=INVALID_FILE_ATTRIBUTES || GetLastError()!=ERROR_FILE_NOT_FOUND;
}
bool ReadColdPermit(ColdPermitBlob& blob) {
    const auto path=ColdPermitPath(true);
    if(!ProtectedChain(path))return false;
    ScopedHandle handle;handle.value=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    if(handle.value==INVALID_HANDLE_VALUE)return false;
    BY_HANDLE_FILE_INFORMATION information{};DWORD read=0;
    return GetFileInformationByHandle(handle.value,&information) &&
        !(information.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)) &&
        information.nNumberOfLinks==1 && !information.nFileSizeHigh && information.nFileSizeLow==sizeof(blob) &&
        ReadFile(handle.value,&blob,sizeof(blob),&read,nullptr) && read==sizeof(blob) && blob.magic==0x44434443;
}
struct ColdDiagnosticAdapter {
    DWORD error=ERROR_INVALID_DATA;
    const wchar_t* Disable() {
        error=DisableNextBoot();
        return error?L"cold-diagnostic-disable-failed":nullptr;
    }
    const wchar_t* Consume() {
        error=ERROR_INVALID_DATA;
        const auto active=ColdPermitPath(),consumed=ColdPermitPath(true);
        if(!ProtectedChain(active))return L"cold-diagnostic-permit-unprotected";
        // No replacement: concurrent claim/replay or an unarchived previous trial
        // fails. Rename is atomic on the same volume and happens before reading.
        error=ConsumeColdPermitFile(active,consumed);
        if(error)return L"cold-diagnostic-consume-failed";
        WriteState(L"cold-diagnostic-consumed-unverified");
        return nullptr;
    }
    const wchar_t* Validate() {
        error=ERROR_INVALID_DATA;ColdPermitBlob blob{};
        if(!ReadColdPermit(blob) || blob.reserved)return L"cold-diagnostic-invalid-permit";
        ClassicDeskService::ColdDiagnosticPermit permit;
        permit.schema=blob.schema;permit.preparedUptimeMs=blob.preparedUptimeMs;permit.trialId=blob.trial;
        permit.createdUtc=blob.createdUtc;permit.expiresUtc=blob.expiresUtc;
        for(auto c:blob.fingerprint)permit.fingerprint+=static_cast<unsigned char>(c);
        for(auto c:blob.binding)permit.binding+=static_cast<unsigned char>(c);
        const auto fingerprint=ClassicDeskService::ShellFingerprint(root);
        const auto decision=ClassicDeskService::ColdDiagnosticDecision(permit,CurrentUtc(),GetTickCount64(),
            fingerprint,ColdBinding(fingerprint),NoShellProcesses());
        if(std::wstring(decision)==L"cold-diagnostic-authorized-unverified")error=ERROR_SUCCESS;
        return decision;
    }
};
bool OwnedColdArmState(SC_HANDLE service,ClassicDeskService::ColdArmIdentity& identity,DWORD expectedStart=SERVICE_DISABLED) {
    DWORD bytes=0;QueryServiceConfigW(service,nullptr,0,&bytes);
    if(!bytes || bytes>65536)return false;
    std::vector<BYTE> storage(bytes);auto config=reinterpret_cast<QUERY_SERVICE_CONFIGW*>(storage.data());
    SERVICE_STATUS_PROCESS status{};DWORD returned=0;
    const auto image=L"\""+root+L"\\ClassicDeskShell.exe\" --service";
    if(!(QueryServiceConfigW(service,config,bytes,&bytes) &&
        config->dwServiceType==SERVICE_WIN32_OWN_PROCESS && config->dwStartType==expectedStart &&
        config->lpBinaryPathName && image==config->lpBinaryPathName && config->lpServiceStartName &&
        !_wcsicmp(config->lpServiceStartName,L"LocalSystem") &&
        QueryServiceStatusEx(service,SC_STATUS_PROCESS_INFO,reinterpret_cast<BYTE*>(&status),sizeof(status),&returned)))return false;
    identity={};
    SERVICE_DELAYED_AUTO_START_INFO delayed{};
    if(!QueryServiceConfig2W(service,SERVICE_CONFIG_DELAYED_AUTO_START_INFO,reinterpret_cast<BYTE*>(&delayed),sizeof(delayed),&returned) || delayed.fDelayedAutostart)return false;
    if(status.dwCurrentState==SERVICE_STOPPED && status.dwProcessId==0)return true;
    if(status.dwCurrentState!=SERVICE_RUNNING || !status.dwProcessId)return false;
    ScopedHandle process;process.value=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,status.dwProcessId);
    if(!process.value)return false;
    wchar_t actual[32768]{};DWORD length=ARRAYSIZE(actual);
    const auto expectedImage=root+L"\\ClassicDeskShell.exe";
    if(!QueryFullProcessImageNameW(process.value,0,actual,&length) || _wcsicmp(actual,expectedImage.c_str()))return false;
    FILETIME created{},exited{},kernel{},user{};
    if(!GetProcessTimes(process.value,&created,&exited,&kernel,&user))return false;
    identity={true,status.dwProcessId,(static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime};
    const auto state=root+L"\\service-state.ini";
    if(!ProtectedChain(state))return false;
    wchar_t mode[64]{},value[128]{};
    GetPrivateProfileStringW(L"Service",L"StartMode",L"",mode,ARRAYSIZE(mode),state.c_str());
    GetPrivateProfileStringW(L"Service",L"State",L"",value,ARRAYSIZE(value),state.c_str());
    const bool verified=std::wstring(mode)==L"session-only" && std::wstring(value)==L"running-current-session-unverified" &&
        GetPrivateProfileIntW(L"Service",L"ProcessId",0,state.c_str())==status.dwProcessId &&
        GetPrivateProfileIntW(L"Service",L"EngineStarted",0,state.c_str())==1 &&
        GetPrivateProfileIntW(L"Service",L"Error",1,state.c_str())==0 &&
        GetPrivateProfileIntW(L"Service",L"SessionId",0,state.c_str())!=0 &&
        GetPrivateProfileIntW(L"Service",L"ExplorerPid",0,state.c_str())!=0 &&
        WaitForSingleObject(process.value,0)==WAIT_TIMEOUT;
    return ClassicDeskService::ValidColdArmIdentity(identity,verified);
}
DWORD ArmColdDiagnostic(const std::wstring& expected,std::uint64_t minutes,bool nextBoot=false) {
    if(!ClassicDeskService::IsFingerprint(expected) || (nextBoot?minutes!=0:minutes<1 || minutes>360))return ERROR_INVALID_PARAMETER;
    if(!Verify(true))return ERROR_INVALID_DATA;
    const auto fingerprint=ClassicDeskService::ShellFingerprint(root),binding=ColdBinding(fingerprint);
    if(fingerprint!=expected || !ClassicDeskService::IsFingerprint(binding))return ERROR_REVISION_MISMATCH;
    ColdPermitBlob blob{};
    blob.preparedUptimeMs=GetTickCount64();
    if(blob.preparedUptimeMs<ClassicDeskService::MinimumPreparedUptimeMs)return ERROR_NOT_READY;
    if(BCryptGenRandom(nullptr,blob.trial.data(),blob.trial.size(),BCRYPT_USE_SYSTEM_PREFERRED_RNG)<0)
        return ERROR_NOT_SUPPORTED;
    blob.schema=nextBoot?3:2;
    blob.createdUtc=CurrentUtc();blob.expiresUtc=nextBoot?0:blob.createdUtc+minutes*60*ClassicDeskService::FileTimeSecond;
    for(unsigned i=0;i<64;i++){blob.fingerprint[i]=static_cast<char>(fingerprint[i]);blob.binding[i]=static_cast<char>(binding[i]);}
    // Serialize arming operations with a protected, non-overwritten lock file.
    ScopedHandle lock;lock.value=CreateFileW((root+L"\\cold-diagnostic.lock").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL|FILE_FLAG_DELETE_ON_CLOSE|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    if(lock.value==INVALID_HANDLE_VALUE)return GetLastError();
    const auto consumed=ColdPermitPath(true);
    if(GetFileAttributesW(consumed.c_str())!=INVALID_FILE_ATTRIBUTES || GetLastError()!=ERROR_FILE_NOT_FOUND)return ERROR_ALREADY_EXISTS;
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);
    if(!manager)return GetLastError();
    SC_HANDLE service=OpenServiceW(manager,ServiceName,SERVICE_QUERY_CONFIG|SERVICE_QUERY_STATUS|SERVICE_CHANGE_CONFIG);
    DWORD error=service?ERROR_SUCCESS:GetLastError();
    if(service) {
        ClassicDeskService::ColdArmIdentity before;
        if(!OwnedColdArmState(service,before))error=ERROR_INVALID_STATE;
        else {
            const auto path=ColdPermitPath();ScopedHandle file;
            file.value=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL|FILE_FLAG_WRITE_THROUGH|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
            if(file.value==INVALID_HANDLE_VALUE)error=GetLastError();
            else {
                DWORD written=0;
                if(!ProtectedChain(path) || !WriteFile(file.value,&blob,sizeof(blob),&written,nullptr) ||
                   written!=sizeof(blob) || !FlushFileBuffers(file.value))error=ERROR_WRITE_FAULT;
                CloseHandle(file.value);file.value=nullptr;
                // Permit is durable before Auto is set. A crash before this step
                // leaves Disabled; a crash after it leaves a one-use guarded boot.
                ClassicDeskService::ColdArmIdentity current;
                if(!error && (!OwnedColdArmState(service,current) || current!=before))error=ERROR_INVALID_STATE;
                if(!error && !ChangeServiceConfigW(service,SERVICE_NO_CHANGE,SERVICE_AUTO_START,SERVICE_NO_CHANGE,
                    nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr))error=GetLastError();
                if(!error && (!OwnedColdArmState(service,current,SERVICE_AUTO_START) || current!=before))error=ERROR_INVALID_STATE;
                if(error) {
                    // Never roll back to Auto after a failed preparation.
                    // Do not mutate a replacement registration/process after a race.
                    if((OwnedColdArmState(service,current,SERVICE_AUTO_START) || OwnedColdArmState(service,current)) && (current==before || !current.running))
                        ChangeServiceConfigW(service,SERVICE_NO_CHANGE,SERVICE_DISABLED,SERVICE_NO_CHANGE,
                            nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr);
                }
                // Only ServiceMain owns service-state.ini, even if a service
                // starts concurrently with arming. Permit + SCM + the returned
                // result describe preparation; never write the helper PID/mode
                // into the service's live runtime state.
            }
        }
        CloseServiceHandle(service);
    }
    CloseServiceHandle(manager);return error;
}
bool ValidateSessionExplorer(const ClassicDeskService::ServiceStartParameters& parameters,HANDLE& process) {
    process=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,parameters.explorerPid);
    if(!process)return false;
    DWORD session=0;
    if(!ProcessIdToSessionId(parameters.explorerPid,&session) || !session || session!=parameters.sessionId)return false;
    FILETIME created{},exited{},kernel{},user{};
    if(!GetProcessTimes(process,&created,&exited,&kernel,&user))return false;
    const ULONGLONG creation=(static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime;
    if(creation!=parameters.explorerCreationFileTimeUtc)return false;
    wchar_t windows[32768]{},image[32768]{};
    const UINT windowsLength=GetWindowsDirectoryW(windows,ARRAYSIZE(windows));
    if(!windowsLength || windowsLength>=ARRAYSIZE(windows))return false;
    std::wstring expected(windows,windowsLength);
    if(expected.back()!=L'\\')expected+=L'\\';
    expected+=L"explorer.exe";
    DWORD imageLength=ARRAYSIZE(image);
    if(!QueryFullProcessImageNameW(process,0,image,&imageLength) || !imageLength)return false;
    if(_wcsicmp(std::wstring(image,imageLength).c_str(),expected.c_str()))return false;
    return WaitForSingleObject(process,0)==WAIT_TIMEOUT;
}
const wchar_t* Compatibility(std::wstring& fingerprint) {
    fingerprint=ClassicDeskService::ShellFingerprint(root);
    const auto receipt=root+L"\\shell-compatibility.ini";
    bool protectedReceipt=ProtectedChain(receipt);
    wchar_t recorded[128]{};
    unsigned schema=0;bool accepted=false;
    if(protectedReceipt) {
        schema=GetPrivateProfileIntW(L"Compatibility",L"Schema",0,receipt.c_str());
        accepted=GetPrivateProfileIntW(L"Compatibility",L"EnhancedShellAccepted",0,receipt.c_str())==1;
        GetPrivateProfileStringW(L"Compatibility",L"Fingerprint",L"",recorded,ARRAYSIZE(recorded),receipt.c_str());
    }
    return ClassicDeskService::CompatibilityDecision(protectedReceipt,schema,accepted,recorded,fingerprint);
}
struct Engine {
    using StartFn=HANDLE(*)();using ScanFn=BOOL(*)(HANDLE);using EndFn=BOOL(*)(HANDLE);
    HMODULE module=nullptr;StartFn start=nullptr;ScanFn scan=nullptr;EndFn end=nullptr;
    ClassicDeskService::StartupTrace& trace;
    ClassicDeskService::ServiceRuntimeMetadata& metadata;
    DWORD& cleanupError;
    bool firstScan=true;
    Engine(ClassicDeskService::StartupTrace& value,ClassicDeskService::ServiceRuntimeMetadata& state,DWORD& cleanup)
        :trace(value),metadata(state),cleanupError(cleanup) {}
    bool Load(){
        trace.Begin(ClassicDeskService::StartupStage::EngineLoad,GetTickCount64());DWORD error=0;
        auto path=root+L"\\Runtime\\Engine\\1.7.3\\32\\windhawk.dll";
        module=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!module)error=GetLastError();
        else {
            start=(StartFn)GetProcAddress(module,"GlobalHookSessionStart");
            scan=(ScanFn)GetProcAddress(module,"GlobalHookSessionHandleNewProcesses");
            end=(EndFn)GetProcAddress(module,"GlobalHookSessionEnd");
            if(!start || !scan || !end) {error=GetLastError();if(!error)error=ERROR_PROC_NOT_FOUND;}
        }
        const bool loaded=module && start && scan && end;
        const auto ended=GetTickCount64();
        if(!loaded && !error)error=ERROR_MOD_NOT_FOUND;
        trace.Complete(ClassicDeskService::StartupStage::EngineLoad,
            loaded?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
            ended,loaded?0:error);
        if(!loaded)RecordFailure(trace,error);
        return loaded;
    }
    void* Start(){
        trace.Begin(ClassicDeskService::StartupStage::GlobalHookSessionStart,GetTickCount64());
        void* value=start();
        metadata.MarkSessionStarted(value!=nullptr);
        const auto ended=GetTickCount64();
        const DWORD error=value?0:ERROR_DLL_INIT_FAILED;
        trace.Complete(ClassicDeskService::StartupStage::GlobalHookSessionStart,
            value?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
            ended,error);
        if(!value)RecordFailure(trace,error);
        return value;
    }
    bool Scan(void* value){
        const bool initial=firstScan;
        if(initial)trace.Begin(ClassicDeskService::StartupStage::FirstScan,GetTickCount64());
        const bool scanned=scan(value)!=FALSE;
        if(initial) {
            firstScan=false;
            const auto ended=GetTickCount64();
            const DWORD error=scanned?0:ERROR_GEN_FAILURE;
            trace.Complete(ClassicDeskService::StartupStage::FirstScan,
                scanned?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
                ended,error);
        }
        if(!scanned)RecordFailure(trace,ERROR_GEN_FAILURE);
        return scanned;
    }
    void End(void* value){
        trace.Begin(ClassicDeskService::StartupStage::End,GetTickCount64());
        const bool endedSuccessfully=end(value)!=FALSE;
        const auto ended=GetTickCount64();
        metadata.MarkSessionEnded(endedSuccessfully);
        trace.Complete(ClassicDeskService::StartupStage::End,
            endedSuccessfully?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
            ended,endedSuccessfully?0:ERROR_GEN_FAILURE);
        if(!endedSuccessfully){cleanupError=ERROR_GEN_FAILURE;RecordFailure(trace,cleanupError);}
    }
    ~Engine(){if(module)FreeLibrary(module);}
};
DWORD WINAPI Control(DWORD control,DWORD,LPVOID,LPVOID) {
    if(control==SERVICE_CONTROL_STOP || control==SERVICE_CONTROL_SHUTDOWN){Report(SERVICE_STOP_PENDING);if(stopEvent)SetEvent(stopEvent);return NO_ERROR;}
    if(control==SERVICE_CONTROL_INTERROGATE)return NO_ERROR;
    return ERROR_CALL_NOT_IMPLEMENTED;
}
void WINAPI ServiceMain(DWORD argc,LPWSTR* argv) {
    statusHandle=RegisterServiceCtrlHandlerExW(ServiceName,Control,nullptr);if(!statusHandle)return;
    Report(SERVICE_START_PENDING);
    runtimeMetadata.Reset();
    ClassicDeskService::StartupTrace trace(GetTickCount64());
    ClassicDeskService::ServiceStartParameters startParameters;
    if(!ClassicDeskService::ParseServiceStartParameters(argc,argv,ServiceName,startParameters)) {
        RecordFailure(trace,ERROR_INVALID_PARAMETER);
        WriteState(L"invalid-start-parameters",ERROR_INVALID_PARAMETER);Report(SERVICE_STOPPED,ERROR_INVALID_PARAMETER);return;
    }
    const bool sessionOnly=startParameters.mode==ClassicDeskService::StartMode::SessionOnly;
    const bool coldDiagnostic=!sessionOnly && ColdPermitPresent();
    const auto traceMode=sessionOnly?ClassicDeskService::StartupMode::SessionOnly:
        coldDiagnostic?ClassicDeskService::StartupMode::ColdDiagnostic:ClassicDeskService::StartupMode::Normal;
    runtimeMetadata.SetMode(traceMode,startParameters.sessionId,startParameters.explorerPid);
    trace.SetMode(traceMode);
    StartupTraceFile traceFile(trace);
    if(coldDiagnostic) {
        const DWORD disableError=DisableNextBoot();
        if(disableError) {
            RecordFailure(trace,disableError);WriteState(L"cold-diagnostic-disable-failed",disableError);
            Report(SERVICE_STOPPED,disableError);return;
        }
        WriteState(L"cold-diagnostic-validating-unverified");
        if(!traceFile.Ready()) {
            RecordFailure(trace,ERROR_WRITE_FAULT);WriteState(L"cold-diagnostic-trace-unavailable",ERROR_WRITE_FAULT);
            Report(SERVICE_STOPPED,ERROR_WRITE_FAULT);return;
        }
    }
    trace.Begin(ClassicDeskService::StartupStage::Verify,GetTickCount64());
    const bool verified=Verify(true);
    const auto verifyEnded=GetTickCount64();
    trace.Complete(ClassicDeskService::StartupStage::Verify,
        verified?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
        verifyEnded,verified?0:ERROR_INVALID_DATA);
    if(!verified){
        RecordFailure(trace,ERROR_INVALID_DATA);
        WriteState(sessionOnly?L"session-only-verification-failed":coldDiagnostic?L"cold-diagnostic-verification-failed":L"verification-failed",ERROR_INVALID_DATA);
        Report(SERVICE_STOPPED,ERROR_INVALID_DATA);return;
    }
    if(sessionOnly)trace.Record(ClassicDeskService::StartupStage::Compatibility,
        ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());
    if(sessionOnly) {
        const DWORD disableError=DisableNextBoot();
        if(disableError!=ERROR_SUCCESS) {
            RecordFailure(trace,disableError);
            WriteState(L"session-only-disable-failed",disableError);Report(SERVICE_STOPPED,disableError);return;
        }
    }
    std::wstring fingerprint;
    ScopedHandle shellProcess;
    if(sessionOnly) {
        if(!ValidateSessionExplorer(startParameters,shellProcess.value)) {
            RecordFailure(trace,ERROR_INVALID_DATA);
            WriteState(L"session-shell-identity-mismatch",ERROR_INVALID_DATA);Report(SERVICE_STOPPED,ERROR_INVALID_DATA);return;
        }
        fingerprint=ClassicDeskService::ShellFingerprint(root);
        if(!ClassicDeskService::MatchesCurrentShellFingerprint(startParameters.fingerprint,fingerprint)) {
            RecordFailure(trace,ERROR_REVISION_MISMATCH);
            WriteState(L"session-shell-fingerprint-changed",ERROR_REVISION_MISMATCH);Report(SERVICE_STOPPED,ERROR_REVISION_MISMATCH);return;
        }
    } else if(coldDiagnostic) {
        trace.Begin(ClassicDeskService::StartupStage::Compatibility,GetTickCount64());
        ColdDiagnosticAdapter adapter;
        const auto decision=ClassicDeskService::ClaimColdDiagnostic(adapter);
        const bool authorized=std::wstring(decision)==L"cold-diagnostic-authorized-unverified";
        trace.Complete(ClassicDeskService::StartupStage::Compatibility,
            authorized?ClassicDeskService::StartupOutcome::Skipped:ClassicDeskService::StartupOutcome::Failed,
            GetTickCount64(),authorized?0:adapter.error);
        WriteState(decision,authorized?0:adapter.error);
        if(!authorized) {
            RecordFailure(trace,adapter.error);Report(SERVICE_STOPPED,adapter.error);return;
        }
    } else {
        trace.Begin(ClassicDeskService::StartupStage::Compatibility,GetTickCount64());
        const auto compatibility=Compatibility(fingerprint);
        const auto compatibilityEnded=GetTickCount64();
        const bool compatible=std::wstring(compatibility)==L"compatible";
        trace.Complete(ClassicDeskService::StartupStage::Compatibility,
            compatible?ClassicDeskService::StartupOutcome::Succeeded:ClassicDeskService::StartupOutcome::Failed,
            compatibilityEnded,
            compatible?0:ERROR_REVISION_MISMATCH);
        if(!compatible) {
            // Remain out of the shell when Windows, its modules or the chosen layout
            // differ from a real enhanced-shell acceptance. A running vanilla shell
            // and an installer invocation do not grant this acceptance.
            RecordFailure(trace,ERROR_REVISION_MISMATCH);
            WriteState(compatibility,ERROR_REVISION_MISMATCH);
            DWORD disableError=DisableNextBoot();wchar_t number[32]{};
            std::swprintf(number,ARRAYSIZE(number),L"%lu",disableError);
            const auto stateFile=root+L"\\service-state.ini";
            if(ProtectedChain(stateFile))WritePrivateProfileStringW(L"Service",L"AutoDisableError",number,stateFile.c_str());
            Report(SERVICE_STOPPED,ERROR_REVISION_MISMATCH);return;
        }
    }
    if(!NoOtherEngine()){RecordFailure(trace,ERROR_ALREADY_EXISTS);WriteState(L"engine-conflict",ERROR_ALREADY_EXISTS);Report(SERVICE_STOPPED,ERROR_ALREADY_EXISTS);return;}
    stopEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!stopEvent || !EnableDebugPrivilege()){RecordFailure(trace,ERROR_ACCESS_DENIED);WriteState(L"failed",ERROR_ACCESS_DENIED);if(stopEvent)CloseHandle(stopEvent);stopEvent=nullptr;Report(SERVICE_STOPPED,ERROR_ACCESS_DENIED);return;}
    EVT_HANDLE crashSubscription=EvtSubscribe(nullptr,nullptr,L"Application",
        L"*[System[Provider[@Name='Application Error'] and EventID=1000] and EventData[(Data[@Name='AppName']='explorer.exe' or Data[@Name='AppName']='StartMenuExperienceHost.exe')]]",
        nullptr,nullptr,OnCrash,EvtSubscribeToFutureEvents);
    if(!crashSubscription){const DWORD error=GetLastError();RecordFailure(trace,error);WriteState(L"crash-monitor-unavailable",error);Report(SERVICE_STOPPED,ERROR_NOT_READY);CloseHandle(stopEvent);stopEvent=nullptr;return;}
    DWORD error=0,cleanupError=0;
    bool shellEnded=false;
    bool enteredRunning=false;
    ULONGLONG runningSince=0;
    {
        Engine api(trace,runtimeMetadata,cleanupError);
        if(crashTrip){DisableNextBoot();error=ERROR_PROCESS_ABORTED;trace.Record(ClassicDeskService::StartupStage::EngineLoad,ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());RecordFailure(trace,error);}
        else if(coldDiagnostic && !traceFile.Ready()){error=ERROR_WRITE_FAULT;WriteState(L"cold-diagnostic-trace-unavailable",error);RecordFailure(trace,error);}
        else if(coldDiagnostic && !NoShellProcesses()){error=ERROR_ALREADY_EXISTS;WriteState(L"cold-diagnostic-shell-already-present",error);RecordFailure(trace,error);}
        else if(sessionOnly && WaitForSingleObject(shellProcess.value,0)!=WAIT_TIMEOUT){error=ERROR_PROCESS_ABORTED;trace.Record(ClassicDeskService::StartupStage::EngineLoad,ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());RecordFailure(trace,error);}
        else if(!api.Load()){error=ERROR_MOD_NOT_FOUND;}
        else if(coldDiagnostic && !NoShellProcesses()){error=ERROR_ALREADY_EXISTS;WriteState(L"cold-diagnostic-shell-already-present",error);RecordFailure(trace,error);}
        else if(sessionOnly && WaitForSingleObject(shellProcess.value,0)!=WAIT_TIMEOUT){error=ERROR_PROCESS_ABORTED;trace.Record(ClassicDeskService::StartupStage::GlobalHookSessionStart,ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());RecordFailure(trace,error);}
        else {
            ClassicDeskService::Session session(api);
            if(sessionOnly && WaitForSingleObject(shellProcess.value,0)!=WAIT_TIMEOUT){error=ERROR_PROCESS_ABORTED;trace.Record(ClassicDeskService::StartupStage::GlobalHookSessionStart,ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());RecordFailure(trace,error);}
            else if(!session.Start()){error=ERROR_DLL_INIT_FAILED;RecordFailure(trace,error);}
            else {
                const DWORD shellWait=sessionOnly?WaitForSingleObject(shellProcess.value,0):WAIT_TIMEOUT;
                if(sessionOnly && shellWait==WAIT_OBJECT_0){shellEnded=true;trace.Record(ClassicDeskService::StartupStage::Running,ClassicDeskService::StartupOutcome::Skipped,GetTickCount64());}
                else if(sessionOnly && shellWait!=WAIT_TIMEOUT){error=ERROR_GEN_FAILURE;RecordFailure(trace,error);}
                else {
                    runningSince=GetTickCount64();enteredRunning=true;
                    trace.Record(ClassicDeskService::StartupStage::Running,ClassicDeskService::StartupOutcome::Succeeded,runningSince);
                    WriteState(sessionOnly?L"running-current-session-unverified":coldDiagnostic?L"running-cold-diagnostic-unverified":L"running-unverified");Report(SERVICE_RUNNING);
                    // A session-only launch follows the exact Explorer process that
                    // was reviewed; its exit ends the engine session immediately.
                    for(;;) {
                        HANDLE waits[]={stopEvent,shellProcess.value};
                        const DWORD wait=WaitForMultipleObjects(sessionOnly?2:1,waits,FALSE,1000);
                        if(wait==WAIT_OBJECT_0)break;
                        if(sessionOnly && wait==WAIT_OBJECT_0+1){shellEnded=true;break;}
                        if(wait!=WAIT_TIMEOUT || !session.Scan()){error=ERROR_GEN_FAILURE;RecordFailure(trace,error);break;}
                    }
                }
                if(crashTrip){DisableNextBoot();error=ERROR_PROCESS_ABORTED;RecordFailure(trace,error);}
                if(enteredRunning) {
                    const auto runningEnded=GetTickCount64();
                    trace.Record(ClassicDeskService::StartupStage::Running,ClassicDeskService::StartupOutcome::Stopped,
                        runningEnded,runningEnded-runningSince);
                }
                Report(SERVICE_STOP_PENDING);session.Stop();
            }
        }
    }
    EvtClose(crashSubscription);
    error=ClassicDeskService::PreservePrimaryError(error,cleanupError);
    if(error)RecordFailure(trace,error);
    WriteState(error?L"failed":shellEnded?L"session-ended":L"stopped",error);CloseHandle(stopEvent);stopEvent=nullptr;Report(SERVICE_STOPPED,error);
}
}
int wmain(int argc,wchar_t** argv) {
    root=Base();
    if(argc==2 && std::wstring(argv[1])==L"--inspect-cold-diagnostic") {
        const auto uptime=GetTickCount64();
        std::wprintf(L"{\"schema\":2,\"bootEvidence\":\"monotonic-uptime-reset-v2\",\"runningSessionArmSupported\":true,\"nextBootArmSupported\":true,\"canArm\":%ls,\"minimumPreparedUptimeMs\":%llu,\"bootElapsedMs\":%llu,\"permitPresent\":%ls,\"writable\":false,\"startupChanged\":false,\"engineStarted\":false,\"acceptanceWritten\":false}\n",
            uptime>=ClassicDeskService::MinimumPreparedUptimeMs?L"true":L"false",ClassicDeskService::MinimumPreparedUptimeMs,uptime,ColdPermitPresent()?L"true":L"false");
        return 0;
    }
    if(argc==3 && std::wstring(argv[1])==L"--arm-next-boot") {
        const DWORD error=ArmColdDiagnostic(argv[2],0,true);
        std::wprintf(L"{\"state\":\"%ls\",\"error\":%lu,\"permitSchema\":3,\"lifetime\":\"next-boot\",\"expiresUtc\":null,\"engineStarted\":false,\"acceptanceWritten\":false,\"rebootRequested\":false}\n",
            error?L"cold-diagnostic-arm-failed":L"cold-diagnostic-armed-unverified",error);
        return error?1:0;
    }
    if(argc==4 && std::wstring(argv[1])==L"--arm-cold-diagnostic") {
        std::uint64_t minutes=0;
        const DWORD error=ClassicDeskService::ParsePositiveDecimal(argv[3],360,minutes)?
            ArmColdDiagnostic(argv[2],minutes):ERROR_INVALID_PARAMETER;
        std::wprintf(L"{\"state\":\"%ls\",\"error\":%lu,\"engineStarted\":false,\"acceptanceWritten\":false,\"rebootRequested\":false}\n",
            error?L"cold-diagnostic-arm-failed":L"cold-diagnostic-armed-unverified",error);
        return error?1:0;
    }
    if(argc==2 && std::wstring(argv[1])==L"--inspect-compatibility") {
        std::wstring fingerprint;const auto state=Compatibility(fingerprint);
        std::wprintf(L"{\"state\":\"%ls\",\"fingerprint\":\"%ls\",\"engineStarted\":false,\"acceptanceWritten\":false}\n",state,fingerprint.c_str());
        return fingerprint.empty()?1:0;
    }
    if(argc==2 && (std::wstring(argv[1])==L"--inspect" || std::wstring(argv[1])==L"--inspect-protected")) {
        bool valid=Verify(std::wstring(argv[1])==L"--inspect-protected");
        std::wprintf(L"{\"assetsVerified\":%ls,\"engineStarted\":false,\"serviceCreated\":false}\n",valid?L"true":L"false");
        return valid?0:1;
    }
    if(argc!=2 || std::wstring(argv[1])!=L"--service")return ERROR_BAD_ARGUMENTS;
    SERVICE_TABLE_ENTRYW table[]={{const_cast<LPWSTR>(ServiceName),ServiceMain},{nullptr,nullptr}};
    return StartServiceCtrlDispatcherW(table)?0:(int)GetLastError();
}
