#pragma once
#include "service-compatibility.h"
#include <array>
#include <cstdint>
#include <string>

namespace ClassicDeskService {
using TrialIdentity=std::array<std::uint8_t,16>;
constexpr std::uint64_t FileTimeSecond=10000000;
constexpr std::uint64_t MaximumColdPermitAge=6*60*60*FileTimeSecond;
constexpr std::uint64_t MaximumColdBootElapsedMs=10*60*1000;
// A strictly later preparation checkpoint than the entire allowed startup
// window makes a reset provable without undocumented boot GUID semantics.
constexpr std::uint64_t MinimumPreparedUptimeMs=MaximumColdBootElapsedMs+1000;

struct ColdArmIdentity {
    bool running=false;
    std::uint32_t processId=0;
    std::uint64_t creation=0;
    bool operator==(const ColdArmIdentity&) const=default;
};
inline bool ValidColdArmIdentity(const ColdArmIdentity& identity,bool sessionOnlyVerified) {
    return identity.running ? sessionOnlyVerified && identity.processId!=0 && identity.creation!=0
        : identity.processId==0 && identity.creation==0;
}

struct ColdDiagnosticPermit {
    std::uint32_t schema=2;
    std::uint64_t preparedUptimeMs=0;
    TrialIdentity trialId{};
    std::uint64_t createdUtc=0,expiresUtc=0;
    // ShellFingerprint binds OS binaries, host, pinned component manifest and
    // all effective settings. Binding also hashes root + protected install receipt.
    std::wstring fingerprint,binding;
};
inline bool ValidIdentity(const TrialIdentity& id) {
    for(auto byte:id)if(byte)return true;
    return false;
}
inline const wchar_t* ColdDiagnosticDecision(const ColdDiagnosticPermit& p,
        std::uint64_t now,std::uint64_t uptime,
        const std::wstring& fingerprint,const std::wstring& binding,bool shellAbsent) {
    // v2 is the legacy timed trial. v3 is explicitly authorized for the next
    // eligible boot, without a wall-clock deadline; both are consumed once.
    const bool validLifetime=p.schema==3 ? p.expiresUtc==0 : p.schema==2 &&
        p.expiresUtc>p.createdUtc && p.expiresUtc-p.createdUtc<=MaximumColdPermitAge;
    if(!validLifetime || p.preparedUptimeMs<MinimumPreparedUptimeMs || !ValidIdentity(p.trialId) ||
       !IsFingerprint(p.fingerprint) || !IsFingerprint(p.binding) || !p.createdUtc ||
       (p.schema!=2 && p.schema!=3))
        return L"cold-diagnostic-invalid-permit";
    // GetTickCount64 never decreases during a kernel session (including sleep
    // and wall-clock changes). A service restart cannot satisfy this check.
    // The full 64-bit counter is used; no 49-day truncation/wrap is involved.
    if(uptime>=p.preparedUptimeMs)return L"cold-diagnostic-same-boot";
    if(now<p.createdUtc || (p.schema==2 && now>=p.expiresUtc))return L"cold-diagnostic-expired";
    if(uptime>MaximumColdBootElapsedMs)return L"cold-diagnostic-late-start";
    if(!IsFingerprint(fingerprint) || fingerprint!=p.fingerprint ||
       !IsFingerprint(binding) || binding!=p.binding)return L"cold-diagnostic-binding-changed";
    if(!shellAbsent)return L"cold-diagnostic-shell-already-present";
    return L"cold-diagnostic-authorized-unverified";
}

// The durable disable and atomic claim are prerequisites, even for malformed,
// expired or same-boot permits. No rejected attempt can fall back to acceptance.
// Adapter methods return a status string; nullptr means the operation succeeded.
template<class Adapter> const wchar_t* ClaimColdDiagnostic(Adapter& adapter) {
    if(auto failure=adapter.Disable())return failure;
    if(auto failure=adapter.Consume())return failure;
    return adapter.Validate();
}
}
