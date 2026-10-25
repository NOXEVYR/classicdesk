#include "service-cold-diagnostic.h"
#include "service-startup-trace.h"
#include <cstring>
#include <cassert>
#include <iostream>
#include <vector>
using namespace ClassicDeskService;
int main() {
    ColdDiagnosticPermit permit;permit.preparedUptimeMs=10800000;permit.trialId[0]=2;
    permit.createdUtc=100*FileTimeSecond;permit.expiresUtc=200*FileTimeSecond;
    permit.fingerprint=std::wstring(64,L'a');permit.binding=std::wstring(64,L'b');
    auto decide=[&](const ColdDiagnosticPermit& p,std::uint64_t now,
        std::uint64_t uptime,const std::wstring& fp,const std::wstring& binding,bool absent) {
        return std::wstring(ColdDiagnosticDecision(p,now,uptime,fp,binding,absent));
    };
    auto normal=[&](const ColdDiagnosticPermit& p){return decide(p,150*FileTimeSecond,50,p.fingerprint,p.binding,true);};
    assert(normal(permit)==L"cold-diagnostic-authorized-unverified");
    // Reproduce the observed real reboot: the firmware/environment GUID did
    // not change, but uptime went from hours to 17 seconds. No GUID is needed.
    assert(decide(permit,150*FileTimeSecond,17281,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-authorized-unverified");
    for(auto uptime:{permit.preparedUptimeMs,permit.preparedUptimeMs+1,permit.preparedUptimeMs+3600000})
        assert(decide(permit,150*FileTimeSecond,uptime,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-same-boot");
    for(auto time:{99ULL*FileTimeSecond,200ULL*FileTimeSecond,201ULL*FileTimeSecond})
        assert(decide(permit,time,50,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-expired");
    assert(decide(permit,150*FileTimeSecond,MaximumColdBootElapsedMs+1,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-late-start");
    assert(decide(permit,150*FileTimeSecond,MaximumColdBootElapsedMs,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-authorized-unverified");
    assert(decide(permit,150*FileTimeSecond,50,std::wstring(64,L'c'),permit.binding,true)==L"cold-diagnostic-binding-changed");
    assert(decide(permit,150*FileTimeSecond,50,permit.fingerprint,std::wstring(64,L'c'),true)==L"cold-diagnostic-binding-changed");
    assert(decide(permit,150*FileTimeSecond,50,permit.fingerprint,permit.binding,false)==L"cold-diagnostic-shell-already-present");
    auto invalid=permit;invalid.schema=1;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=permit;invalid.preparedUptimeMs=MinimumPreparedUptimeMs-1;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    auto boundary=permit;boundary.preparedUptimeMs=MinimumPreparedUptimeMs;assert(normal(boundary)==L"cold-diagnostic-authorized-unverified");
    auto longRunning=permit;longRunning.preparedUptimeMs=(1ULL<<32)+1000;
    assert(normal(longRunning)==L"cold-diagnostic-authorized-unverified");
    assert(decide(longRunning,150*FileTimeSecond,(1ULL<<32)+2000,permit.fingerprint,permit.binding,true)==L"cold-diagnostic-same-boot");
    invalid=permit;invalid.expiresUtc=invalid.createdUtc+MaximumColdPermitAge+1;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=permit;invalid.expiresUtc=invalid.createdUtc;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=permit;invalid.trialId={};assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=permit;invalid.fingerprint[0]=L'G';assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    auto nextBoot=permit;nextBoot.schema=3;nextBoot.expiresUtc=0;
    for(auto days:{1ULL,7ULL,90ULL}) {
        auto later=nextBoot.createdUtc+days*86400*FileTimeSecond;
        assert(decide(nextBoot,later,17000,nextBoot.fingerprint,nextBoot.binding,true)==L"cold-diagnostic-authorized-unverified");
        assert(decide(nextBoot,later,nextBoot.preparedUptimeMs,nextBoot.fingerprint,nextBoot.binding,true)==L"cold-diagnostic-same-boot");
        assert(decide(nextBoot,later,17000,std::wstring(64,L'c'),nextBoot.binding,true)==L"cold-diagnostic-binding-changed");
        assert(decide(nextBoot,later,17000,nextBoot.fingerprint,std::wstring(64,L'c'),true)==L"cold-diagnostic-binding-changed");
        assert(decide(nextBoot,later,17000,nextBoot.fingerprint,nextBoot.binding,false)==L"cold-diagnostic-shell-already-present");
    }
    assert(decide(nextBoot,99*FileTimeSecond,50,nextBoot.fingerprint,nextBoot.binding,true)==L"cold-diagnostic-expired");
    assert(decide(nextBoot,150*FileTimeSecond,600001,nextBoot.fingerprint,nextBoot.binding,true)==L"cold-diagnostic-late-start");
    invalid=nextBoot;invalid.expiresUtc=200*FileTimeSecond;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=nextBoot;invalid.schema=4;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    invalid=permit;invalid.expiresUtc=0;assert(normal(invalid)==L"cold-diagnostic-invalid-permit");
    struct Adapter {
        std::vector<int> calls;bool disableFails=false,consumeFails=false,consumed=false;
        const wchar_t* Disable(){calls.push_back(1);return disableFails?L"disable-failed":nullptr;}
        const wchar_t* Consume(){calls.push_back(2);if(consumeFails || consumed)return L"consume-failed";consumed=true;return nullptr;}
        const wchar_t* Validate(){calls.push_back(3);return L"cold-diagnostic-authorized-unverified";}
    };
    Adapter adapter;assert(std::wstring(ClaimColdDiagnostic(adapter))==L"cold-diagnostic-authorized-unverified");
    assert((adapter.calls==std::vector<int>{1,2,3}));
    adapter.calls.clear();assert(std::wstring(ClaimColdDiagnostic(adapter))==L"consume-failed");assert((adapter.calls==std::vector<int>{1,2}));
    adapter={};adapter.disableFails=true;assert(std::wstring(ClaimColdDiagnostic(adapter))==L"disable-failed");assert((adapter.calls==std::vector<int>{1}));
    adapter={};adapter.consumeFails=true;assert(std::wstring(ClaimColdDiagnostic(adapter))==L"consume-failed");assert((adapter.calls==std::vector<int>{1,2}));
    StartupTrace trace(10);trace.SetMode(StartupMode::ColdDiagnostic);char header[512]{};std::size_t bytes=0;
    assert(trace.SerializeHeader(1,header,sizeof(header),bytes) && std::strstr(header,"mode=cold-diagnostic"));
    ServiceRuntimeMetadata metadata;metadata.SetMode(StartupMode::ColdDiagnostic,123,456);
    assert(metadata.Mode()==StartupMode::ColdDiagnostic && !metadata.EngineStarted() && metadata.SessionId()==0 && metadata.ExplorerPid()==0);
    assert(ValidColdArmIdentity({},false));
    assert(ValidColdArmIdentity({true,123,456},true));
    assert(!ValidColdArmIdentity({true,123,456},false));
    assert(!ValidColdArmIdentity({true,0,456},true));
    assert(!ValidColdArmIdentity({true,123,0},true));
    assert(!ValidColdArmIdentity({false,123,456},true));
    const ColdArmIdentity armed{true,123,456};
    assert((armed!=ColdArmIdentity{true,124,456}));
    assert((armed!=ColdArmIdentity{true,123,457}));
    assert((armed!=ColdArmIdentity{}));
    std::cout<<"cold-diagnostic policy, live-session arm identity and ordered single-use gate: PASS\n";
}
