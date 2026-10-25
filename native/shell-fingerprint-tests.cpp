#include "shell-fingerprint.h"
#include "service-compatibility.h"
#include <cassert>
#include <fstream>
#include <filesystem>
#include <iostream>

int wmain(int argc,wchar_t** argv) {
    assert(argc==2);
    const std::filesystem::path root(argv[1]);
    assert(!std::filesystem::exists(root));
    std::filesystem::create_directories(root/L"Runtime/AppData/Engine/Mods");
    std::filesystem::create_directories(root/L"Runtime/Engine/1.7.3");
    auto write=[&](const std::wstring& file,const std::string& value){
        std::ofstream out(root/file,std::ios::binary);out<<value;out.close();assert(out.good());
    };
    write(L"ClassicDeskShell.exe","synthetic-host");
    write(L"Runtime/runtime-assets-manifest.json","synthetic-manifest");
    write(L"Runtime/AppData/Engine/settings.ini","synthetic-policy");
    write(L"Runtime/Engine/1.7.3/engine.ini","synthetic-engine-settings");
    for(int i=0;i<6;i++)write(L"Runtime/AppData/Engine/Mods/"+std::to_wstring(i)+L".ini","synthetic-module-settings");
    auto original=ClassicDeskService::ShellFingerprint(root.wstring());
    assert(ClassicDeskService::IsFingerprint(original));
    assert(original==ClassicDeskService::ShellFingerprint(root.wstring()));
    write(L"Runtime/AppData/Engine/Mods/0.ini","changed-layout");
    assert(original!=ClassicDeskService::ShellFingerprint(root.wstring()));
    write(L"Runtime/AppData/Engine/Mods/0.ini","synthetic-module-settings");
    assert(original==ClassicDeskService::ShellFingerprint(root.wstring()));
    write(L"ClassicDeskShell.exe","changed-host");
    assert(original!=ClassicDeskService::ShellFingerprint(root.wstring()));
    write(L"ClassicDeskShell.exe","synthetic-host");
    write(L"Runtime/runtime-assets-manifest.json","changed-modules");
    assert(original!=ClassicDeskService::ShellFingerprint(root.wstring()));
    write(L"Runtime/runtime-assets-manifest.json","synthetic-manifest");
    write(L"Runtime/AppData/Engine/Mods/extra.ini","extra-policy");
    assert(ClassicDeskService::ShellFingerprint(root.wstring()).empty());
    // Only files created by this test in the supplied new fixture are removed.
    std::filesystem::remove(root/L"Runtime/AppData/Engine/Mods/extra.ini");
    const auto locked=root/L"Runtime/AppData/Engine/Mods/0.ini";
    HANDLE lock=CreateFileW(locked.c_str(),GENERIC_READ,0,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    assert(lock!=INVALID_HANDLE_VALUE);
    assert(ClassicDeskService::ShellFingerprint(root.wstring()).empty());CloseHandle(lock);
    assert(original==ClassicDeskService::ShellFingerprint(root.wstring()));
    std::filesystem::remove(locked);
    assert(ClassicDeskService::ShellFingerprint(root.wstring()).empty());
    ClassicDeskService::FingerprintBuilder missing;
    assert(!missing.File(L"unreadable",(root/L"missing.dll").wstring()));assert(missing.Finish().empty());
    std::puts("Fingerprint integration checks passed (OS files read only, synthetic local runtime, no engine loaded).");
}
