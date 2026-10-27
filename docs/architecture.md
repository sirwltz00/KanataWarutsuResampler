# Architecture and host contract

## Why this is a managed DLL plus a C++ shared library

The OpenUtau renderer API exposes `IRenderer` as a managed .NET interface with phrase layout, rendering, pitch loading, and expression-capability methods. A raw C++ DLL cannot directly implement that managed interface. This project uses a managed assembly as the host entry point and a compact C ABI for the DSP operations.

The classic UTAU CLI resampler is a separate interface: the host invokes a process with file paths, flags, timing parameters, and a pitch string. This project intentionally does not produce that executable because the target is the managed OpenUtau Mobile renderer plugin workflow.

## Native C ABI

`native/include/kanata_warutsu_dsp.h` declares:

- `kw_dsp_abi_version()` — returns `0x00010000`.
- `kw_dsp_last_error()` — thread-local error message.
- `kw_dsp_process_mono_f32(...)` — mono float32 processing with caller-selected exact output length, sample rate, pitch offset in semitones, and formant stabilization strength.

The API is versioned so a future managed bridge can reject a mismatched native engine instead of calling an incompatible function signature.

## Signal path

1. The bridge reads a source WAV and downmixes channels to mono.
2. OTO offset/cutoff defines the usable recording window. The consonant/onset segment is handled separately, to protect transients.
3. The vowel segment is passed to the C++ engine. A windowed-sinc stage shifts pitch; WSOLA adjusts the duration to the requested frame count; a smoothed short-time spectral-envelope pass optionally compensates for formant movement.
4. The bridge applies the host envelope and volume and mixes the rendered phone into the phrase buffer.

## Known boundaries

The first bridge uses reflection for several host metadata members because OpenUtau Mobile's Core types have changed between revisions. This is a compatibility aid, not a substitute for compiling and testing against the exact mobile Core version. If a host changes the renderer discovery API, result struct layout, sample-path API, or pitch-curve units, update the bridge and rebuild.

The current native tests are algorithmic smoke tests only. They cannot detect bad alignment against a particular voicebank's OTO, subjective artifacts, or platform-specific native-loading restrictions. Those require rendering real Warutsu phrases in the actual OpenUtau Mobile build.
