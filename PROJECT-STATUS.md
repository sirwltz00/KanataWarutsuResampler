# Build and validation status

## Verified locally on 2026-10-27

- Native C++ DSP shared library builds with CMake and GCC in Release mode.
- CTest: 1/1 tests passed.
- Direct DSP test output: upward pitch test measured 264 Hz for a 220 Hz tone at +3 semitones (target about 261.6 Hz); downward test measured 186 Hz at -3 semitones (target about 185.0 Hz).
- A separate AddressSanitizer + UndefinedBehaviorSanitizer build also passed the native CTest suite.
- The GitHub Actions workflow parses as YAML.

## Not yet verified

- The managed `IRenderer` bridge has not been compiled locally because this environment does not have the .NET SDK installed and cannot fetch the OpenUtau Mobile repository directly.
- The GitHub Actions workflow has not been run; the GitHub account/repository connection has not been authorized in this session.
- The plugin has not yet been installed into a real OpenUtau Mobile build or listened to with Kanata Warutsu's actual OTO samples.

The first hosted workflow run is therefore an important validation gate. If upstream OpenUtau Mobile changed the renderer API, the compile step will fail rather than quietly producing an unverified plugin DLL.
