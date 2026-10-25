#pragma once

#include "service-compatibility.h"
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <utility>

namespace ClassicDeskService {
enum class StartMode { CompatibilityGuard, SessionOnly };

struct ServiceStartParameters {
    StartMode mode=StartMode::CompatibilityGuard;
    std::uint32_t explorerPid=0;
    std::uint32_t sessionId=0;
    std::uint64_t explorerCreationFileTimeUtc=0;
    std::wstring fingerprint;
};

inline bool ParsePositiveDecimal(std::wstring_view text,std::uint64_t maximum,std::uint64_t& value) {
    if(text.empty())return false;
    std::uint64_t parsed=0;
    for(wchar_t c:text) {
        if(c<L'0' || c>L'9')return false;
        const auto digit=static_cast<std::uint64_t>(c-L'0');
        if(parsed>(maximum-digit)/10)return false;
        parsed=parsed*10+digit;
    }
    if(parsed==0)return false;
    value=parsed;
    return true;
}

// SCM ServiceMain receives argv[0] as the service name. The only supported
// optional form is exactly: session-only PID SESSION CREATION_FILETIME FINGERPRINT.
inline bool ParseServiceStartParameters(std::size_t argc,wchar_t* const* argv,
        std::wstring_view serviceName,ServiceStartParameters& result) {
    result=ServiceStartParameters{};
    if(!argv || argc==0 || !argv[0] || serviceName!=argv[0])return false;
    if(argc==1)return true;
    if(argc!=6 || !argv[1] || std::wstring_view(argv[1])!=L"session-only" ||
       !argv[2] || !argv[3] || !argv[4] || !argv[5])return false;

    std::uint64_t pid=0,session=0,creation=0;
    if(!ParsePositiveDecimal(argv[2],UINT32_MAX,pid) ||
       !ParsePositiveDecimal(argv[3],UINT32_MAX,session) ||
       !ParsePositiveDecimal(argv[4],UINT64_MAX,creation))return false;
    std::wstring fingerprint(argv[5]);
    if(!IsFingerprint(fingerprint))return false;

    result.mode=StartMode::SessionOnly;
    result.explorerPid=static_cast<std::uint32_t>(pid);
    result.sessionId=static_cast<std::uint32_t>(session);
    result.explorerCreationFileTimeUtc=creation;
    result.fingerprint=std::move(fingerprint);
    return true;
}

inline bool MatchesCurrentShellFingerprint(const std::wstring& supplied,const std::wstring& current) {
    return IsFingerprint(supplied) && IsFingerprint(current) && supplied==current;
}
}
