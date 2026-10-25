// Compile the actual mod entry points against SDK editing stubs. No engine is
// linked or loaded; hook registration and library loading are intercepted here.
#define WH_MOD
#define WH_EDITING
#include <windhawk_api.h>
#include <windhawk_utils.h>
#include <cassert>
#include <cstdio>
#include <vector>
#include <algorithm>

static PCWSTR setting = L"classicRibbonUI";
static bool hookSucceeds = true;
static bool navigationLibrariesAvailable = true, symbolSucceeds = true;
static int hookCalls, settingsFreed, libraryLoads, symbolCalls;
static int failSymbolCall;
static bool legacySelectorAvailable = true, cachedSelectorAvailable = true;
static std::vector<std::wstring> loadedLibraries;
static PCWSTR TestGetStringSetting(PCWSTR) { return setting; }
static void TestFreeStringSetting(PCWSTR) { ++settingsFreed; }
static HMODULE WINAPI TestLoadLibraryEx(LPCWSTR name, HANDLE, DWORD) {
    loadedLibraries.emplace_back(name);
    ++libraryLoads; return navigationLibrariesAvailable ? reinterpret_cast<HMODULE>(1) : nullptr;
}
static HMODULE WINAPI TestLoadLibrary(LPCWSTR) { ++libraryLoads; return nullptr; }
namespace WindhawkUtils {
template<class Target, class Hook, class Original>
BOOL TestSetFunctionHook(Target, Hook, Original*) { ++hookCalls; return hookSucceeds; }
bool TestHookSymbols(HMODULE, const SYMBOL_HOOK*, size_t);
}
// Fake navigation object and USER32 calls: no actual HWND or shell input.
static HWND navHandle = reinterpret_cast<HWND>(0x101);
static LONG navStyle = WS_VISIBLE;
static bool navAlive = true;
static int redrawMessages, invalidations, loadCalls;
static LONG WINAPI TestGetWindowLongW(HWND, int index) { assert(index == GWL_STYLE); return navStyle; }
static BOOL WINAPI TestIsWindow(HWND) { return navAlive; }
static LRESULT WINAPI TestSendMessageW(HWND h, UINT m, WPARAM w, LPARAM l) {
    assert(h == navHandle && m == WM_SETREDRAW && w == TRUE && l == 0);
    ++redrawMessages; navStyle |= WS_VISIBLE; return 0;
}
static BOOL WINAPI TestRedrawWindow(HWND h, const RECT*, HRGN, UINT flags) {
    assert(h == navHandle && flags == (RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN));
    ++invalidations; return TRUE;
}
#define Wh_GetStringSetting TestGetStringSetting
#define Wh_FreeStringSetting TestFreeStringSetting
#define SetFunctionHook TestSetFunctionHook
#define HookSymbols TestHookSymbols
#undef LoadLibraryEx
#define LoadLibraryEx TestLoadLibraryEx
#define LoadLibraryExW TestLoadLibraryEx
#undef LoadLibrary
#define LoadLibraryW TestLoadLibrary
#define GetWindowLongW TestGetWindowLongW
#define IsWindow TestIsWindow
#define SendMessageW TestSendMessageW
#define RedrawWindow TestRedrawWindow
#include "explorer-frame-classic.wh.cpp"

namespace WindhawkUtils {
bool TestHookSymbols(HMODULE, const SYMBOL_HOOK*, size_t) {
    ++symbolCalls;
    if (!symbolSucceeds || symbolCalls == failSymbolCall) return false;
    CanShowModernNavBar_Original = legacySelectorAvailable ? +[](PVOID)->bool { return true; } : nullptr;
    CachedExplorerExtensionState_IsModernNavBarAvailable_Original = cachedSelectorAvailable ? +[](PVOID)->bool { return true; } : nullptr;
    return true;
}
}

struct FakeNav : IOleWindow {
    bool queryFails = false, getFails = false;
    int references = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (queryFails || (iid != IID_IOleWindow && iid != IID_IUnknown)) return E_NOINTERFACE;
        *out = static_cast<IOleWindow*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { return --references; }
    HRESULT STDMETHODCALLTYPE GetWindow(HWND* out) override {
        *out = getFails ? nullptr : navHandle; return getFails ? E_FAIL : S_OK;
    }
    HRESULT STDMETHODCALLTYPE ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }
};
static bool hideOnLoad, replaceOnLoad, destroyOnLoad;
static HRESULT loadResult;
static IStream* expectedStream = reinterpret_cast<IStream*>(0x303);
static HRESULT WINAPI LoadNav(PVOID, IStream* stream) {
    assert(stream == expectedStream); ++loadCalls;
    if (hideOnLoad) navStyle &= ~WS_VISIBLE;
    if (replaceOnLoad) navHandle = reinterpret_cast<HWND>(0x202);
    if (destroyOnLoad) navAlive = false;
    return loadResult;
}
static void CheckNav(bool initiallyVisible, bool hide, bool replace, bool destroy,
                     bool queryFails, bool getFails, HRESULT hr, bool expectedRepair) {
    FakeNav nav; nav.queryFails = queryFails; nav.getFails = getFails;
    navHandle = reinterpret_cast<HWND>(0x101); navAlive = true;
    navStyle = initiallyVisible ? WS_VISIBLE : 0;
    hideOnLoad = hide; replaceOnLoad = replace; destroyOnLoad = destroy; loadResult = hr;
    redrawMessages = invalidations = loadCalls = 0;
    CNavBar_Load_Original = LoadNav;
    assert(CNavBar_Load_Hook(&nav, expectedStream) == hr);
    assert(loadCalls == 1 && nav.references == 1);
    assert(redrawMessages == (expectedRepair ? 1 : 0) && invalidations == redrawMessages);
}

static int forwarded;
static HRESULT WINAPI Forward(REFCLSID, LPUNKNOWN, DWORD, REFIID, LPVOID* output) {
    ++forwarded;
    if (output) *output = reinterpret_cast<void*>(0x1234);
    return S_FALSE;
}
int main() {
    assert(Wh_ModInit());
    assert(hookCalls == 1 && libraryLoads == 2 && symbolCalls == 2);
    assert((loadedLibraries == std::vector<std::wstring>{L"explorerframe.dll", L"explorerframe.dll"}));
    assert(ClassicDeskExplorerTrace.initStage == 2);
    Wh_ModAfterInit();
    assert(ClassicDeskExplorerTrace.initStage == 3 && libraryLoads == 2);
    assert(g_settings.explorerStyle == ExplorerStyle::classicRibbonUI);
    // Ribbon must choose classic navigation before rejecting the XAML adapter.
    // A Load-only hook cannot repair a navigation object that was never made.
    assert(!CanShowModernNavBar_Hook(nullptr));
    assert(!CachedExplorerExtensionState_IsModernNavBarAvailable_Hook(nullptr));
    assert(ClassicDeskExplorerNavSelectionTrace.legacyQueries == 1);
    assert(ClassicDeskExplorerNavSelectionTrace.cachedQueries == 1);
    legacySelectorAvailable = true; cachedSelectorAvailable = false;
    assert(HookExplorerFrameSymbols());
    legacySelectorAvailable = false; cachedSelectorAvailable = true;
    assert(HookExplorerFrameSymbols());
    legacySelectorAvailable = cachedSelectorAvailable = false;
    assert(!HookExplorerFrameSymbols()); // Optional lookups cannot pass with no capability.
    legacySelectorAvailable = cachedSelectorAvailable = true;
    // Reset counters so the original lifecycle assertions remain independent.
    libraryLoads = 2; symbolCalls = 2;

    BOOL reload = TRUE;
    assert(Wh_ModSettingsChanged(&reload) && !reload);
    setting = L"classicNavigationBar";
    assert(Wh_ModSettingsChanged(&reload) && reload);
    // Existing hooks retain their birth mode while the engine arranges reload.
    assert(g_settings.explorerStyle == ExplorerStyle::classicRibbonUI);
    assert(libraryLoads == 2 && hookCalls == 1);
    assert(settingsFreed == 3);

    CoCreateInstance_Original = Forward;
    const GUID adapter = {0x6480100B, 0x5A83, 0x4D1E, {0x9F,0x69,0x8A,0xE5,0xA8,0x8E,0x9A,0x33}};
    void* output = reinterpret_cast<void*>(0x5678);
    assert(CoCreateInstance_Hook(adapter, nullptr, CLSCTX_INPROC_SERVER, IID_IUnknown, &output) == REGDB_E_CLASSNOTREG);
    assert(output == nullptr && forwarded == 0 && ClassicDeskExplorerTrace.blockedAdapters == 1);
    assert(CoCreateInstance_Hook(adapter, nullptr, CLSCTX_INPROC_SERVER, IID_IUnknown, nullptr) == E_POINTER);
    assert(ClassicDeskExplorerTrace.blockedAdapters == 1);
    assert(CoCreateInstance_Hook(CLSID_NULL, nullptr, CLSCTX_INPROC_SERVER, IID_IUnknown, &output) == S_FALSE);
    assert(output == reinterpret_cast<void*>(0x1234) && forwarded == 1);
    Wh_ModUninit();
    assert(libraryLoads == 2); // No hidden window-rebuilding or modern loads.

    CheckNav(true, true, false, false, false, false, S_OK, true);
    CheckNav(true, true, false, false, false, false, E_FAIL, true);
    CheckNav(true, false, false, false, false, false, S_FALSE, false);
    CheckNav(false, true, false, false, false, false, S_OK, false);
    CheckNav(true, true, true, false, false, false, S_OK, false);
    CheckNav(true, true, false, true, false, false, S_OK, false);
    CheckNav(true, true, false, false, true, false, S_OK, false);
    CheckNav(true, true, false, false, false, true, S_OK, false);
    // Repeat loads remain balanced, not a one-shot repair.
    CheckNav(true, true, false, false, false, false, S_OK, true);

    setting = L"classicRibbonUI";
    hookSucceeds = false;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(libraryLoads == 4); // Failed Ribbon install must not fall into WinUI.
    symbolSucceeds = false; hookSucceeds = true;
    const int beforeFailedSymbol = hookCalls;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(hookCalls == beforeFailedSymbol); // Never half-install the Ribbon fallback.
    symbolSucceeds = true;
    // Missing creation selector must not leave a COM-only partial conversion.
    failSymbolCall = symbolCalls + 2;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(hookCalls == beforeFailedSymbol);
    failSymbolCall = 0;
    legacySelectorAvailable = cachedSelectorAvailable = false;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(hookCalls == beforeFailedSymbol);
    legacySelectorAvailable = cachedSelectorAvailable = true;
    navigationLibrariesAvailable = false;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(hookCalls == beforeFailedSymbol);
    assert(std::all_of(loadedLibraries.begin(), loadedLibraries.end(),
                      [](const auto& name) { return name == L"explorerframe.dll"; }));

    // Navigation requires the late FileExplorerExtensions loader hook too.
    setting = L"classicNavigationBar";
    g_settings.explorerStyle = ExplorerStyle::classicNavigationBar;
    assert(!CanShowModernNavBar_Hook(nullptr));
    assert(!CachedExplorerExtensionState_IsModernNavBarAvailable_Hook(nullptr));
    navigationLibrariesAvailable = true;
    hookSucceeds = false;
    const int beforeHooks = hookCalls;
    assert(!Wh_ModInit() && ClassicDeskExplorerTrace.initStage == -1);
    assert(hookCalls == beforeHooks + 1 && libraryLoads > 0);
    hookSucceeds = true;
    assert(Wh_ModInit() && ClassicDeskExplorerTrace.initStage == 2);
    puts("Explorer mod entry checks passed; no engine, windows or real hooks used.");
}
