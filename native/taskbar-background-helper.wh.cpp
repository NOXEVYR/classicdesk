// ==WindhawkMod==
// @id              taskbar-background-helper
// @name            ClassicDesk adaptive taskbar background
// @description     ClassicDesk fork: clear desktop/windowed apps, opaque maximized apps, event-driven.
// @version         1.2-classicdesk.1
// @author          ClassicDesk contributors
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -ldwmapi -lgdi32 -lole32 -loleaut32 -lruntimeobject
// @license         GPL-3.0
// ==/WindhawkMod==
// SPDX-License-Identifier: GPL-3.0-or-later
// The accent policy follows m417z's Taskbar Background Helper 1.2.
// Native-to-XAML root discovery follows r1file's Dynamic Taskbar Transparency
// 0.3.9 (GPL-3.0), pinned upstream revision:
// f3ec3675168600d1decfcda97d2a12e375903ae9.
// This is a locally built ClassicDesk fork, not the upstream precompiled DLL.

#include <windhawk_utils.h>
#include <dwmapi.h>
#undef GetCurrentTime
#include <algorithm>
#include <array>
#include <atomic>
#include <mutex>
#include <vector>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include "appearance-policy.h"

using namespace winrt::Windows::UI::Xaml;
using winrt::Windows::UI::Core::CoreDispatcherPriority;
namespace {
constexpr UINT WorkMessage = WM_APP + 121;
constexpr UINT_PTR SampleTimer = 1;
constexpr UINT_PTR SettleTimer = 2;
constexpr DWORD ClearColor = 0;
constexpr DWORD LightColor = 0xFFF3F3F3;
constexpr DWORD DarkColor = 0xFF202020;
struct AccentPolicy { int state; UINT flags; DWORD color; int animation; };
struct CompositionData { int attribute; void* data; SIZE_T size; };
using SetComposition = BOOL(WINAPI*)(HWND, CompositionData*);
SetComposition setCompositionOriginal;
std::atomic<HWND> taskbar{nullptr};
std::atomic<HWND> eventWindow{nullptr};
std::atomic<HWND> lastApplicationWindow{nullptr};
HANDLE eventThread;
DWORD eventThreadId;
std::atomic<bool> stopping{false}, workQueued{false};
std::atomic<DWORD> desiredColor{LightColor};
std::atomic<int> desiredTheme{1};
std::atomic<ULONGLONG> eventCount{0}, sampleCount{0}, applyCount{0};
std::mutex rootMutex;
struct Root { winrt::weak_ref<FrameworkElement> element; ElementTheme original; };
std::vector<Root> roots;
using VisualUpdate = void(WINAPI*)(void*);
VisualUpdate visualUpdateOriginal;
VisualUpdate paddingUpdateOriginal;
using LoadLibraryFunction = decltype(&LoadLibraryExW);
LoadLibraryFunction loadLibraryOriginal;
using CreateWindowFunction = decltype(&CreateWindowExW);
CreateWindowFunction createWindowOriginal;
std::atomic<bool> viewHooksAttempted{false};

bool BindTaskbar(HWND window) {
    if(stopping || !window) return false;
    wchar_t name[64]{};GetClassNameW(window,name,ARRAYSIZE(name));
    if(_wcsicmp(name,L"Shell_TrayWnd")) return false;
    DWORD process=0;GetWindowThreadProcessId(window,&process);
    if(process!=GetCurrentProcessId()) return false;
    HWND previous=taskbar.load();
    if(previous && !IsWindow(previous)) taskbar.compare_exchange_strong(previous,nullptr);
    HWND expected=nullptr;
    if(taskbar.compare_exchange_strong(expected,window) && eventWindow)
        PostMessageW(eventWindow,WorkMessage,0,0);
    return taskbar.load()==window;
}

bool IsShellSurface(HWND window) {
    wchar_t name[128]{};
    GetClassNameW(window, name, ARRAYSIZE(name));
    if(!_wcsicmp(name,L"Windows.UI.Core.CoreWindow")) {
        DWORD id=0; GetWindowThreadProcessId(window,&id);
        HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,id);
        wchar_t path[1024]{}; DWORD length=ARRAYSIZE(path);
        bool shell=false;
        if(process) {
            if(QueryFullProcessImageNameW(process,0,path,&length)) {
                const wchar_t* base=wcsrchr(path,L'\\'); base=base?base+1:path;
                shell=!_wcsicmp(base,L"StartMenuExperienceHost.exe") ||
                    !_wcsicmp(base,L"ShellExperienceHost.exe") || !_wcsicmp(base,L"SearchHost.exe");
            }
            CloseHandle(process);
        }
        return shell;
    }
    return !_wcsicmp(name,L"Shell_TrayWnd") || !_wcsicmp(name,L"Shell_SecondaryTrayWnd") ||
        !_wcsicmp(name,L"XamlExplorerHostIslandWindow") ||
        !_wcsicmp(name,L"TopLevelWindowForOverflowXamlIsland");
}
bool IsDesktop(HWND window) {
    wchar_t name[128]{};
    GetClassNameW(window,name,ARRAYSIZE(name));
    return !_wcsicmp(name,L"Progman") || !_wcsicmp(name,L"WorkerW");
}
ClassicDeskAppearance::VisibleApps VisibleApplications(HWND& application) {
    using namespace ClassicDeskAppearance;
    MONITORINFO monitor{sizeof(monitor)};
    if(!GetMonitorInfoW(MonitorFromWindow(taskbar,MONITOR_DEFAULTTONEAREST),&monitor)) return VisibleApps::Unknown;
    struct Scan { RECT work; HWND found=nullptr; bool uncertain=false; } scan{monitor.rcWork};
    BOOL completed=EnumWindows([](HWND window,LPARAM value)->BOOL {
        auto& scan=*reinterpret_cast<Scan*>(value);
        if(!IsWindowVisible(window) || IsIconic(window) || IsDesktop(window) || IsShellSurface(window)) return TRUE;
        auto style=GetWindowLongPtrW(window,GWL_EXSTYLE);
        if((style&(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE)) && !(style&WS_EX_APPWINDOW)) return TRUE;
        DWORD cloaked=0;
        if(SUCCEEDED(DwmGetWindowAttribute(window,14,&cloaked,sizeof(cloaked))) && cloaked) return TRUE;
        RECT frame{},visible{};
        if(!GetWindowRect(window,&frame)) {scan.uncertain=true;return TRUE;}
        if(!IntersectRect(&visible,&frame,&scan.work)) return TRUE;
        scan.found=window;return FALSE;
    },reinterpret_cast<LPARAM>(&scan));
    application=scan.found;
    if(scan.found) return VisibleApps::Present;
    return completed && !scan.uncertain ? VisibleApps::None : VisibleApps::Unknown;
}
bool IsExpanded(HWND window) {
    auto taskbarMonitor=MonitorFromWindow(taskbar,MONITOR_DEFAULTTONEAREST);
    MONITORINFO monitor{sizeof(monitor)};
    if(!GetMonitorInfoW(taskbarMonitor,&monitor)) return false;
    RECT frame{};
    if(FAILED(DwmGetWindowAttribute(window,9,&frame,sizeof(frame)))) GetWindowRect(window,&frame);
    return ClassicDeskAppearance::ExpandedOnTaskbarMonitor(
        MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST)==taskbarMonitor,IsZoomed(window),
        {frame.left,frame.top,frame.right,frame.bottom},
        {monitor.rcWork.left,monitor.rcWork.top,monitor.rcWork.right,monitor.rcWork.bottom});
}
// Nine local pixels are read only after a relevant event. No screen image,
// window title, browsing content, or colour samples are retained or logged.
int SampleBrightness(HWND window, const RECT& area, bool desktop) {
    HDC dc=GetDC(nullptr); if(!dc) return -1;
    std::array<int,9> values{}; int count=0;
    int y=area.bottom-6;
    for(int i=0;i<9;i++) {
        POINT p{area.left+(area.right-area.left)*(i+1)/10,y};
        HWND hit=GetAncestor(WindowFromPoint(p),GA_ROOT);
        if(!hit || (desktop ? !IsDesktop(hit) : hit!=window)) continue;
        COLORREF color=GetPixel(dc,p.x,p.y);
        if(color==CLR_INVALID) continue;
        values[count++]=ClassicDeskAppearance::Luminance(GetRValue(color),GetGValue(color),GetBValue(color));
    }
    ReleaseDC(nullptr,dc);
    ++sampleCount;
    return ClassicDeskAppearance::Median(values,count);
}
void ApplyNative() {
    if(!taskbar || !IsWindow(taskbar) || !setCompositionOriginal) return;
    AccentPolicy policy{2,0x13,desiredColor.load(),0};
    CompositionData data{19,&policy,sizeof(policy)};
    setCompositionOriginal(taskbar,&data);
}
void ApplyRoots(bool restore) {
    std::vector<Root> snapshot;
    { std::lock_guard lock(rootMutex); snapshot=roots; }
    for(auto& item:snapshot) {
        try {
            auto root=item.element.get(); if(!root) continue;
            auto dispatcher=root.Dispatcher(); if(!dispatcher) continue;
            auto theme=restore ? item.original : static_cast<ElementTheme>(desiredTheme.load());
            auto apply=[weak=item.element,theme,restore]() {
                if(auto value=weak.get()) {
                    if(!restore || value.RequestedTheme()==static_cast<ElementTheme>(desiredTheme.load())) value.RequestedTheme(theme);
                }
            };
            if(dispatcher.HasThreadAccess()) apply();
            else dispatcher.RunAsync(CoreDispatcherPriority::Normal,apply).get();
            // All dispatched work is awaited. Unload never leaves a DLL callback queued.
        } catch(...) { }
    }
}
void DiscoverRoot(void* instance) {
    if(stopping) return;
    try {
        { std::lock_guard lock(rootMutex); if(!roots.empty() && roots.front().element.get()) return; }
        SetPropW(taskbar,L"ClassicDesk.Adaptive.RootStage",(HANDLE)1);
        winrt::Windows::Foundation::IUnknown unknown;
        winrt::copy_from_abi(unknown,(void**)instance+3);
        auto current=unknown.try_as<FrameworkElement>();
        if(!current) return;
        SetPropW(taskbar,L"ClassicDesk.Adaptive.RootStage",(HANDLE)2);
        auto xamlRoot=current.XamlRoot();
        if(!xamlRoot) return;
        auto content=xamlRoot.Content().try_as<FrameworkElement>();
        if(!content) return;
        std::vector<Root> discovered{{winrt::make_weak(content),content.RequestedTheme()}};
        std::vector<winrt::Windows::UI::Xaml::DependencyObject> pending{content};
        for(int visited=0;!pending.empty() && visited<1024;visited++) {
            auto node=pending.back();pending.pop_back();
            if(auto element=node.try_as<FrameworkElement>()) {
                if(element!=content && winrt::get_class_name(element)==L"SystemTray.SystemTrayFrame")
                    discovered.push_back({winrt::make_weak(element),element.RequestedTheme()});
            }
            int count=Media::VisualTreeHelper::GetChildrenCount(node);
            for(int i=0;i<count;i++) pending.push_back(Media::VisualTreeHelper::GetChild(node,i));
        }
        {std::lock_guard lock(rootMutex);roots=discovered;}
        SetPropW(taskbar,L"ClassicDesk.Adaptive.RootStage",(HANDLE)3);
        SetPropW(taskbar,L"ClassicDesk.Adaptive.RootCount",(HANDLE)(ULONG_PTR)discovered.size());
        ApplyRoots(false);
    } catch(...) { }
}
void WINAPI VisualHook(void* instance) { visualUpdateOriginal(instance); DiscoverRoot(instance); }
void WINAPI PaddingHook(void* instance) { paddingUpdateOriginal(instance); DiscoverRoot(instance); }
void RefreshState() {
    if(stopping) return;
    if(!IsWindow(taskbar)) BindTaskbar(FindWindowW(L"Shell_TrayWnd",nullptr));
    if(!IsWindow(taskbar)) return;
    HWND foreground=GetForegroundWindow();
    using namespace ClassicDeskAppearance;
    DWORD cloaked=0;
    bool hidden=!foreground || IsIconic(foreground) || !IsWindowVisible(foreground) ||
        (SUCCEEDED(DwmGetWindowAttribute(foreground,14,&cloaked,sizeof(cloaked))) && cloaked);
    Surface surface=IsShellSurface(foreground) ? Surface::ShellFlyout :
        hidden ? Surface::Unknown : IsDesktop(foreground) ? Surface::Desktop : Surface::Application;
    auto apps=VisibleApps::Unknown;
    if(surface==Surface::Unknown || surface==Surface::ShellFlyout) {
        // Focus can stay on the taskbar after minimize. Resolve the top visible
        // app so a remaining normal window also restores transparency.
        HWND visible=nullptr;
        apps=VisibleApplications(visible);
        if(visible) { foreground=visible;surface=Surface::Application; }
    }
    if(surface==Surface::Application && IsExpanded(foreground)) surface=Surface::ExpandedApplication;
    auto backdrop=BackgroundFor(surface,apps);
    if(backdrop==Backdrop::Preserve) return;
    bool opaque=backdrop==Backdrop::Opaque;
    lastApplicationWindow=surface==Surface::Application || surface==Surface::ExpandedApplication ? foreground : nullptr;
    MONITORINFO monitor{sizeof(monitor)};
    bool hasMonitor=GetMonitorInfoW(MonitorFromWindow(opaque?foreground:taskbar.load(),MONITOR_DEFAULTTONEAREST),&monitor);
    int brightness=-1;
    if(hasMonitor) {
        RECT area=monitor.rcWork;
        if(opaque) {
            RECT frame{},visible{};
            if(SUCCEEDED(DwmGetWindowAttribute(foreground,9,&frame,sizeof(frame))) &&
                IntersectRect(&visible,&frame,&area) && visible.bottom-visible.top>12)
                brightness=SampleBrightness(foreground,visible,false);
        } else brightness=SampleBrightness(foreground,area,true);
    }
    bool dark=desiredTheme.load()==2;
    if(brightness>=0) {
        // Hysteresis avoids flicker around medium-grey page backgrounds.
        dark=ClassicDeskAppearance::Dark(brightness,dark);
    } else if(opaque) {
        BOOL nativeDark=FALSE;
        if(SUCCEEDED(DwmGetWindowAttribute(foreground,20,&nativeDark,sizeof(nativeDark)))) dark=nativeDark!=FALSE;
        // Failed colour detection preserves the last theme, never transparency.
    }
    DWORD color=opaque ? (dark?DarkColor:LightColor) : ClearColor;
    int theme=dark?2:1;
    if(desiredColor.exchange(color)!=color || desiredTheme.load()!=theme) {
        desiredTheme=theme; ApplyNative(); ApplyRoots(false); ++applyCount;
    }
    SetPropW(taskbar,L"ClassicDesk.Adaptive.Thread",(HANDLE)(ULONG_PTR)GetCurrentThreadId());
    SetPropW(taskbar,L"ClassicDesk.Adaptive.Mode",(HANDLE)(ULONG_PTR)(opaque?(dark?3:2):1));
    SetPropW(taskbar,L"ClassicDesk.Adaptive.Samples",(HANDLE)(ULONG_PTR)sampleCount.load());
    SetPropW(taskbar,L"ClassicDesk.Adaptive.Events",(HANDLE)(ULONG_PTR)eventCount.load());
}
LRESULT CALLBACK EventWindowProc(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WorkMessage) {
        workQueued=false;
        SetTimer(window,SampleTimer,180,nullptr);
        SetTimer(window,SettleTimer,750,nullptr); // One final check after DWM paints the new window.
        return 0;
    }
    if(message==WM_TIMER && (w==SampleTimer || w==SettleTimer)) { KillTimer(window,w); RefreshState(); return 0; }
    if(message==WM_CLOSE) { KillTimer(window,SampleTimer); KillTimer(window,SettleTimer); DestroyWindow(window); PostQuitMessage(0); return 0; }
    return DefWindowProcW(window,message,w,l);
}
void CALLBACK WindowEvent(HWINEVENTHOOK,DWORD event,HWND window,LONG object,LONG,DWORD,DWORD) {
    if(stopping || !eventWindow || !window) return;
    if(event!=EVENT_SYSTEM_FOREGROUND && event!=EVENT_SYSTEM_MINIMIZESTART && event!=EVENT_SYSTEM_MINIMIZEEND && object!=OBJID_WINDOW) return;
    HWND foreground=GetForegroundWindow();
    using namespace ClassicDeskAppearance;
    Change change=event==EVENT_SYSTEM_FOREGROUND ? Change::Foreground :
        (event==EVENT_SYSTEM_MINIMIZESTART || event==EVENT_SYSTEM_MINIMIZEEND) ? Change::Minimize :
        (event==EVENT_OBJECT_SHOW || event==EVENT_OBJECT_HIDE) ? Change::Visibility :
        event==EVENT_OBJECT_DESTROY ? Change::Destroyed : Change::Other;
    if(!NeedsRefresh(change,window==foreground || IsDesktop(window),
        GetAncestor(window,GA_ROOT)==window,window==lastApplicationWindow.load())) return;
    ++eventCount;
    if(!workQueued.exchange(true)) PostMessageW(eventWindow,WorkMessage,0,0);
}
DWORD WINAPI EventThread(void*) {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    WNDCLASSW wc{}; wc.lpfnWndProc=EventWindowProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"ClassicDesk.AdaptiveTaskbar.Events.v1";
    if(!RegisterClassW(&wc)) { winrt::uninit_apartment(); return 1; }
    eventWindow=CreateWindowExW(0,wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
    if(!eventWindow) { UnregisterClassW(wc.lpszClassName,wc.hInstance); winrt::uninit_apartment(); return 1; }
    std::vector<HWINEVENTHOOK> hooks;
    for(DWORD event:{EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_MINIMIZESTART,EVENT_SYSTEM_MINIMIZEEND,EVENT_OBJECT_LOCATIONCHANGE,EVENT_OBJECT_NAMECHANGE,EVENT_OBJECT_SHOW,EVENT_OBJECT_HIDE,EVENT_OBJECT_DESTROY}) {
        auto hook=SetWinEventHook(event,event,nullptr,WindowEvent,0,0,WINEVENT_OUTOFCONTEXT);
        if(hook) hooks.push_back(hook);
    }
    PostMessageW(eventWindow,WorkMessage,0,0);
    MSG message{};
    while(GetMessageW(&message,nullptr,0,0)>0) { TranslateMessage(&message); DispatchMessageW(&message); }
    for(auto hook:hooks) UnhookWinEvent(hook);
    eventWindow=nullptr; UnregisterClassW(wc.lpszClassName,wc.hInstance);
    winrt::uninit_apartment(); return 0;
}
BOOL WINAPI CompositionHook(HWND window,CompositionData* data) {
    if(!taskbar) BindTaskbar(window);
    if(!stopping && window==taskbar && data && data->attribute==19) {
        AccentPolicy policy{2,0x13,desiredColor.load(),0};
        CompositionData replacement{19,&policy,sizeof(policy)};
        return setCompositionOriginal(window,&replacement);
    }
    return setCompositionOriginal(window,data);
}
HMODULE TaskbarViewModule() {
    auto module=GetModuleHandleW(L"Taskbar.View.dll");
    return module ? module : GetModuleHandleW(L"ExplorerExtensions.dll");
}
bool HookView(HMODULE module,bool apply) {
    if(!module || stopping || viewHooksAttempted.exchange(true)) return true;
    WindhawkUtils::SYMBOL_HOOK hooks[]={
        {{LR"(private: void __cdecl winrt::Taskbar::implementation::TaskListButton::UpdateVisualStates(void))"},&visualUpdateOriginal,VisualHook},
        {{LR"(private: void __cdecl winrt::Taskbar::implementation::TaskListButton::UpdateButtonPadding(void))"},&paddingUpdateOriginal,PaddingHook}
    };
    if(!WindhawkUtils::HookSymbols(module,hooks,ARRAYSIZE(hooks))) {
        Wh_Log(L"ClassicDesk: taskbar theme symbols unavailable; no repeated hook attempts.");return false;
    }
    if(apply && !Wh_ApplyHookOperations()) return false;
    return true;
}
HMODULE WINAPI LoadLibraryHook(LPCWSTR path,HANDLE file,DWORD flags) {
    auto module=loadLibraryOriginal(path,file,flags);
    if(module && !stopping && !viewHooksAttempted && module==TaskbarViewModule()) HookView(module,true);
    return module;
}
HWND WINAPI CreateWindowHook(DWORD exStyle,LPCWSTR cls,LPCWSTR title,DWORD style,
    int x,int y,int width,int height,HWND parent,HMENU menu,HINSTANCE instance,LPVOID param) {
    HWND window=createWindowOriginal(exStyle,cls,title,style,x,y,width,height,parent,menu,instance,param);
    if(BindTaskbar(window)) ApplyNative();
    return window;
}
}
BOOL Wh_ModInit() {
    if(!Wh_GetIntSetting(L"followMaximizedWindow")) return FALSE;
    BindTaskbar(FindWindowW(L"Shell_TrayWnd",nullptr));
    // Early injection is valid: subscribe before Taskbar.View or Shell_TrayWnd
    // exist instead of failing startup and waiting for a manual engine reload.
    if(!taskbar) desiredColor=ClearColor;
    auto address=(SetComposition)GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetWindowCompositionAttribute");
    if(!address || !WindhawkUtils::SetFunctionHook(address,CompositionHook,&setCompositionOriginal)) return FALSE;
    if(!WindhawkUtils::SetFunctionHook(CreateWindowExW,CreateWindowHook,&createWindowOriginal)) return FALSE;
    auto kernel=GetModuleHandleW(L"kernelbase.dll");
    auto loader=kernel ? reinterpret_cast<LoadLibraryFunction>(GetProcAddress(kernel,"LoadLibraryExW")) : nullptr;
    if(!loader || !WindhawkUtils::SetFunctionHook(loader,LoadLibraryHook,&loadLibraryOriginal)) return FALSE;
    if(!HookView(TaskbarViewModule(),false)) return FALSE;
    return TRUE;
}
void Wh_ModAfterInit() {
    HookView(TaskbarViewModule(),true); // Close the Init-to-hook-activation race.
    if(!taskbar) BindTaskbar(FindWindowW(L"Shell_TrayWnd",nullptr));
    ApplyNative(); eventThread=CreateThread(nullptr,0,EventThread,nullptr,0,&eventThreadId);
}
void Wh_ModBeforeUninit() {
    stopping=true;
    if(eventThread) {
        // Ensure the message queue exists even if unload follows startup immediately.
        for(int i=0;i<100 && !eventWindow;i++) { if(WaitForSingleObject(eventThread,10)==WAIT_OBJECT_0) break; }
        if(eventWindow) PostMessageW(eventWindow,WM_CLOSE,0,0);
        else PostThreadMessageW(eventThreadId,WM_QUIT,0,0);
        WaitForSingleObject(eventThread,INFINITE); CloseHandle(eventThread); eventThread=nullptr;
    }
    ApplyRoots(true);
    { std::lock_guard lock(rootMutex); roots.clear(); }
}
void Wh_ModUninit() {
    desiredColor=ClearColor; ApplyNative();
    if((DWORD)(ULONG_PTR)GetPropW(taskbar,L"ClassicDesk.Adaptive.Thread")==eventThreadId) {
        for(auto name:{L"ClassicDesk.Adaptive.Thread",L"ClassicDesk.Adaptive.Mode",L"ClassicDesk.Adaptive.Samples",L"ClassicDesk.Adaptive.Events",L"ClassicDesk.Adaptive.RootStage",L"ClassicDesk.Adaptive.RootCount"}) RemovePropW(taskbar,name);
    }
}
void Wh_ModSettingsChanged() { if(eventWindow) PostMessageW(eventWindow,WorkMessage,0,0); }
