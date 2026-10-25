#include "start-menu-ready.h"
#include <cassert>
#include <stdexcept>
#include <cstdio>
struct Window {
    bool present; int content; bool throws = false;
    explicit operator bool() const { return present; }
    int Content() const { if (throws) throw std::runtime_error("XAML not ready"); return content; }
};
int main() {
    using ClassicDeskStartMenu::ApplyWhenReady;
    bool applying=false; int calls=0, errors=0;
    auto style=[&](int content){ assert(content); ++calls; };
    auto error=[&]{ ++errors; };
    ApplyWhenReady(applying, []{return Window{false,0};}, style, error);
    assert(!applying && calls==0 && errors==0);
    ApplyWhenReady(applying, []{return Window{true,0};}, style, error);
    assert(!applying && calls==0 && errors==0);
    ApplyWhenReady(applying, []{return Window{true,1};}, style, error);
    assert(!applying && calls==1);
    ApplyWhenReady(applying, []{return Window{true,1,true};}, style, error);
    assert(!applying && errors==1);
    ApplyWhenReady(applying, []{return Window{true,1};}, [&](int){throw std::runtime_error("style failed");}, error);
    assert(!applying && errors==2);
    ApplyWhenReady(applying, []{return Window{true,1};}, [&](int){
        ApplyWhenReady(applying, []{return Window{true,1};}, style, error);
    }, error);
    assert(!applying && calls==1 && errors==2);
    // Retry after absent Content or an exception must remain possible.
    ApplyWhenReady(applying, []{return Window{true,2};}, style, error);
    assert(!applying && calls==2 && errors==2);
    std::puts("7 Start menu readiness checks passed");
}
