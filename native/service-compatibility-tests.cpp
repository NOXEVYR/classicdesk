#include "service-compatibility.h"
#include <cassert>
#include <cwchar>
int main() {
    using ClassicDeskService::CompatibilityDecision;
    const std::wstring a(64,L'a'),b(64,L'b');
    auto check=[](const wchar_t* actual,const wchar_t* expected){assert(std::wcscmp(actual,expected)==0);};
    check(CompatibilityDecision(false,1,true,a,a),L"compatibility-unverified");
    check(CompatibilityDecision(true,2,true,a,a),L"compatibility-unverified");
    check(CompatibilityDecision(true,1,false,a,a),L"compatibility-unverified");
    check(CompatibilityDecision(true,1,true,L"",a),L"compatibility-unverified");
    check(CompatibilityDecision(true,1,true,a,L""),L"compatibility-unavailable");
    check(CompatibilityDecision(true,1,true,a,b),L"compatibility-changed");
    check(CompatibilityDecision(true,1,true,a,a),L"compatible");
    assert(!ClassicDeskService::IsFingerprint(std::wstring(64,L'g')));
    assert(!ClassicDeskService::IsFingerprint(std::wstring(63,L'a')));
    assert(!ClassicDeskService::IsFingerprint(std::wstring(64,L'A')));
}
