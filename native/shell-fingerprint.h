#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <string>
#include <vector>

namespace ClassicDeskService {
class FingerprintBuilder {
    BCRYPT_ALG_HANDLE algorithm=nullptr;
    BCRYPT_HASH_HANDLE hash=nullptr;
    std::vector<UCHAR> object;
    bool valid=false;
public:
    FingerprintBuilder() {
        DWORD bytes=0,returned=0;
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return;
        if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,(PUCHAR)&bytes,sizeof(bytes),&returned,0)<0)return;
        object.resize(bytes);
        valid=BCryptCreateHash(algorithm,&hash,object.data(),bytes,nullptr,0,0)>=0;
    }
    ~FingerprintBuilder(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
    bool Bytes(const void* data,ULONG bytes) {
        valid=valid && BCryptHashData(hash,(PUCHAR)data,bytes,0)>=0;return valid;
    }
    bool Text(const std::wstring& text) {
        // Each field is length delimited, including file identities.
        const ULONGLONG bytes=text.size()*sizeof(wchar_t);
        return Bytes(&bytes,sizeof(bytes)) && Bytes(text.data(),(ULONG)bytes);
    }
    bool File(const std::wstring& label,const std::wstring& path) {
        if(!Text(label))return false;
        HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file==INVALID_HANDLE_VALUE){valid=false;return false;}
        LARGE_INTEGER size{};valid=GetFileSizeEx(file,&size) && size.QuadPart>=0 && size.QuadPart<=256LL*1024*1024;
        if(valid)Bytes(&size.QuadPart,sizeof(size.QuadPart));
        BYTE buffer[65536];DWORD count=0;
        while(valid){if(!ReadFile(file,buffer,sizeof(buffer),&count,nullptr)){valid=false;break;}if(!count)break;Bytes(buffer,count);}
        CloseHandle(file);return valid;
    }
    std::wstring Finish() {
        BYTE digest[32]{};
        if(!valid || BCryptFinishHash(hash,digest,sizeof(digest),0)<0)return {};
        constexpr wchar_t hex[]=L"0123456789abcdef";std::wstring result;
        for(auto b:digest){result+=hex[b>>4];result+=hex[b&15];}return result;
    }
};

inline std::wstring ShellFingerprint(const std::wstring& installRoot) {
    FingerprintBuilder result;
    if(!result.Text(L"ClassicDesk-shell-compatibility-v1"))return {};
    wchar_t windows[MAX_PATH]{},build[64]{};DWORD bytes=sizeof(build),ubr=0,ubrBytes=sizeof(ubr);
    if(!GetWindowsDirectoryW(windows,ARRAYSIZE(windows)))return {};
    constexpr auto key=L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    HKEY versionKey=nullptr;
    if(RegOpenKeyExW(HKEY_LOCAL_MACHINE,key,0,KEY_READ|KEY_WOW64_64KEY,&versionKey)!=ERROR_SUCCESS)return {};
    bool versionRead=RegGetValueW(versionKey,nullptr,L"CurrentBuildNumber",RRF_RT_REG_SZ,nullptr,build,&bytes)==ERROR_SUCCESS &&
       RegGetValueW(versionKey,nullptr,L"UBR",RRF_RT_REG_DWORD,nullptr,&ubr,&ubrBytes)==ERROR_SUCCESS;
    RegCloseKey(versionKey);if(!versionRead)return {};
    if(!result.Text(build) || !result.Bytes(&ubr,sizeof(ubr)))return {};
    // The SCM host is x86; Sysnative deliberately hashes the x64 shell binaries.
    BOOL wow64=FALSE;if(!IsWow64Process(GetCurrentProcess(),&wow64))return {};
    const auto system=std::wstring(windows)+(wow64?L"\\Sysnative\\":L"\\System32\\");
    const auto apps=std::wstring(windows)+L"\\SystemApps\\";
    const std::pair<std::wstring,std::wstring> files[]={
        {L"Explorer",std::wstring(windows)+L"\\explorer.exe"},
        {L"Taskbar",system+L"Taskbar.dll"},
        {L"Taskbar.View",apps+L"MicrosoftWindows.Client.Core_cw5n1h2txyewy\\Taskbar.View.dll"},
        {L"StartMenuExperienceHost",apps+L"Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\\StartMenuExperienceHost.exe"},
        {L"Windows.UI.Xaml",system+L"Windows.UI.Xaml.dll"},
        {L"Microsoft.UI.Xaml.CBS",apps+L"Microsoft.UI.Xaml.CBS_8wekyb3d8bbwe\\Microsoft.UI.Xaml.dll"}
    };
    for(auto& file:files)if(!result.File(file.first,file.second))return {};
    const auto winui3=apps+L"Microsoft.WindowsAppRuntime.CBS.2_8wekyb3d8bbwe\\Microsoft.UI.Xaml.dll";
    DWORD winui3Attributes=GetFileAttributesW(winui3.c_str());
    if(winui3Attributes!=INVALID_FILE_ATTRIBUTES) {
        if(!result.File(L"Microsoft.WindowsAppRuntime.CBS.2",winui3))return {};
    } else {
        DWORD error=GetLastError();
        if((error!=ERROR_FILE_NOT_FOUND && error!=ERROR_PATH_NOT_FOUND) || !result.Text(L"Microsoft.WindowsAppRuntime.CBS.2-absent"))return {};
    }
    if(!result.File(L"ClassicDeskShell",installRoot+L"\\ClassicDeskShell.exe"))return {};
    // The manifest binds the exact module/engine code, settings bind the layout.
    for(auto file:{L"runtime-assets-manifest.json",L"AppData\\Engine\\settings.ini",L"Engine\\1.7.3\\engine.ini"})
        if(!result.File(file,installRoot+L"\\Runtime\\"+file))return {};
    const auto directory=installRoot+L"\\Runtime\\AppData\\Engine\\Mods\\";
    WIN32_FIND_DATAW entry{};HANDLE find=FindFirstFileW((directory+L"*.ini").c_str(),&entry);
    if(find==INVALID_HANDLE_VALUE)return {};
    std::vector<std::wstring> names;
    do{if(!(entry.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY))names.emplace_back(entry.cFileName);}while(FindNextFileW(find,&entry));
    DWORD enumerationError=GetLastError();
    FindClose(find);if(enumerationError!=ERROR_NO_MORE_FILES || names.size()!=6)return {};
    std::sort(names.begin(),names.end());
    for(auto& name:names)if(!result.File(name,directory+name))return {};
    return result.Finish();
}
}
