#pragma once

namespace ClassicDeskStartMenu {
// XAML can expose a Window before its Content exists during shell startup.
// Keep callbacks contained and release the recursion guard on every exit.
template<class GetWindow, class Apply, class Error>
void ApplyWhenReady(bool& applying, GetWindow getWindow, Apply apply, Error error) {
    if (applying) return;
    applying = true;
    struct Reset { bool& value; ~Reset() { value = false; } } reset{applying};
    try {
        auto window = getWindow();
        if (!window) return;
        auto content = window.Content();
        if (!content) return;
        apply(content);
    } catch (...) {
        error();
    }
}
}
