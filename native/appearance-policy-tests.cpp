#include "appearance-policy.h"
#include <cassert>
int main() {
    using namespace ClassicDeskAppearance;
    // A normal browser and a full-screen app use the same opaque policy.
    assert(BackgroundFor(Surface::Application)==Backdrop::Opaque);
    assert(BackgroundFor(Surface::Desktop)==Backdrop::Transparent);
    // Opening Start/quick settings and transient missing foreground windows
    // must not turn an opaque application taskbar transparent.
    assert(BackgroundFor(Surface::ShellFlyout)==Backdrop::Preserve);
    assert(BackgroundFor(Surface::Unknown)==Backdrop::Preserve);
    assert(Luminance(0,0,0)==0 && Luminance(255,255,255)==255);
    assert(Luminance(243,243,243)>150 && Luminance(32,32,32)<110);
    assert(Median({255,255,255,255,255,0,0,0,0},9)==255);
    assert(Median({0,0,0,0,0,255,255,255,255},9)==0);
    assert(Median({255,255,255,255},4)==-1); // Too few unobscured pixels.
    assert(Dark(20,false) && !Dark(240,true));
    for(int light=110;light<=150;light++) { assert(Dark(light,true)); assert(!Dark(light,false)); }
    assert(Dark(-1,true) && !Dark(-1,false)); // Unavailable samples retain state.
}
