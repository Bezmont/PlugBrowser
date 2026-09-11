// Minimal JSON writer.
//
// The worker's only output is one document whose shape is fixed by PlugBrowser.Core's InspectResult,
// so a dependency-free writer is a better fit than pulling in a JSON library: there is nothing to
// parse, and the property names have to match the C# record exactly. System.Text.Json is
// case-sensitive by default, so the PascalCase spellings here are load-bearing.
#pragma once

#include <string>
#include <string_view>

namespace plugbrowser {

// Converts UTF-16 (as the VST3 C ABI returns it) to UTF-8 for the JSON document.
std::string Utf8From(const char16_t* text, size_t capacity);

// Escapes a string for embedding in JSON, per RFC 8259.
std::string JsonEscape(std::string_view text);

/// Builds a JSON document with just enough structure for the worker's report.
class JsonWriter {
public:
    void BeginObject() { Separate(); buffer_ += '{'; first_ = true; depth_++; }
    void EndObject()   { buffer_ += '}'; first_ = false; depth_--; }
    void BeginArray(std::string_view key) { Key(key); buffer_ += '['; first_ = true; depth_++; }
    void EndArray()    { buffer_ += ']'; first_ = false; depth_--; }

    void Write(std::string_view key, std::string_view value) {
        Key(key);
        buffer_ += '"';
        buffer_ += JsonEscape(value);
        buffer_ += '"';
    }

    void Write(std::string_view key, bool value) {
        Key(key);
        buffer_ += value ? "true" : "false";
    }

    void Write(std::string_view key, int value) {
        Key(key);
        buffer_ += std::to_string(value);
    }

    void Write(std::string_view key, unsigned int value) {
        Key(key);
        buffer_ += std::to_string(value);
    }

    void Write(std::string_view key, double value);

    // A null is written explicitly rather than omitted: the C# side treats an absent property and a
    // null one identically, and being explicit makes the document easier to read while debugging.
    void WriteNull(std::string_view key) { Key(key); buffer_ += "null"; }

    // Writes a string property, or null when the text is empty.
    void WriteOrNull(std::string_view key, std::string_view value) {
        if (value.empty()) WriteNull(key); else Write(key, value);
    }

    const std::string& Text() const { return buffer_; }

private:
    void Separate() {
        if (!first_) buffer_ += ',';
        first_ = false;
    }

    void Key(std::string_view key) {
        Separate();
        buffer_ += '"';
        buffer_ += key;
        buffer_ += "\":";
    }

    std::string buffer_;
    bool first_ = true;
    int depth_ = 0;
};

}  // namespace plugbrowser
