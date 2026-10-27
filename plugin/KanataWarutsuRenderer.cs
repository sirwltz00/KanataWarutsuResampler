using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace KanataWarutsu.Resampler;

/// <summary>
/// OpenUtau's managed renderer bridge. The render API is the host contract; the intensive
/// resampling algorithms live in the portable C++ C-ABI library embedded into this assembly.
/// Compile this project against the exact OpenUtau Mobile Core revision used by the app.
/// </summary>
public sealed class KanataWarutsuRenderer : IRenderer {
    private const int OutputSampleRate = 44100;
    private const float FormantStabilization = 0.32f;
    private const string NativeLibraryName = "KanataWarutsuDsp";
    private static readonly object NativeLoadLock = new();
    private static IntPtr nativeHandle;
    private static bool resolverInstalled;

    public USingerType SingerType => USingerType.Classic;
    public bool SupportsRenderPitch => false;
    public bool SupportsRealCurve => false;

    public KanataWarutsuRenderer() {
        InstallNativeResolver();
    }

    public bool SupportsExpression(UExpressionDescriptor descriptor) => false;

    public RenderResult Layout(RenderPhrase phrase) {
        double leading = Math.Max(0, Number(phrase, "leadingMs", 0));
        double position = Number(phrase, "positionMs", 0);
        double duration = Math.Max(0, Number(phrase, "durationMs", 0));
        return CreateHostResult<RenderResult>(Array.Empty<float>(), leading, position, leading + duration);
    }

    public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) => default;

    public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings)
        => Array.Empty<UExpressionDescriptor>();

    public Task<RenderResult> Render(
        RenderPhrase phrase,
        Progress progress,
        int trackNo,
        CancellationTokenSource cancellation,
        bool isPreRender = false,
        RenderPhraseEvents? renderEvents = null) {
        cancellation.ThrowIfCancellationRequested();
        InstallNativeResolver();

        double phrasePositionMs = Number(phrase, "positionMs", 0);
        double leadingMs = Math.Max(0, Number(phrase, "leadingMs", 0));
        double durationMs = Math.Max(0, Number(phrase, "durationMs", 0));
        double outputMs = leadingMs + durationMs;
        int outputFrames = MillisecondsToFrames(outputMs);
        if (outputFrames <= 0) {
            var empty = CreateHostResult<RenderResult>(Array.Empty<float>(), leadingMs, phrasePositionMs, outputMs);
            CompleteProgress(progress, 0, "Kanata Warutsu: empty phrase");
            return Task.FromResult(empty);
        }

        float[] mix = new float[outputFrames];
        object? phonesValue = Member(phrase, "phones");
        if (phonesValue is not IEnumerable phones) {
            var silent = CreateHostResult<RenderResult>(mix, leadingMs, phrasePositionMs, outputMs);
            CompleteProgress(progress, 0, "Kanata Warutsu: no phonemes");
            return Task.FromResult(silent);
        }

        int phoneCount = phonesValue is ICollection collection ? collection.Count : -1;
        int processed = 0;
        int missingSamples = 0;
        foreach (object? phone in phones) {
            cancellation.ThrowIfCancellationRequested();
            if (phone is null) continue;
            processed++;
            try {
                RenderPhoneIntoMix(phrase, phone, mix, phrasePositionMs, leadingMs, cancellation);
            } catch (FileNotFoundException) {
                // Keep rendering valid phones, but fail clearly if the singer/OTO mapping is
                // entirely unresolved rather than silently returning a whole phrase of silence.
                missingSamples++;
            }
            ReportProgress(progress, processed, phoneCount, "Kanata Warutsu: rendering phonemes");
        }
        if (processed > 0 && missingSamples == processed) {
            throw new FileNotFoundException("Kanata Warutsu Resampler could not resolve any OTO source files. Check the singer's oto.ini and sample paths.");
        }

        // Keep finite floating-point audio while allowing modest overlap headroom. A hard
        // +/-1 clip here would cause distortion before OpenUtau's downstream mix stage.
        for (int i = 0; i < mix.Length; i++) {
            if (!float.IsFinite(mix[i])) mix[i] = 0;
            mix[i] = Math.Clamp(mix[i], -4.0f, 4.0f);
        }

        CompleteProgress(progress, processed, "Kanata Warutsu: render complete");
        return Task.FromResult(CreateHostResult<RenderResult>(mix, leadingMs, phrasePositionMs, outputMs));
    }

    private static void RenderPhoneIntoMix(
        RenderPhrase phrase,
        object phone,
        float[] mix,
        double phrasePositionMs,
        double phraseLeadingMs,
        CancellationTokenSource cancellation) {
        object? oto = Member(phone, "oto");
        if (oto is null) return;

        string samplePath = ResolveSamplePath(phrase, oto);
        if (!File.Exists(samplePath)) throw new FileNotFoundException("Kanata Warutsu OTO sample was not found.", samplePath);

        cancellation.ThrowIfCancellationRequested();
        WaveData wave = WaveData.Read(samplePath);
        cancellation.ThrowIfCancellationRequested();
        float[] source = wave.SampleRate == OutputSampleRate
            ? wave.MonoSamples
            : ResampleLinear(wave.MonoSamples, wave.SampleRate, OutputSampleRate);
        if (source.Length == 0) return;

        double offsetMs = Number(oto, "Offset", Number(oto, "offset", 0));
        double consonantMs = Number(oto, "Consonant", Number(oto, "consonant", offsetMs));
        double cutoffMs = Number(oto, "Cutoff", Number(oto, "cutoff", 0));
        int sourceStart = Math.Clamp(MillisecondsToFrames(offsetMs), 0, source.Length - 1);
        int sourceEnd = source.Length;
        if (cutoffMs < 0) sourceEnd = Math.Clamp(source.Length + MillisecondsToFrames(cutoffMs), sourceStart + 1, source.Length);
        else if (cutoffMs > 0) sourceEnd = Math.Clamp(MillisecondsToFrames(cutoffMs), sourceStart + 1, source.Length);
        if (sourceEnd <= sourceStart) return;

        int consonantEnd = Math.Clamp(MillisecondsToFrames(consonantMs), sourceStart, sourceEnd);
        // If OTO data is degenerate (common for simple vowel samples), use a short protected
        // onset rather than treating the whole recorded vowel as a consonant.
        if (consonantEnd <= sourceStart) consonantEnd = Math.Min(sourceEnd, sourceStart + Math.Min(128, Math.Max(1, OutputSampleRate / 100)));
        int consonantSourceFrames = Math.Max(0, consonantEnd - sourceStart);
        int vowelStart = consonantEnd;
        int vowelSourceFrames = Math.Max(0, sourceEnd - vowelStart);

        double preutterMs = Math.Clamp(Number(phone, "preutterMs", Number(oto, "Preutter", Number(oto, "preutter", 0))), 0, 2000);
        double phoneDurationMs = Math.Clamp(Number(phone, "durationMs", 0), 0, 60000);
        int consonantOutputFrames = Math.Min(MillisecondsToFrames(preutterMs), MillisecondsToFrames(preutterMs + phoneDurationMs));
        int vowelOutputFrames = Math.Max(0, MillisecondsToFrames(phoneDurationMs));
        int destTotalFrames = consonantOutputFrames + vowelOutputFrames;
        if (destTotalFrames <= 0) return;

        float[] destConsonant = new float[consonantOutputFrames];
        if (consonantOutputFrames > 0 && consonantSourceFrames > 0) {
            float[] consonantInput = Slice(source, sourceStart, consonantSourceFrames);
            if (ProcessNative(consonantInput, destConsonant, 0, 0) != 0) {
                StretchLinear(consonantInput, destConsonant);
            }
        }

        float[] destVowel = new float[vowelOutputFrames];
        if (vowelOutputFrames > 0 && vowelSourceFrames > 0) {
            cancellation.ThrowIfCancellationRequested();
            float[] vowelInput = Slice(source, vowelStart, vowelSourceFrames);
            double tone = Number(phone, "tone", 60);
            double otoTone = Number(oto, "Tone", Number(oto, "tone", tone));
            double pitchSemitones = AveragePitchSemitones(phrase, phone, tone) + (tone - otoTone);
            pitchSemitones = Math.Clamp(pitchSemitones, -36.0, 36.0);
            int status = ProcessNative(vowelInput, destVowel, (float)pitchSemitones, FormantStabilization);
            if (status != 0) StretchLinear(vowelInput, destVowel);
        }

        double phonePositionMs = Number(phone, "positionMs", phrasePositionMs);
        int destStart = MillisecondsToFrames(phonePositionMs - phrasePositionMs + phraseLeadingMs - preutterMs);
        float volume = (float)Math.Clamp(Number(phone, "volume", 1), 0, 4);
        double velocityRaw = Number(phone, "velocity", 100);
        float velocity = (float)(velocityRaw > 2.0 ? Math.Clamp(velocityRaw / 100.0, 0, 2.0) : Math.Clamp(velocityRaw, 0, 2.0));
        float gain = volume * velocity;
        double durationTotalMs = preutterMs + phoneDurationMs;

        for (int i = 0; i < destConsonant.Length; i++) {
            int dst = destStart + i;
            if ((uint)dst >= (uint)mix.Length) continue;
            double localMs = i * 1000.0 / OutputSampleRate;
            float envelope = EnvelopeGain(phone, localMs, durationTotalMs);
            mix[dst] += destConsonant[i] * gain * envelope;
        }
        for (int i = 0; i < destVowel.Length; i++) {
            int dst = destStart + destConsonant.Length + i;
            if ((uint)dst >= (uint)mix.Length) continue;
            double localMs = (destConsonant.Length + i) * 1000.0 / OutputSampleRate;
            float envelope = EnvelopeGain(phone, localMs, durationTotalMs);
            mix[dst] += destVowel[i] * gain * envelope;
        }
    }

    private static double AveragePitchSemitones(object phrase, object phone, double baseTone) {
        if (Member(phrase, "pitches") is not Array pitches || pitches.Length == 0) return 0;
        double startMs = Number(phone, "positionMs", Number(phrase, "positionMs", 0));
        double endMs = Number(phone, "endMs", startMs + Number(phone, "durationMs", 0));
        double phraseMs = Number(phrase, "positionMs", 0);
        int a = Math.Clamp((int)Math.Floor((startMs - phraseMs) / 5.0), 0, pitches.Length - 1);
        int b = Math.Clamp((int)Math.Ceiling((endMs - phraseMs) / 5.0), a, pitches.Length - 1);
        double sum = 0;
        int count = 0;
        for (int i = a; i <= b; i++) {
            if (pitches.GetValue(i) is not IConvertible v) continue;
            double n;
            try { n = v.ToDouble(System.Globalization.CultureInfo.InvariantCulture); }
            catch { continue; }
            if (!double.IsFinite(n)) continue;
            sum += n;
            count++;
        }
        if (count == 0) return 0;
        double average = sum / count;
        // OpenUtau phrase pitch arrays are generally cents. Accept either absolute cents
        // (MIDI note * 100) or relative cents depending on the range around the note pitch.
        if (Math.Abs(average) <= 2400.0) return average / 100.0;
        if (average > 0 && average < 12700.0) return average / 100.0 - baseTone;
        return 0;
    }

    private static float EnvelopeGain(object phone, double localMs, double totalMs) {
        if (Member(phone, "envelope") is Array points && points.Length >= 2) {
            double x0 = 0, y0 = 1;
            for (int i = 0; i < points.Length; i++) {
                object? point = points.GetValue(i);
                if (point is null) continue;
                double x1 = Number(point, "X", Number(point, "x", 0));
                double y1 = Number(point, "Y", Number(point, "y", 1));
                if (i == 0) { x0 = x1; y0 = y1; continue; }
                if (localMs <= x1) {
                    double width = x1 - x0;
                    double t = width > 1e-6 ? Math.Clamp((localMs - x0) / width, 0, 1) : 1;
                    double y = y0 + (y1 - y0) * t;
                    if (y > 2) y /= 100.0;
                    return (float)Math.Clamp(y, 0, 2);
                }
                x0 = x1; y0 = y1;
            }
            if (y0 > 2) y0 /= 100.0;
            return (float)Math.Clamp(y0, 0, 2);
        }

        // Minimal click protection when the host doesn't supply a volume envelope.
        double attack = Math.Min(1.0, localMs / 4.0);
        double tailMs = Math.Max(0, totalMs - localMs);
        double release = Math.Min(1.0, tailMs / 5.0);
        return (float)Math.Clamp(Math.Min(attack, release), 0, 1);
    }

    private static string ResolveSamplePath(RenderPhrase phrase, object oto) {
        object? singer = Member(phrase, "singer");
        string sample = StringMember(oto, "File", "file", "FilePath", "filePath", "WavPath", "wavPath", "Sample", "sample", "Path", "path");
        if (singer is not null) {
            foreach (MethodInfo method in singer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (!method.Name.Contains("GetSampleFile", StringComparison.OrdinalIgnoreCase)) continue;
                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length != 1) continue;
                object? argument = ps[0].ParameterType.IsInstanceOfType(oto)
                    ? oto
                    : ps[0].ParameterType == typeof(string) && !string.IsNullOrWhiteSpace(sample) ? sample : null;
                if (argument is null) continue;
                try {
                    if (method.Invoke(singer, new[] { argument }) is string found && !string.IsNullOrWhiteSpace(found) && File.Exists(found)) return Path.GetFullPath(found);
                } catch { /* Try the OTO's file field below. */ }
            }
        }

        if (string.IsNullOrWhiteSpace(sample)) throw new FileNotFoundException("The host OTO does not expose a sample filename.");
        if (Path.IsPathRooted(sample) && File.Exists(sample)) return Path.GetFullPath(sample);

        string root = StringMember(singer, "Location", "location", "BasePath", "basePath", "Path", "path", "Folder", "folder", "Directory", "directory");
        if (!string.IsNullOrWhiteSpace(root)) {
            string joined = Path.GetFullPath(Path.Combine(root, sample));
            if (File.Exists(joined)) return joined;
            string raw = Path.GetFullPath(Path.Combine(root, "raw", sample));
            if (File.Exists(raw)) return raw;
        }
        string relative = Path.GetFullPath(sample);
        if (File.Exists(relative)) return relative;
        throw new FileNotFoundException($"Could not resolve OTO sample '{sample}'.", relative);
    }

    private static int ProcessNative(float[] input, float[] output, float pitch, float formant) {
        if (output.Length == 0) return 0;
        if (input.Length == 0) { Array.Clear(output, 0, output.Length); return 0; }
        EnsureNativeReady();
        return NativeProcess(input, (ulong)input.Length, output, (ulong)output.Length,
            OutputSampleRate, pitch, formant);
    }

    [DllImport(NativeLibraryName, EntryPoint = "kw_dsp_process_mono_f32", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeProcess(
        [In] float[] input, ulong inputFrames,
        [Out] float[] output, ulong outputFrames,
        uint sampleRate, float pitchSemitones, float formantStrength);

    [DllImport(NativeLibraryName, EntryPoint = "kw_dsp_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeAbiVersion();

    private static void InstallNativeResolver() {
        lock (NativeLoadLock) {
            if (resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(KanataWarutsuRenderer).Assembly, ResolveNativeLibrary);
            resolverInstalled = true;
        }
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, NativeLibraryName, StringComparison.Ordinal)) return IntPtr.Zero;
        lock (NativeLoadLock) {
            if (nativeHandle != IntPtr.Zero) return nativeHandle;
            (string resourceName, string fileName) = NativeResourceForCurrentPlatform();
            using Stream? resource = assembly.GetManifestResourceStream(resourceName);
            if (resource is null) {
                throw new DllNotFoundException($"The plugin has no embedded backend '{resourceName}'. Build the official GitHub Actions artifact for the target platform.");
            }
            string directory = Path.Combine(Path.GetTempPath(), "KanataWarutsuResampler", "1.0.0");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) resource.CopyTo(output);
            nativeHandle = NativeLibrary.Load(path);
            int abi = NativeAbiVersion();
            if (abi != 0x00010000) {
                NativeLibrary.Free(nativeHandle);
                nativeHandle = IntPtr.Zero;
                throw new BadImageFormatException($"Kanata Warutsu DSP ABI mismatch. Expected 0x00010000, received 0x{abi:X8}.");
            }
            return nativeHandle;
        }
    }

    private static (string Resource, string FileName) NativeResourceForCurrentPlatform() {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return ("KanataWarutsu.Native.win-x64.bin", "KanataWarutsuDsp.dll");
        if (OperatingSystem.IsAndroid() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return ("KanataWarutsu.Native.android-arm64.bin", "libKanataWarutsuDsp.so");
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return ("KanataWarutsu.Native.linux-x64.bin", "libKanataWarutsuDsp.so");
        throw new PlatformNotSupportedException($"Kanata Warutsu Resampler has no native backend for {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}). Android currently targets arm64-v8a.");
    }

    private static void EnsureNativeReady() {
        if (nativeHandle == IntPtr.Zero) {
            IntPtr h = ResolveNativeLibrary(NativeLibraryName, typeof(KanataWarutsuRenderer).Assembly, null);
            if (h == IntPtr.Zero) throw new DllNotFoundException("Kanata Warutsu DSP backend could not be loaded.");
        }
    }

    private static float[] Slice(float[] src, int start, int count) {
        start = Math.Clamp(start, 0, src.Length);
        count = Math.Clamp(count, 0, src.Length - start);
        var result = new float[count];
        Array.Copy(src, start, result, 0, count);
        return result;
    }

    private static void StretchLinear(float[] input, float[] output) {
        if (output.Length == 0) return;
        if (input.Length == 0) { Array.Clear(output, 0, output.Length); return; }
        if (output.Length == 1) { output[0] = input[0]; return; }
        double scale = (input.Length - 1.0) / Math.Max(1, output.Length - 1);
        for (int i = 0; i < output.Length; i++) {
            double p = i * scale;
            int a = (int)p;
            int b = Math.Min(a + 1, input.Length - 1);
            double t = p - a;
            output[i] = (float)(input[a] * (1 - t) + input[b] * t);
        }
    }

    private static float[] ResampleLinear(float[] input, int sourceRate, int targetRate) {
        if (input.Length == 0 || sourceRate <= 0 || targetRate <= 0) return Array.Empty<float>();
        int n = Math.Max(1, (int)Math.Round(input.Length * (double)targetRate / sourceRate));
        var output = new float[n];
        double ratio = (double)sourceRate / targetRate;
        for (int i = 0; i < output.Length; i++) {
            double p = i * ratio;
            int a = Math.Min((int)p, input.Length - 1);
            int b = Math.Min(a + 1, input.Length - 1);
            double t = p - a;
            output[i] = (float)(input[a] * (1 - t) + input[b] * t);
        }
        return output;
    }

    private static int MillisecondsToFrames(double ms) {
        if (!double.IsFinite(ms)) return 0;
        double frames = Math.Round(ms * OutputSampleRate / 1000.0);
        return (int)Math.Clamp(frames, int.MinValue, int.MaxValue);
    }

    private static object? Member(object? target, string name) {
        if (target is null) return null;
        Type type = target.GetType();
        for (Type? t = type; t is not null; t = t.BaseType) {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            PropertyInfo? property = t.GetProperties(flags).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == 0);
            if (property is not null) {
                try { return property.GetValue(target); } catch { }
            }
            FieldInfo? field = t.GetFields(flags).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is not null) {
                try { return field.GetValue(target); } catch { }
            }
        }
        return null;
    }

    private static string StringMember(object? target, params string[] names) {
        foreach (string name in names) {
            object? value = Member(target, name);
            if (value is string s && !string.IsNullOrWhiteSpace(s)) return s;
        }
        return string.Empty;
    }

    private static double Number(object? target, string name, double fallback) {
        object? value = Member(target, name);
        if (value is null) return fallback;
        try {
            double number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? number : fallback;
        } catch { return fallback; }
    }

    private static RenderResult CreateHostResult<T>(float[] samples, double leadingMs, double positionMs, double estimatedLengthMs) {
        object instance = CreateHostValue(typeof(T), samples, leadingMs, positionMs, estimatedLengthMs);
        return (RenderResult)instance;
    }

    private static object CreateHostValue(Type type, float[] samples, double leadingMs, double positionMs, double estimatedLengthMs) {
        object? instance = null;
        foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderByDescending(c => c.GetParameters().Length)) {
            ParameterInfo[] parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) {
                string name = parameters[i].Name?.ToLowerInvariant() ?? string.Empty;
                if (name.Contains("sample")) args[i] = ConvertForType(samples, parameters[i].ParameterType);
                else if (name.Contains("leading") || name.Contains("head")) args[i] = ConvertForType(leadingMs, parameters[i].ParameterType);
                else if (name.Contains("position") || name.Contains("start")) args[i] = ConvertForType(positionMs, parameters[i].ParameterType);
                else if (name.Contains("estimate") || name.Contains("length") || name.Contains("duration")) args[i] = ConvertForType(estimatedLengthMs, parameters[i].ParameterType);
                else if (name.Contains("samplerate") || name.Contains("sample_rate") || name.Contains("frequency")) args[i] = ConvertForType(OutputSampleRate, parameters[i].ParameterType);
                else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
                else args[i] = parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null;
            }
            try { instance = ctor.Invoke(args); break; } catch { }
        }
        if (instance is null) {
            try { instance = Activator.CreateInstance(type, nonPublic: true); }
            catch { throw new InvalidOperationException($"Cannot construct OpenUtau host result type {type.FullName}. Its API may have changed; rebuild against the current Mobile Core."); }
        }

        SetMember(instance, "samples", samples);
        SetMember(instance, "leadingMs", leadingMs);
        SetMember(instance, "positionMs", positionMs);
        SetMember(instance, "estimatedLengthMs", estimatedLengthMs);
        SetMember(instance, "durationMs", estimatedLengthMs);
        SetMember(instance, "sampleRate", OutputSampleRate);
        return instance;
    }

    private static object? ConvertForType(object value, Type target) {
        try {
            if (target.IsInstanceOfType(value)) return value;
            if (target == typeof(float[])) return value is float[] fa ? fa : Array.Empty<float>();
            return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        } catch { return target.IsValueType ? Activator.CreateInstance(target) : null; }
    }

    private static void SetMember(object target, string name, object value) {
        for (Type? t = target.GetType(); t is not null; t = t.BaseType) {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            PropertyInfo? p = t.GetProperties(flags).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetIndexParameters().Length == 0 && x.SetMethod is not null);
            if (p is not null) {
                try { p.SetValue(target, ConvertForType(value, p.PropertyType)); return; } catch { }
            }
            FieldInfo? f = t.GetFields(flags).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !x.IsInitOnly);
            if (f is not null) {
                try { f.SetValue(target, ConvertForType(value, f.FieldType)); return; } catch { }
            }
        }
    }

    private static void ReportProgress(Progress progress, int completed, int total, string message) {
        if (progress is null) return;
        foreach (MethodInfo method in progress.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (!string.Equals(method.Name, "Report", StringComparison.OrdinalIgnoreCase)) continue;
            ParameterInfo[] p = method.GetParameters();
            try {
                if (p.Length == 1 && p[0].ParameterType == typeof(double)) method.Invoke(progress, new object[] { total > 0 ? completed / (double)total : 1.0 });
                else if (p.Length == 1 && p[0].ParameterType == typeof(float)) method.Invoke(progress, new object[] { total > 0 ? completed / (float)total : 1.0f });
            } catch { }
            break;
        }
        _ = message;
    }

    private static void CompleteProgress(Progress progress, int completed, string message) {
        if (progress is null) return;
        foreach (MethodInfo method in progress.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (!string.Equals(method.Name, "Complete", StringComparison.OrdinalIgnoreCase)) continue;
            ParameterInfo[] p = method.GetParameters();
            try {
                if (p.Length == 2) method.Invoke(progress, new object?[] { completed, message });
                else if (p.Length == 1 && p[0].ParameterType == typeof(string)) method.Invoke(progress, new object?[] { message });
                else if (p.Length == 0) method.Invoke(progress, Array.Empty<object>());
            } catch { }
            break;
        }
    }

    private sealed class WaveData {
        public int SampleRate { get; private init; }
        public float[] MonoSamples { get; private init; } = Array.Empty<float>();

        public static WaveData Read(string path) {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new InvalidDataException($"Not a supported RIFF/WAVE sample: {path}");
            int format = 0, channels = 0, sampleRate = 0, bits = 0, dataOffset = -1, dataSize = 0;
            int cursor = 12;
            while (cursor + 8 <= bytes.Length) {
                string id = Encoding.ASCII.GetString(bytes, cursor, 4);
                uint chunkSizeU = BitConverter.ToUInt32(bytes, cursor + 4);
                if (chunkSizeU > int.MaxValue) throw new InvalidDataException("WAV chunk is too large.");
                int chunkSize = (int)chunkSizeU;
                int start = cursor + 8;
                if (start < 0 || chunkSize < 0 || start > bytes.Length - chunkSize) break;
                if (id == "fmt " && chunkSize >= 16) {
                    format = BitConverter.ToUInt16(bytes, start);
                    channels = BitConverter.ToUInt16(bytes, start + 2);
                    sampleRate = (int)BitConverter.ToUInt32(bytes, start + 4);
                    bits = BitConverter.ToUInt16(bytes, start + 14);
                    if (format == 0xfffe && chunkSize >= 40) format = BitConverter.ToUInt16(bytes, start + 24);
                } else if (id == "data") {
                    dataOffset = start;
                    dataSize = chunkSize;
                }
                cursor = start + chunkSize + (chunkSize & 1);
            }
            if (dataOffset < 0 || dataSize <= 0 || channels < 1 || sampleRate < 1 || bits is not (8 or 16 or 24 or 32) || format is not (1 or 3))
                throw new InvalidDataException($"Unsupported WAV format in '{path}' (format={format}, channels={channels}, rate={sampleRate}, bits={bits}).");
            if (format == 3 && bits != 32) throw new InvalidDataException("Only IEEE float32 WAV is supported.");
            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            int frames = dataSize / frameBytes;
            var mono = new float[frames];
            for (int frame = 0; frame < frames; frame++) {
                double sum = 0;
                int frameStart = dataOffset + frame * frameBytes;
                for (int c = 0; c < channels; c++) {
                    int p = frameStart + c * bytesPerSample;
                    float sample = format == 3 ? BitConverter.ToSingle(bytes, p) : bits switch {
                        8 => (bytes[p] - 128) / 128.0f,
                        16 => BitConverter.ToInt16(bytes, p) / 32768.0f,
                        24 => Decode24(bytes, p) / 8388608.0f,
                        32 => BitConverter.ToInt32(bytes, p) / 2147483648.0f,
                        _ => 0.0f
                    };
                    if (!float.IsFinite(sample)) sample = 0;
                    sum += sample;
                }
                mono[frame] = (float)(sum / channels);
            }
            return new WaveData { SampleRate = sampleRate, MonoSamples = mono };
        }

        private static int Decode24(byte[] b, int offset) {
            int value = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16);
            return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
        }
    }
}
