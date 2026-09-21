#include "appearance-policy.h"
#include <cassert>
int main() {
    using namespace ClassicDeskAppearance;
    // Ordinary browser/app windows leave the wallpaper visible. Only expanded
    // windows make the bar opaque; restoring a window must clear it again.
    assert(BackgroundFor(Surface::Application)==Backdrop::Transparent);
    // Small foreground app over a still-visible maximized browser remains
    // opaque. Restoring/minimizing the last expanded window clears the bar.
    assert(BackgroundFor(Surface::Application,VisibleApps::ExpandedPresent)==Backdrop::Opaque);
    assert(BackgroundFor(Surface::Application,VisibleApps::Present)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::Application,VisibleApps::None)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::ShellFlyout,VisibleApps::ExpandedPresent)==Backdrop::Opaque);
    assert(BackgroundFor(Surface::Unknown,VisibleApps::ExpandedPresent)==Backdrop::Opaque);
    assert(BackgroundFor(Surface::Desktop,VisibleApps::ExpandedPresent)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::ExpandedApplication)==Backdrop::Opaque);
    Bounds work{0,0,2560,1368};
    assert(!ExpandedOnTaskbarMonitor(true,false,{500,100,2200,1200},work));
    assert(ExpandedOnTaskbarMonitor(true,true,{},work));
    assert(ExpandedOnTaskbarMonitor(true,false,{0,0,2560,1440},work));
    assert(ExpandedOnTaskbarMonitor(true,false,work,work));
    assert(ExpandedOnTaskbarMonitor(true,false,{2,2,2558,1366},work));
    assert(!ExpandedOnTaskbarMonitor(true,false,{3,0,2560,1368},work));
    assert(!ExpandedOnTaskbarMonitor(true,false,{0,0,1280,1368},work));
    assert(!ExpandedOnTaskbarMonitor(false,true,work,work));
    assert(!ExpandedOnTaskbarMonitor(false,false,work,work));
    assert(!ExpandedOnTaskbarMonitor(true,false,{},work));
    assert(!ExpandedOnTaskbarMonitor(true,true,work,{}));
    assert(BackgroundFor(Surface::Desktop)==Backdrop::Transparent);
    // Opening Start/quick settings and transient missing foreground windows
    // must not turn an opaque application taskbar transparent.
    assert(BackgroundFor(Surface::ShellFlyout)==Backdrop::Preserve);
    assert(BackgroundFor(Surface::Unknown)==Backdrop::Preserve);
    assert(BackgroundFor(Surface::ShellFlyout,VisibleApps::None)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::Unknown,VisibleApps::None)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::ShellFlyout,VisibleApps::Present)==Backdrop::Preserve);
    assert(BackgroundFor(Surface::Unknown,VisibleApps::Unknown)==Backdrop::Preserve);
    assert(BackgroundFor(Surface::Desktop,VisibleApps::Present)==Backdrop::Transparent);
    assert(BackgroundFor(Surface::Application,VisibleApps::None)==Backdrop::Transparent);
    // Minimize-end/hide often arrive after the former app has lost focus.
    assert(NeedsRefresh(Change::Minimize,false,true,true));
    assert(NeedsRefresh(Change::Visibility,false,true,false));
    assert(NeedsRefresh(Change::Destroyed,false,false,true));
    assert(!NeedsRefresh(Change::Visibility,false,false,false));
    assert(!NeedsRefresh(Change::Other,false,true,false));
    assert(NeedsRefresh(Change::Geometry,false,true,false)); // Background app maximizes/restores.
    assert(!NeedsRefresh(Change::Geometry,false,false,false)); // Child controls do not rescan.
    assert(NeedsRefresh(Change::Other,false,true,true)); // Tracked background app theme/name event.
    assert(Luminance(0,0,0)==0 && Luminance(255,255,255)==255);
    assert(Luminance(243,243,243)>150 && Luminance(32,32,32)<110);
    assert(Median({255,255,255,255,255,0,0,0,0},9)==255);
    assert(Median({0,0,0,0,0,255,255,255,255},9)==0);
    assert(Median({255,255,255,255},4)==-1); // Too few unobscured pixels.
    assert(Dark(20,false) && !Dark(240,true));
    for(int light=110;light<=150;light++) { assert(Dark(light,true)); assert(!Dark(light,false)); }
    assert(Dark(-1,true) && !Dark(-1,false)); // Unavailable samples retain state.
}
