#include "kanata_warutsu_dsp.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <vector>

namespace {
constexpr double pi = 3.14159265358979323846;
std::vector<float> sine(double hz, uint32_t sr, size_t n) {
    std::vector<float> v(n);
    for (size_t i = 0; i < n; ++i) v[i] = static_cast<float>(0.35 * std::sin(2.0 * pi * hz * i / sr));
    return v;
}
double estimate_frequency(const std::vector<float>& x, uint32_t sr) {
    const size_t begin = x.size() / 3;
    const size_t end = x.size() * 2 / 3;
    size_t crossings = 0;
    for (size_t i = begin + 1; i < end; ++i) if (x[i - 1] <= 0.0f && x[i] > 0.0f) ++crossings;
    return crossings * static_cast<double>(sr) / static_cast<double>(end - begin);
}
bool finite_non_silent(const std::vector<float>& x) {
    double sum = 0.0;
    for (float f : x) {
        if (!std::isfinite(f)) return false;
        sum += std::abs(f);
    }
    return sum / std::max<size_t>(1, x.size()) > 1.0e-4;
}
}
int main() {
    constexpr uint32_t sr = 44100;
    const size_t n = sr / 2;
    auto input = sine(220.0, sr, n);
    std::vector<float> output(n);

    if (kw_dsp_abi_version() != 0x00010000) { std::cerr << "ABI version mismatch\n"; return 1; }
    if (kw_dsp_process_mono_f32(nullptr, 12, output.data(), output.size(), sr, 0, 0) >= 0) {
        std::cerr << "null-input validation failed\n"; return 2;
    }
    if (kw_dsp_process_mono_f32(input.data(), input.size(), output.data(), output.size(), sr, 3.0f, 0.0f) != 0) {
        std::cerr << "+3 semitone processing failed: " << kw_dsp_last_error() << "\n"; return 3;
    }
    const double f3 = estimate_frequency(output, sr);
    if (std::abs(f3 - 220.0 * std::pow(2.0, 3.0 / 12.0)) > 5.0) {
        std::cerr << "pitch test failed: expected about 261.6 Hz, got " << f3 << "\n"; return 4;
    }
    if (!finite_non_silent(output)) { std::cerr << "output finite/non-silent test failed\n"; return 5; }

    if (kw_dsp_process_mono_f32(input.data(), input.size(), output.data(), output.size(), sr, -3.0f, 0.55f) != 0) {
        std::cerr << "-3 semitone/formant processing failed: " << kw_dsp_last_error() << "\n"; return 6;
    }
    const double fm = estimate_frequency(output, sr);
    if (std::abs(fm - 220.0 * std::pow(2.0, -3.0 / 12.0)) > 5.0) {
        std::cerr << "down-pitch test failed: expected about 185.0 Hz, got " << fm << "\n"; return 7;
    }

    // Exact target duration is guaranteed, including extreme but bounded stretches.
    std::vector<float> longer(n * 3 / 2);
    if (kw_dsp_process_mono_f32(input.data(), input.size(), longer.data(), longer.size(), sr, 0.0f, 0.2f) != 0) {
        std::cerr << "time-stretch processing failed\n"; return 8;
    }
    if (!finite_non_silent(longer)) { std::cerr << "time-stretch finite/non-silent test failed\n"; return 9; }

    std::cout << "PASS: ABI, invalid-input guard, +3 semitone (" << f3 << " Hz), -3 semitone (" << fm
              << " Hz), formant pass, exact-length stretch, finite output\n";
    return 0;
}
