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
enum class Surface { Unknown, Desktop, Application, ShellFlyout };
enum class Backdrop { Preserve, Transparent, Opaque };
enum class VisibleApps { Unknown, None, Present };
inline Backdrop BackgroundFor(Surface surface,VisibleApps apps=VisibleApps::Unknown) {
    if(surface==Surface::Desktop) return Backdrop::Transparent;
    if(surface==Surface::Application) return Backdrop::Opaque;
    // Minimize/show-desktop may leave focus on the taskbar or a hidden window.
    // No visible application is positive desktop evidence without a mouse click.
    if(apps==VisibleApps::None) return Backdrop::Transparent;
    return Backdrop::Preserve;
}
enum class Change { Foreground, Minimize, Visibility, Destroyed, Other };
inline bool NeedsRefresh(Change change,bool relevant,bool topLevel,bool tracked) {
    if(change==Change::Foreground || change==Change::Minimize) return true;
    if(change==Change::Destroyed) return tracked;
    if(change==Change::Visibility) return topLevel || relevant || tracked;
    return relevant;
}
inline bool Dark(int brightness,bool previous) {
    if(brightness>=0 && brightness<110) return true;
    if(brightness>150) return false;
    return previous;
}
}
