#pragma once
#include <stdint.h>

#if defined(_WIN32)
  #if defined(KW_DSP_BUILD)
    #define KW_DSP_API __declspec(dllexport)
  #else
    #define KW_DSP_API __declspec(dllimport)
  #endif
  #define KW_DSP_CALL __cdecl
#else
  #define KW_DSP_API __attribute__((visibility("default")))
  #define KW_DSP_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Stable C ABI shared by the native DSP backend and the managed OpenUtau bridge. */
KW_DSP_API int32_t KW_DSP_CALL kw_dsp_abi_version(void);
KW_DSP_API const char* KW_DSP_CALL kw_dsp_last_error(void);

/*
 * Resample one mono sample. Output length is caller-selected and exact.
 * The engine separates pitch from duration: band-limited resampling changes
 * pitch, WSOLA restores/sets the requested duration, and a smoothed spectral
 * envelope pass can stabilize formants. Input and output are float32 PCM.
 *
 * Returns 0 on success, a negative status code on failure.
 * pitch_semitones is clamped to +/- 36 semitones. formant_strength is 0..1.
 */
KW_DSP_API int32_t KW_DSP_CALL kw_dsp_process_mono_f32(
    const float* input,
    uint64_t input_frames,
    float* output,
    uint64_t output_frames,
    uint32_t sample_rate,
    float pitch_semitones,
    float formant_strength);

#ifdef __cplusplus
}
#endif
