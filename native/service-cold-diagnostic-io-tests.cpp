// Compile the actual host adapter, but never enter its SCM/arming path.
#define wmain ClassicDeskHostMainForTest
#include "shell-service.cpp"
#undef wmain
#include <cassert>
#include <thread>
#include <iostream>

int wmain(int argc,wchar_t** argv) {
    assert(argc==2);root=argv[1];
    assert(CreateDirectoryW(root.c_str(),nullptr));
    const auto active=ColdPermitPath(),consumed=ColdPermitPath(true);
    auto create=[](const std::wstring& path,const char* payload) {
        HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        assert(file!=INVALID_HANDLE_VALUE);DWORD written=0;
        assert(WriteFile(file,payload,4,&written,nullptr) && written==4);CloseHandle(file);
    };
    create(active,"AAAA");
    DWORD result1=0,result2=0;
    std::thread first([&]{result1=ConsumeColdPermitFile(active,consumed);});
    std::thread second([&]{result2=ConsumeColdPermitFile(active,consumed);});
    first.join();second.join();
    std::cerr<<"consume race Win32 results: "<<result1<<", "<<result2<<"\n";
    assert((result1==ERROR_SUCCESS)!=(result2==ERROR_SUCCESS));
    assert(GetFileAttributesW(active.c_str())==INVALID_FILE_ATTRIBUTES);
    create(active,"BBBB");
    assert(ConsumeColdPermitFile(active,consumed)!=ERROR_SUCCESS);
    char payload[4]{};DWORD read=0;
    HANDLE used=CreateFileW(consumed.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    assert(used!=INVALID_HANDLE_VALUE && ReadFile(used,payload,4,&read,nullptr) && read==4 && std::memcmp(payload,"AAAA",4)==0);CloseHandle(used);
    ColdPermitBlob blob{};assert(!ReadColdPermit(blob)); // unprotected fixture is never accepted
    assert(ArmColdDiagnostic(std::wstring(64,L'a'),1)==ERROR_INVALID_DATA); // exits before opening SCM
    const auto before=GetTickCount64();Sleep(20);const auto after=GetTickCount64();
    assert(after>=before);
    static_assert(sizeof(ColdPermitBlob)==184 && offsetof(ColdPermitBlob,preparedUptimeMs)==24 &&
        offsetof(ColdPermitBlob,reserved)==32 && offsetof(ColdPermitBlob,trial)==40 && offsetof(ColdPermitBlob,fingerprint)==56);
    assert(blob.schema==2);
    std::cout<<"actual no-replace consumption race, preserved receipt, unprotected refusal: PASS\n";
    std::cout<<"read-only 64-bit uptime sample: "<<before<<" -> "<<after<<" ms; no reboot acceptance inferred\n";
    // Fixture files remain in the explicit report directory as test evidence.
}
