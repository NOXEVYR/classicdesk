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
#include <string>
#include <vector>
#include "service-session.h"
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
    FindClose(files);if(!validMods || count!=6)return false;
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
void WriteState(const wchar_t* state,DWORD error=0) {
    auto file=root+L"\\service-state.ini";
    wchar_t number[64]{};std::swprintf(number,ARRAYSIZE(number),L"%lu",GetCurrentProcessId());
    WritePrivateProfileStringW(L"Service",L"ProcessId",number,file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%llu",GetTickCount64());
    WritePrivateProfileStringW(L"Service",L"BootElapsedMs",number,file.c_str());
    std::swprintf(number,ARRAYSIZE(number),L"%lu",error);
    WritePrivateProfileStringW(L"Service",L"Error",number,file.c_str());
    WritePrivateProfileStringW(L"Service",L"State",state,file.c_str());
}
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
void DisableNextBoot() {
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);if(!manager)return;
    SC_HANDLE service=OpenServiceW(manager,ServiceName,SERVICE_CHANGE_CONFIG);
    if(service){ChangeServiceConfigW(service,SERVICE_NO_CHANGE,SERVICE_DISABLED,SERVICE_NO_CHANGE,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr);CloseServiceHandle(service);}
    CloseServiceHandle(manager);
}
struct Engine {
    using StartFn=HANDLE(*)();using ScanFn=BOOL(*)(HANDLE);using EndFn=BOOL(*)(HANDLE);
    HMODULE module=nullptr;StartFn start=nullptr;ScanFn scan=nullptr;EndFn end=nullptr;
    bool Load(){
        auto path=root+L"\\Runtime\\Engine\\1.7.3\\32\\windhawk.dll";
        module=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!module)return false;
        start=(StartFn)GetProcAddress(module,"GlobalHookSessionStart");
        scan=(ScanFn)GetProcAddress(module,"GlobalHookSessionHandleNewProcesses");
        end=(EndFn)GetProcAddress(module,"GlobalHookSessionEnd");return start&&scan&&end;
    }
    void* Start(){return start();}bool Scan(void* value){return scan(value)!=FALSE;}void End(void* value){end(value);}
    ~Engine(){if(module)FreeLibrary(module);}
};
DWORD WINAPI Control(DWORD control,DWORD,LPVOID,LPVOID) {
    if(control==SERVICE_CONTROL_STOP || control==SERVICE_CONTROL_SHUTDOWN){Report(SERVICE_STOP_PENDING);if(stopEvent)SetEvent(stopEvent);return NO_ERROR;}
    if(control==SERVICE_CONTROL_INTERROGATE)return NO_ERROR;
    return ERROR_CALL_NOT_IMPLEMENTED;
}
void WINAPI ServiceMain(DWORD,LPWSTR*) {
    statusHandle=RegisterServiceCtrlHandlerExW(ServiceName,Control,nullptr);if(!statusHandle)return;
    Report(SERVICE_START_PENDING);
    if(!Verify(true)){Report(SERVICE_STOPPED,ERROR_INVALID_DATA);return;}
    if(!NoOtherEngine()){WriteState(L"engine-conflict",ERROR_ALREADY_EXISTS);Report(SERVICE_STOPPED,ERROR_ALREADY_EXISTS);return;}
    stopEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!stopEvent || !EnableDebugPrivilege()){WriteState(L"failed",ERROR_ACCESS_DENIED);if(stopEvent)CloseHandle(stopEvent);stopEvent=nullptr;Report(SERVICE_STOPPED,ERROR_ACCESS_DENIED);return;}
    EVT_HANDLE crashSubscription=EvtSubscribe(nullptr,nullptr,L"Application",
        L"*[System[Provider[@Name='Application Error'] and EventID=1000] and EventData[(Data[@Name='AppName']='explorer.exe' or Data[@Name='AppName']='StartMenuExperienceHost.exe')]]",
        nullptr,nullptr,OnCrash,EvtSubscribeToFutureEvents);
    if(!crashSubscription){WriteState(L"crash-monitor-unavailable",GetLastError());Report(SERVICE_STOPPED,ERROR_NOT_READY);CloseHandle(stopEvent);stopEvent=nullptr;return;}
    DWORD error=0;
    {
        Engine api;
        if(crashTrip){DisableNextBoot();error=ERROR_PROCESS_ABORTED;}
        else if(!api.Load()){error=ERROR_MOD_NOT_FOUND;}
        else {
            ClassicDeskService::Session session(api);
            if(!session.Start())error=ERROR_DLL_INIT_FAILED;
            else {
                WriteState(L"running-unverified");Report(SERVICE_RUNNING);
                // Same incremental fallback as upstream. Early child-process hooks
                // do the startup work; no per-user UI process or screen polling.
                for(;;) {
                    DWORD wait=WaitForSingleObject(stopEvent,1000);
                    if(wait==WAIT_OBJECT_0)break;
                    if(wait!=WAIT_TIMEOUT || !session.Scan()){error=ERROR_GEN_FAILURE;break;}
                }
                if(crashTrip){DisableNextBoot();error=ERROR_PROCESS_ABORTED;}
                Report(SERVICE_STOP_PENDING);session.Stop();
            }
        }
    }
    EvtClose(crashSubscription);
    WriteState(error?L"failed":L"stopped",error);CloseHandle(stopEvent);stopEvent=nullptr;Report(SERVICE_STOPPED,error);
}
}
int wmain(int argc,wchar_t** argv) {
    root=Base();
    if(argc==2 && (std::wstring(argv[1])==L"--inspect" || std::wstring(argv[1])==L"--inspect-protected")) {
        bool valid=Verify(std::wstring(argv[1])==L"--inspect-protected");
        std::wprintf(L"{\"assetsVerified\":%ls,\"engineStarted\":false,\"serviceCreated\":false}\n",valid?L"true":L"false");
        return valid?0:1;
    }
    if(argc!=2 || std::wstring(argv[1])!=L"--service")return ERROR_BAD_ARGUMENTS;
    SERVICE_TABLE_ENTRYW table[]={{const_cast<LPWSTR>(ServiceName),ServiceMain},{nullptr,nullptr}};
    return StartServiceCtrlDispatcherW(table)?0:(int)GetLastError();
}
