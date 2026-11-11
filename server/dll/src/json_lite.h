#pragma once
// Minimal JSON readers for flat messages (auth, commands, Discord replies); not a general parser.
#include <cstdint>
#include <string>
#include <vector>

namespace nebo {

// The string value of the first "key" in json.
bool extractJsonString(const std::string& json, const char* key, std::string& value);
// The integer value of the first "key" (no exponent, no fraction).
bool extractJsonInteger(const std::string& json, const char* key, int64_t& value);
// A plain decimal number ("-12.345") of the first "key" (no exponent).
bool extractJsonNumber(const std::string& json, const char* key, double& value);
// The strings of the array value of the first "key" (an array of strings only).
bool extractJsonStringArray(const std::string& json, const char* key, std::vector<std::string>& values);
std::string jsonEscape(const std::string& s);

}  // namespace nebo
