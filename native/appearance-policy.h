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
inline bool Dark(int brightness,bool previous) {
    if(brightness>=0 && brightness<110) return true;
    if(brightness>150) return false;
    return previous;
}
}
