#include "service-start-arguments.h"
#include <cassert>
#include <string>
#include <vector>

using namespace ClassicDeskService;

static bool Parse(std::vector<std::wstring> values,ServiceStartParameters& result) {
    std::vector<wchar_t*> argv;
    argv.reserve(values.size());
    for(auto& value:values)argv.push_back(value.data());
    return ParseServiceStartParameters(argv.size(),argv.data(),L"ClassicDeskShell",result);
}

int main() {
    ServiceStartParameters parsed;
    assert(Parse({L"ClassicDeskShell"},parsed));
    assert(parsed.mode==StartMode::CompatibilityGuard);
    assert(parsed.explorerPid==0 && parsed.sessionId==0 && parsed.fingerprint.empty());

    const std::wstring fingerprint(64,L'a');
    assert(Parse({L"ClassicDeskShell",L"session-only",L"1234",L"7",L"134078000000000000",fingerprint},parsed));
    assert(parsed.mode==StartMode::SessionOnly);
    assert(parsed.explorerPid==1234 && parsed.sessionId==7 && parsed.explorerCreationFileTimeUtc==134078000000000000ULL);
    assert(parsed.fingerprint==fingerprint);
    assert(MatchesCurrentShellFingerprint(fingerprint,fingerprint));
    assert(!MatchesCurrentShellFingerprint(fingerprint,std::wstring(64,L'b')));
    assert(!MatchesCurrentShellFingerprint(std::wstring(64,L'A'),fingerprint));

    const std::vector<std::vector<std::wstring>> rejected={
        {},
        {L"WrongService"},
        {L"ClassicDeskShell",L"unexpected"},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"1"},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"1",fingerprint,L"extra"},
        {L"ClassicDeskShell",L"Session-only",L"1",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"+1",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"-1",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1x",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"0",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"4294967296",L"1",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1",L"0",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1",L"4294967296",L"1",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"0",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"18446744073709551616",fingerprint},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"1",std::wstring(63,L'a')},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"1",std::wstring(64,L'A')},
        {L"ClassicDeskShell",L"session-only",L"1",L"1",L"1",std::wstring(63,L'a')+L"g"}
    };
    for(const auto& arguments:rejected)assert(!Parse(arguments,parsed));
}
