#pragma once
#include <string>

namespace ClassicDeskService {
// A native desktop snapshot is never evidence that injected modules were tested.
// Only an explicitly accepted, protected receipt may authorize unattended loading.
inline bool IsFingerprint(const std::wstring& value) {
    if (value.size()!=64) return false;
    for (auto c:value) if(!((c>=L'0' && c<=L'9') || (c>=L'a' && c<=L'f'))) return false;
    return true;
}
inline const wchar_t* CompatibilityDecision(bool protectedReceipt, unsigned schema,
        bool accepted, const std::wstring& recorded, const std::wstring& current) {
    if (!IsFingerprint(current)) return L"compatibility-unavailable";
    if (!protectedReceipt || schema!=1 || !accepted || !IsFingerprint(recorded))
        return L"compatibility-unverified";
    if (recorded!=current) return L"compatibility-changed";
    return L"compatible";
}
}
