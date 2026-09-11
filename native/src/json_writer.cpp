#include "json_writer.h"

#include <cstdio>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace plugbrowser {

std::string Utf8From(const char16_t* text, size_t capacity) {
    if (!text) return {};

    // The ABI returns fixed-size arrays that are null-terminated in practice, but the capacity is the
    // real bound: a plug-in that fills the array without terminating it must not run us off the end.
    size_t length = 0;
    while (length < capacity && text[length] != u'\0') length++;
    if (length == 0) return {};

    int bytes = ::WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<const wchar_t*>(text),
                                      static_cast<int>(length), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return {};

    std::string result(static_cast<size_t>(bytes), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<const wchar_t*>(text), static_cast<int>(length),
                          result.data(), bytes, nullptr, nullptr);
    return result;
}

std::string JsonEscape(std::string_view text) {
    std::string out;
    out.reserve(text.size() + 8);

    for (unsigned char c : text) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b";  break;
            case '\f': out += "\\f";  break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (c < 0x20) {
                    // Control characters must be escaped; plug-in strings occasionally contain them.
                    char escape[7];
                    std::snprintf(escape, sizeof(escape), "\\u%04x", c);
                    out += escape;
                } else {
                    out += static_cast<char>(c);
                }
        }
    }
    return out;
}

void JsonWriter::Write(std::string_view key, double value) {
    Key(key);

    // "R"-style round-tripping, and never a locale-dependent decimal comma, which would produce a
    // document the C# side cannot parse.
    char text[40];
    std::snprintf(text, sizeof(text), "%.17g", value);

    for (char* p = text; *p; ++p) {
        if (*p == ',') *p = '.';
    }
    buffer_ += text;
}

}  // namespace plugbrowser
