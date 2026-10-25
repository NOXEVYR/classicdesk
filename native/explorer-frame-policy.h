// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <string_view>

namespace ClassicDeskExplorer {
enum class Style { Navigation, Ribbon };
constexpr Style ParseStyle(std::wstring_view value) {
    return value == L"classicRibbonUI" ? Style::Ribbon : Style::Navigation;
}
// Ribbon must not initialize the modern navigation/XAML path as a side effect.
// The installed hook set is immutable until the engine reloads the module.
constexpr bool NeedsModernHooks(Style style) { return style == Style::Navigation; }
constexpr bool NeedsReload(Style installed, Style requested) { return installed != requested; }
}
