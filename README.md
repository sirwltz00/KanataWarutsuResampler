# Kanata Warutsu Resampler

A custom resampler / phrase renderer for the UTAU voicebank **奏多ワルツ (Kanata Warutsu)**. The design prioritizes natural, smooth pitch changes, stable formants, and protection of consonant attacks.

## Deliverable model: one host plugin DLL, no EXE

The installable `KanataWarutsuResampler.OpenUtau.dll` is a managed OpenUtau renderer plugin that implements the host's `IRenderer` API. The audio DSP is a C++ shared library with a stable C ABI. CI embeds the native library for each supported runtime as a resource inside the managed plugin, so the release contains **no resampler executable**.

This distinction matters: the classic Windows UTAU resampler contract is an executable command-line interface; it is not a generic native-DLL calling convention. OpenUtau Mobile's managed renderer plugin API is the target for this project. The bridge is compiled against the OpenUtau Mobile Core source revision selected by the workflow.

## Current processing design

- Band-limited windowed-sinc resampling for the pitch-shift stage.
- WSOLA overlap/add duration adjustment to restore the exact requested output frame count.
- Optional smoothed spectral-envelope correction for practical formant stabilization.
- Consonant region is treated separately from the vowel region to reduce transient smearing.
- Input WAV reader supports PCM 8/16/24/32-bit and IEEE float32, including mono downmix and sample-rate conversion to 44.1 kHz.
- The bridge passes the host phrase pitch curve into the DSP stage, applies the phone envelope, and mixes phrase phones with overlap headroom.
- DSP ABI validation, invalid-input guards, finite-sample checks, and a CTest pitch/time-stretch test.

This is an advanced practical DSP baseline, not a claim that the current version matches a mature commercial resampler. The native algorithm tests use synthetic signals. Listen tests using real Warutsu OTO samples and in-app rendering are still required before treating it as production-ready.

## Build with GitHub Actions

1. Create a GitHub repository and upload/push this project to its `main` branch.
2. Enable GitHub Actions for the repository.
3. `Build Kanata Warutsu Resampler (DLL only)` runs for pushes, pull requests, and manual dispatches. The manual workflow accepts an OpenUtau Mobile git ref (default: `dev`).
4. Download the artifact named **`KanataWarutsuResampler-OpenUtau-Mobile-DLL`**. It contains:
   - `KanataWarutsuResampler.OpenUtau.dll` — the plugin/renderer assembly (the only binary intended to be installed into the host's renderer/plugin folder).
   - `README.md` — installation and compatibility notes.
5. Install that DLL using the plugin/renderer installation path supported by your exact OpenUtau Mobile build, restart the app if required, and select **Kanata Warutsu Resampler** as the renderer. Do not install it as a classic command-line `.exe` resampler.
6. To create a GitHub Release, push a version tag such as `v0.1.0`; the workflow attaches the plugin DLL and README.

CI also builds/tests the native DSP on Linux x64, and builds native backends for Windows x64 and Android ARM64. The Windows native job builds the library target only; no `.exe` is distributed.

## Platform scope and limitations

- **Android arm64-v8a:** native backend is produced by CI. Actual OpenUtau Mobile installation/audio-render testing on a device is still required; Android's native-library loading rules and Mobile's plugin-loading policy can vary by release.
- **Windows x64 and Linux x64:** native backends are produced for compatible OpenUtau hosts.
- **iOS:** no native backend is shipped. iOS generally requires native code to be included and signed as part of the app build, so this external plugin package is not claimed to work on iOS.
- **OpenUtau Mobile is actively developed.** The managed bridge must be built against the exact Core revision used by the app. The workflow tries to discover the target framework and reference the matching Core project, but upstream API/layout changes can require updating the bridge/workflow.

## Local native build (optional)

Requirements: CMake 3.20+ and a C++17 compiler.

```sh
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release -DKW_DSP_BUILD_TESTS=ON
cmake --build build --parallel
ctest --test-dir build --output-on-failure
```

The tests verify the native DSP C ABI, invalid-input handling, approximate upward/downward pitch ratios, formant-pass processing, exact output duration, and finite/non-silent output. They do not validate the managed host bridge.

## Repository layout

- `native/` — C++ DSP engine, C ABI, and native tests.
- `plugin/` — managed OpenUtau `IRenderer` bridge; embeds the native backend resources at build time.
- `.github/workflows/build.yml` — cross-platform CI and DLL artifact packaging.
- `docs/` — algorithm and interface design notes.
