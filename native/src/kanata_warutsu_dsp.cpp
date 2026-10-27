#include "kanata_warutsu_dsp.h"

#include <algorithm>
#include <cmath>
#include <complex>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <limits>
#include <string>
#include <vector>

namespace {
constexpr double kPi = 3.141592653589793238462643383279502884;
constexpr uint64_t kMaxFrames = 100'000'000ULL;
thread_local std::string g_last_error;

inline double sinc(double x) noexcept {
    if (std::abs(x) < 1.0e-12) return 1.0;
    const double px = kPi * x;
    return std::sin(px) / px;
}

inline double finite_or_zero(float x) noexcept {
    return std::isfinite(x) ? static_cast<double>(x) : 0.0;
}

float sample_linear_raw(const float* x, size_t n, double pos) {
    if (x == nullptr || n == 0) return 0.0f;
    if (pos <= 0.0) return x[0];
    const double last = static_cast<double>(n - 1);
    if (pos >= last) return x[n - 1];
    const auto i = static_cast<size_t>(pos);
    const double t = pos - static_cast<double>(i);
    return static_cast<float>((1.0 - t) * x[i] + t * x[i + 1]);
}

float sample_linear(const std::vector<float>& x, double pos) {
    return sample_linear_raw(x.data(), x.size(), pos);
}

float sample_sinc(const float* src, size_t n, double pos, double cutoff, int radius = 18) {
    if (n == 0) return 0.0f;
    if (n == 1) return src[0];
    const long center = static_cast<long>(std::floor(pos));
    double sum = 0.0;
    double norm = 0.0;
    const double c = std::clamp(cutoff, 0.05, 1.0);
    for (long j = center - radius + 1; j <= center + radius; ++j) {
        if (j < 0 || j >= static_cast<long>(n)) continue;
        const double d = static_cast<double>(j) - pos;
        const double u = d / static_cast<double>(radius);
        if (std::abs(u) >= 1.0) continue;
        // Blackman window suppresses ringing while retaining a compact kernel.
        const double window = 0.42 + 0.5 * std::cos(kPi * u) + 0.08 * std::cos(2.0 * kPi * u);
        const double w = c * sinc(c * d) * window;
        sum += static_cast<double>(src[static_cast<size_t>(j)]) * w;
        norm += w;
    }
    return static_cast<float>(std::abs(norm) > 1.0e-12 ? sum / norm : sample_linear_raw(src, n, pos));
}

std::vector<float> pitch_resample(const float* input, size_t input_n, double semitones) {
    const double ratio = std::pow(2.0, semitones / 12.0);
    const double requested = static_cast<double>(input_n) / ratio;
    const size_t intermediate_n = std::max<size_t>(1, static_cast<size_t>(std::llround(requested)));
    std::vector<float> out(intermediate_n);
    const double cutoff = std::min(1.0, 1.0 / ratio) * 0.96;
    for (size_t i = 0; i < intermediate_n; ++i) {
        out[i] = sample_sinc(input, input_n, static_cast<double>(i) * ratio, cutoff);
    }
    return out;
}

struct FrameMap {
    size_t out_start;
    size_t in_start;
};

size_t choose_window(uint32_t sample_rate) {
    size_t desired = static_cast<size_t>(std::llround(sample_rate * 0.046));
    size_t n = 512;
    while (n < desired && n < 4096) n <<= 1;
    return std::clamp<size_t>(n, 512, 4096);
}

std::vector<float> stretch_wsola(const std::vector<float>& input, size_t output_n,
                                 uint32_t sample_rate, std::vector<FrameMap>& map) {
    if (output_n == 0) return {};
    if (input.empty()) return std::vector<float>(output_n, 0.0f);
    if (input.size() < 256 || output_n < 256) {
        std::vector<float> simple(output_n);
        const double step = output_n > 1 ? static_cast<double>(input.size() - 1) / (output_n - 1) : 0.0;
        for (size_t i = 0; i < output_n; ++i) simple[i] = sample_linear(input, i * step);
        map.push_back({0, 0});
        return simple;
    }

    const size_t window = std::min(choose_window(sample_rate), std::max<size_t>(256, input.size() / 2));
    const size_t hop = std::max<size_t>(64, window / 4);
    const size_t overlap = window - hop;
    const double analysis_hop = static_cast<double>(hop) * static_cast<double>(input.size()) / static_cast<double>(output_n);
    const size_t search = std::clamp<size_t>(static_cast<size_t>(std::llround(sample_rate * 0.008)), 24, window / 5);

    std::vector<double> acc(output_n, 0.0), weights(output_n, 0.0);
    size_t out_pos = 0;
    size_t in_pos = 0;
    bool first = true;
    while (out_pos < output_n) {
        if (!first) {
            const double expected_d = static_cast<double>(in_pos) + analysis_hop;
            const size_t expected = static_cast<size_t>(std::clamp<double>(expected_d, 0.0,
                static_cast<double>(input.size() > window ? input.size() - window : 0)));
            const size_t lo = expected > search ? expected - search : 0;
            const size_t hi = std::min(input.size() > window ? input.size() - window : 0, expected + search);
            double best_score = -std::numeric_limits<double>::infinity();
            size_t best = expected;
            const size_t compare_n = std::min(overlap, output_n - out_pos);
            const size_t stride = 4;
            for (size_t candidate = lo; candidate <= hi; candidate += 2) {
                double dot = 0.0, aa = 0.0, bb = 0.0;
                for (size_t j = 0; j < compare_n; j += stride) {
                    const size_t oi = out_pos + j;
                    if (weights[oi] <= 1.0e-10) continue;
                    const double a = acc[oi] / weights[oi];
                    const double b = input[std::min(candidate + j, input.size() - 1)];
                    dot += a * b; aa += a * a; bb += b * b;
                }
                const double score = dot / std::sqrt(std::max(aa * bb, 1.0e-20));
                if (score > best_score) { best_score = score; best = candidate; }
            }
            in_pos = best;
        } else {
            in_pos = 0;
            first = false;
        }
        map.push_back({out_pos, in_pos});
        const size_t count = std::min(window, output_n - out_pos);
        for (size_t j = 0; j < count; ++j) {
            const size_t oi = out_pos + j;
            const size_t ii = std::min(in_pos + j, input.size() - 1);
            const double w = 0.5 - 0.5 * std::cos(2.0 * kPi * (static_cast<double>(j) + 0.5) / static_cast<double>(window));
            acc[oi] += static_cast<double>(input[ii]) * w;
            weights[oi] += w;
        }
        if (out_pos + hop >= output_n) break;
        out_pos += hop;
        if (map.size() > output_n / hop + 4) break;
    }
    std::vector<float> out(output_n, 0.0f);
    for (size_t i = 0; i < output_n; ++i) {
        out[i] = weights[i] > 1.0e-10 ? static_cast<float>(acc[i] / weights[i]) : 0.0f;
    }
    // Avoid a tiny zero-valued first/last sample caused by finite overlap-add windows.
    const size_t edge = std::min<size_t>(sample_rate / 500, output_n / 8); // 2 ms
    for (size_t i = 0; i < edge; ++i) {
        const double fade = static_cast<double>(i + 1) / static_cast<double>(edge + 1);
        out[i] = static_cast<float>(out[i] * fade);
        out[output_n - 1 - i] = static_cast<float>(out[output_n - 1 - i] * fade);
    }
    return out;
}

void fft(std::vector<std::complex<double>>& a, bool inverse) {
    const size_t n = a.size();
    for (size_t i = 1, j = 0; i < n; ++i) {
        size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(a[i], a[j]);
    }
    for (size_t len = 2; len <= n; len <<= 1) {
        const double ang = 2.0 * kPi / static_cast<double>(len) * (inverse ? 1.0 : -1.0);
        const std::complex<double> wlen(std::cos(ang), std::sin(ang));
        for (size_t i = 0; i < n; i += len) {
            std::complex<double> w(1.0, 0.0);
            for (size_t j = 0; j < len / 2; ++j) {
                const auto u = a[i + j];
                const auto v = a[i + j + len / 2] * w;
                a[i + j] = u + v;
                a[i + j + len / 2] = u - v;
                w *= wlen;
            }
        }
    }
    if (inverse) for (auto& v : a) v /= static_cast<double>(n);
}

std::vector<double> smooth_envelope(const std::vector<std::complex<double>>& spectrum) {
    const size_t n = spectrum.size() / 2 + 1;
    std::vector<double> mags(n), prefix(n + 1, 0.0), env(n, 0.0);
    for (size_t i = 0; i < n; ++i) {
        mags[i] = std::abs(spectrum[i]);
        prefix[i + 1] = prefix[i] + mags[i];
    }
    for (size_t k = 0; k < n; ++k) {
        const size_t radius = std::clamp<size_t>(static_cast<size_t>(std::llround(k * 0.075)), 3, 36);
        const size_t lo = k > radius ? k - radius : 0;
        const size_t hi = std::min(n - 1, k + radius);
        env[k] = (prefix[hi + 1] - prefix[lo]) / static_cast<double>(hi - lo + 1);
    }
    return env;
}

double mapped_input_position(const std::vector<FrameMap>& map, double out_pos,
                            size_t input_n, size_t output_n) {
    if (map.empty()) return output_n > 0 ? out_pos * input_n / output_n : 0.0;
    if (map.size() == 1 || out_pos <= map.front().out_start) {
        return static_cast<double>(map.front().in_start) + out_pos * input_n / std::max<size_t>(1, output_n);
    }
    auto it = std::upper_bound(map.begin(), map.end(), static_cast<size_t>(out_pos),
        [](size_t v, const FrameMap& m) { return v < m.out_start; });
    if (it == map.end()) {
        const auto& last = map.back();
        return std::min<double>(input_n - 1, last.in_start + (out_pos - last.out_start) * input_n / std::max<size_t>(1, output_n));
    }
    const auto& b = *it;
    const auto& a = *(it - 1);
    const double t = (out_pos - a.out_start) / std::max<double>(1.0, static_cast<double>(b.out_start - a.out_start));
    return (1.0 - t) * a.in_start + t * b.in_start;
}

void stabilize_spectral_envelope(const float* original, size_t original_n,
                                 std::vector<float>& output, uint32_t sample_rate,
                                 double pitch_ratio, double strength,
                                 const std::vector<FrameMap>& map, size_t intermediate_n) {
    if (strength <= 0.001 || original_n < 512 || output.size() < 512) return;
    const size_t window = std::min<size_t>(choose_window(sample_rate), 2048);
    const size_t hop = std::max<size_t>(128, window / 4);
    const size_t bins = window / 2 + 1;
    std::vector<double> acc(output.size(), 0.0), weights(output.size(), 0.0);
    std::vector<std::complex<double>> out_fft(window), src_fft(window);
    std::vector<double> hann(window);
    for (size_t i = 0; i < window; ++i) hann[i] = 0.5 - 0.5 * std::cos(2.0 * kPi * (static_cast<double>(i) + 0.5) / window);

    // Centered STFT frames. Reference frames follow the WSOLA time map back into
    // the original sample, so envelope correction does not depend on note length.
    for (int64_t out_start = -static_cast<int64_t>(window / 2);
         out_start < static_cast<int64_t>(output.size()); out_start += static_cast<int64_t>(hop)) {
        const double out_center = static_cast<double>(out_start) + window / 2.0;
        const double intermediate_center = mapped_input_position(map, out_center, intermediate_n, output.size());
        const double source_center = intermediate_center * pitch_ratio;
        const double source_start = source_center - window / 2.0;
        for (size_t j = 0; j < window; ++j) {
            const int64_t oi = out_start + static_cast<int64_t>(j);
            const double out_sample = oi < 0 ? output.front() : oi >= static_cast<int64_t>(output.size()) ? output.back() : output[static_cast<size_t>(oi)];
            out_fft[j] = std::complex<double>(out_sample * hann[j], 0.0);
            src_fft[j] = std::complex<double>(sample_linear_raw(original, original_n, source_start + j) * hann[j], 0.0);
        }
        fft(out_fft, false);
        fft(src_fft, false);
        const auto src_env = smooth_envelope(src_fft);
        const auto out_env = smooth_envelope(out_fft);
        for (size_t k = 0; k < bins; ++k) {
            double gain = std::sqrt((src_env[k] + 1.0e-7) / (out_env[k] + 1.0e-7));
            gain = std::clamp(gain, 0.60, 1.65);
            gain = std::pow(gain, strength);
            out_fft[k] *= gain;
            if (k > 0 && k < window / 2) out_fft[window - k] *= gain;
        }
        fft(out_fft, true);
        for (size_t j = 0; j < window; ++j) {
            const int64_t oi = out_start + static_cast<int64_t>(j);
            if (oi < 0 || oi >= static_cast<int64_t>(output.size())) continue;
            acc[static_cast<size_t>(oi)] += out_fft[j].real() * hann[j];
            weights[static_cast<size_t>(oi)] += hann[j] * hann[j];
        }
    }
    for (size_t i = 0; i < output.size(); ++i) {
        if (weights[i] > 1.0e-8) output[i] = static_cast<float>(acc[i] / weights[i]);
    }
}

} // namespace

extern "C" KW_DSP_API int32_t KW_DSP_CALL kw_dsp_abi_version(void) { return 0x00010000; }
extern "C" KW_DSP_API const char* KW_DSP_CALL kw_dsp_last_error(void) { return g_last_error.c_str(); }

extern "C" KW_DSP_API int32_t KW_DSP_CALL kw_dsp_process_mono_f32(
    const float* input, uint64_t input_frames, float* output, uint64_t output_frames,
    uint32_t sample_rate, float pitch_semitones, float formant_strength) {
    g_last_error.clear();
    if (output == nullptr || (input == nullptr && input_frames > 0)) {
        g_last_error = "input/output pointer is null"; return -1;
    }
    if (output_frames > kMaxFrames || input_frames > kMaxFrames) {
        g_last_error = "frame count exceeds safety limit"; return -2;
    }
    if (sample_rate < 8000 || sample_rate > 192000) {
        g_last_error = "sample rate must be between 8000 and 192000 Hz"; return -3;
    }
    if (output_frames == 0) return 0;
    if (input_frames == 0) { std::fill(output, output + output_frames, 0.0f); return 0; }
    if (!std::isfinite(pitch_semitones) || !std::isfinite(formant_strength)) {
        g_last_error = "non-finite processing parameter"; return -4;
    }
    pitch_semitones = std::clamp(pitch_semitones, -36.0f, 36.0f);
    formant_strength = std::clamp(formant_strength, 0.0f, 1.0f);
    try {
        const size_t in_n = static_cast<size_t>(input_frames);
        const size_t out_n = static_cast<size_t>(output_frames);
        std::vector<float> sanitized(in_n);
        for (size_t i = 0; i < in_n; ++i) sanitized[i] = static_cast<float>(finite_or_zero(input[i]));
        const double ratio = std::pow(2.0, static_cast<double>(pitch_semitones) / 12.0);
        auto shifted = pitch_resample(sanitized.data(), sanitized.size(), pitch_semitones);
        std::vector<FrameMap> map;
        map.reserve(out_n / std::max<size_t>(1, choose_window(sample_rate) / 4) + 8);
        auto stretched = stretch_wsola(shifted, out_n, sample_rate, map);
        stabilize_spectral_envelope(sanitized.data(), sanitized.size(), stretched,
            sample_rate, ratio, formant_strength, map, shifted.size());
        // Finite output guard and a very gentle overload guard. This is not
        // loudness normalization; it only prevents pathological overlap peaks.
        for (size_t i = 0; i < out_n; ++i) {
            float v = stretched[i];
            if (!std::isfinite(v)) v = 0.0f;
            if (v > 1.25f) v = 1.25f + 0.05f * std::tanh((v - 1.25f) / 0.05f);
            if (v < -1.25f) v = -1.25f - 0.05f * std::tanh((-v - 1.25f) / 0.05f);
            output[i] = v;
        }
        return 0;
    } catch (const std::bad_alloc&) {
        g_last_error = "out of memory while processing audio"; return -5;
    } catch (const std::exception& ex) {
        g_last_error = ex.what(); return -6;
    } catch (...) {
        g_last_error = "unknown native DSP error"; return -7;
    }
}
