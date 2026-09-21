#pragma once
#include <algorithm>
#include <array>
namespace ClassicDeskAppearance {
inline int Luminance(unsigned red,unsigned green,unsigned blue) {
    return (red*2126+green*7152+blue*722)/10000;
}
inline int Median(std::array<int,9> samples,int count) {
    if(count<5 || count>9) return -1;
    std::sort(samples.begin(),samples.begin()+count);
    return samples[count/2];
}
struct Bounds { long left,top,right,bottom; };
inline bool ExpandedOnTaskbarMonitor(bool sameMonitor,bool maximized,Bounds frame,Bounds work) {
    if(!sameMonitor || work.right<=work.left || work.bottom<=work.top) return false;
    if(maximized) return true;
    // DWM may round visible frame edges at fractional DPI scales.
    constexpr int tolerance=2;
    return frame.right>frame.left && frame.bottom>frame.top &&
        frame.left<=work.left+tolerance && frame.top<=work.top+tolerance &&
        frame.right>=work.right-tolerance && frame.bottom>=work.bottom-tolerance;
}
enum class Surface { Unknown, Desktop, Application, ExpandedApplication, ShellFlyout };
enum class Backdrop { Preserve, Transparent, Opaque };
enum class VisibleApps { Unknown, None, Present, ExpandedPresent };
inline Backdrop BackgroundFor(Surface surface,VisibleApps apps=VisibleApps::Unknown) {
    if(surface==Surface::Desktop) return Backdrop::Transparent;
    // A focused small window does not uncover the desktop underneath a
    // maximized browser. The visible scene, not focus alone, owns the backdrop.
    if(apps==VisibleApps::ExpandedPresent) return Backdrop::Opaque;
    if(surface==Surface::Application) return Backdrop::Transparent;
    if(surface==Surface::ExpandedApplication) return Backdrop::Opaque;
    // Minimize/show-desktop may leave focus on the taskbar or a hidden window.
    // No visible application is positive desktop evidence without a mouse click.
    if(apps==VisibleApps::None) return Backdrop::Transparent;
    return Backdrop::Preserve;
}
enum class Change { Foreground, Minimize, Visibility, Geometry, Destroyed, Other };
inline bool NeedsRefresh(Change change,bool relevant,bool topLevel,bool tracked) {
    if(change==Change::Foreground || change==Change::Minimize) return true;
    if(change==Change::Destroyed) return tracked;
    if(change==Change::Visibility) return topLevel || relevant || tracked;
    if(change==Change::Geometry) return topLevel || relevant || tracked;
    return relevant || tracked;
}
inline bool Dark(int brightness,bool previous) {
    if(brightness>=0 && brightness<110) return true;
    if(brightness>150) return false;
    return previous;
}
}
