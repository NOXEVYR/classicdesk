#include "explorer-frame-policy.h"
#include <cassert>
using namespace ClassicDeskExplorer;
int main() {
    assert(ParseStyle(L"classicRibbonUI") == Style::Ribbon);
    assert(ParseStyle(L"classicNavigationBar") == Style::Navigation);
    assert(ParseStyle(L"") == Style::Navigation);
    assert(!NeedsModernHooks(Style::Ribbon));
    assert(NeedsModernHooks(Style::Navigation));
    assert(!NeedsReload(Style::Ribbon, Style::Ribbon));
    assert(!NeedsReload(Style::Navigation, Style::Navigation));
    assert(NeedsReload(Style::Ribbon, Style::Navigation));
    assert(NeedsReload(Style::Navigation, Style::Ribbon));
}
