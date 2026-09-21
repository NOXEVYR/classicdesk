#include "service-session.h"
#include <cassert>
#include <vector>
struct Fake {
    bool start=true,scan=true; std::vector<int> calls;
    void* Start(){calls.push_back(1);return start?(void*)1:nullptr;}
    bool Scan(void*){calls.push_back(2);return scan;}
    void End(void*){calls.push_back(3);}
};
int main(){
    {ClassicDeskService::CrashWindow w;assert(!w.Add(0));assert(!w.Add(100));assert(w.Add(60000));}
    {ClassicDeskService::CrashWindow w;assert(!w.Add(10));assert(!w.Add(20));assert(!w.Add(60011));assert(!w.Add(60021));assert(w.Add(60022));}
    {Fake api;{ClassicDeskService::Session s(api);assert(s.Start());assert(!s.Start());assert(s.Scan());s.Stop();s.Stop();assert(!s.Scan());}assert((api.calls==std::vector<int>{1,2,2,3}));}
    {Fake api;api.start=false;{ClassicDeskService::Session s(api);assert(!s.Start());}assert((api.calls==std::vector<int>{1}));}
    {Fake api;api.scan=false;{ClassicDeskService::Session s(api);assert(!s.Start());}assert((api.calls==std::vector<int>{1,2,3}));}
    {Fake api;{ClassicDeskService::Session s(api);assert(s.Start());}assert((api.calls==std::vector<int>{1,2,3}));}
}
