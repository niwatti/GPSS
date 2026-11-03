using System.Collections.Immutable;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gpss.Core;

namespace Gpss.Application;

/// <summary>Persistent, user-editable preferences. Device selection is deliberately only a hint.</summary>
public sealed record UserSettings
{
    public BackendKind PreferredBackend { get; init; }
    public AcquisitionSettings Acquisition { get; init; }
    public string EstimatorMethodId { get; init; }
    public ImmutableArray<AxisProfile> Profiles { get; init; }
    public string? SelectedDeviceHint { get; init; }
    public AxisMapping? XInputMapping { get; init; }
    public int MeasurementWindowSeconds { get; init; }
    public UserSettings(BackendKind preferredBackend = BackendKind.HidDirect, AcquisitionSettings? acquisition = null,
        string estimatorMethodId = MeanBitStepEstimator.Id, ImmutableArray<AxisProfile> profiles = default, string? selectedDeviceHint = null,
        AxisMapping? xInputMapping = null, int measurementWindowSeconds = MeasurementWindow.DefaultSeconds)
    {
        PreferredBackend = preferredBackend; Acquisition = acquisition ?? new(); EstimatorMethodId = estimatorMethodId;
        Profiles = profiles.IsDefault ? [] : profiles; SelectedDeviceHint = selectedDeviceHint;
        XInputMapping = xInputMapping is null ? null : SettingsStore.NormalizeMapping(xInputMapping);
        MeasurementWindowSeconds = measurementWindowSeconds;
    }
    public static UserSettings Defaults { get; } = new();
}

public sealed record AxisProfile
{
    public DeviceFingerprint Fingerprint { get; init; }
    public AxisMapping Mapping { get; init; }
    public AxisProfile(DeviceFingerprint fingerprint, AxisMapping mapping)
    {
        Fingerprint = fingerprint;
        Mapping = mapping is null ? null! : SettingsStore.NormalizeMapping(mapping);
    }
}

/// <summary>A HID identity requiring exact path and descriptor signature; VID/PID alone is never sufficient.</summary>
public sealed record DeviceFingerprint(string Path, ushort VendorId, ushort ProductId, string DescriptorSignature)
{
    public static DeviceFingerprint? FromDevice(DeviceInfo device)
    {
        if (device.Backend != BackendKind.HidDirect || string.IsNullOrWhiteSpace(device.Path) ||
            device.VendorId is null || device.ProductId is null || device.Axes.IsDefaultOrEmpty || device.Axes.Any(a => a is null)) return null;
        return new(device.Path, device.VendorId.Value, device.ProductId.Value, ComputeDescriptorSignature(device.Axes));
    }

    public bool Matches(DeviceInfo device) => FromDevice(device) is { } current &&
        string.Equals(Path, current.Path, StringComparison.Ordinal) &&
        VendorId == current.VendorId && ProductId == current.ProductId &&
        string.Equals(DescriptorSignature, current.DescriptorSignature, StringComparison.Ordinal);

    private static string ComputeDescriptorSignature(ImmutableArray<AxisDescriptor> axes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var a in axes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                if (a is null) throw new ArgumentException("Device descriptor contains a null axis", nameof(axes));
                writer.WriteStartObject();
                writer.WriteString("key", a.Key); writer.WriteString("name", a.Name);
                writer.WriteNumber("logicalMin", a.LogicalMin); writer.WriteNumber("logicalMax", a.LogicalMax);
                writer.WriteNumber("bitSize", a.BitSize);
                WriteNullableNumber(writer, "usagePage", a.UsagePage); WriteNullableNumber(writer, "usage", a.Usage);
                WriteNullableNumber(writer, "linkCollection", a.LinkCollection); WriteNullableNumber(writer, "reportId", a.ReportId);
                writer.WriteBoolean("isAbsolute", a.IsAbsolute); writer.WriteBoolean("hasNull", a.HasNull);
                writer.WriteBoolean("supported", a.Supported);
                if (a.UnsupportedReason is null) writer.WriteNull("unsupportedReason"); else writer.WriteString("unsupportedReason", a.UnsupportedReason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteNullableNumber<T>(Utf8JsonWriter writer, string name, T? value) where T : struct, IConvertible
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value.ToInt32(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed record SettingsLoadResult(UserSettings Settings, string? Warning);

/// <summary>Versioned JSON settings with defensive loading and same-directory atomic replacement.</summary>
public sealed class SettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _path;
    private readonly EstimatorRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public SettingsStore(string? path = null, EstimatorRegistry? registry = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPSS", "settings.json");
        _registry = registry ?? EstimatorRegistry.CreateDefault();
    }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(_path)) return new(UserSettings.Defaults, null);
        try
        {
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path), JsonOptions);
            if (document is null) return Defaults("設定ファイルが空です");
            if (document.SchemaVersion != CurrentSchemaVersion) return Defaults($"未対応の設定スキーマです: {document.SchemaVersion}");
            var data = document.Settings ?? throw new JsonException("settings object is missing");
            var warnings = new List<string>();
            var backend = Enum.IsDefined(data.PreferredBackend) ? data.PreferredBackend : UserSettings.Defaults.PreferredBackend;
            if (backend != data.PreferredBackend) warnings.Add("不明な入力方式を既定値に戻しました");
            var acquisition = data.Acquisition ?? new();
            try { ValidateAcquisition(acquisition); }
            catch (ArgumentException) { acquisition = new(); warnings.Add("不正な取得設定を既定値に戻しました"); }
            acquisition = acquisition with { Mode = AcquisitionMode.LowLatency };
            var method = data.EstimatorMethodId;
            if (method == ModalStepEstimator.Id) method = MeanBitStepEstimator.Id;
            if (string.IsNullOrWhiteSpace(method) || !_registry.Ids.Contains(method, StringComparer.Ordinal))
            { method = MeanBitStepEstimator.Id; warnings.Add("不明な精度算出方式を既定値に戻しました"); }
            AxisMapping? xInputMapping = null;
            if (data.XInputMapping is not null)
            {
                try { xInputMapping = ValidateXInputMapping(data.XInputMapping); }
                catch (ArgumentException) { warnings.Add("不正なXInput軸設定を無視しました"); }
            }
            var profiles = ImmutableArray.CreateBuilder<AxisProfile>();
            foreach (var p in data.Profiles ?? [])
            {
                try
                {
                    if (p is null || p.Fingerprint is null || p.Mapping is null || string.IsNullOrWhiteSpace(p.Fingerprint.Path) ||
                        !IsFingerprintHex(p.Fingerprint.DescriptorSignature)) throw new ArgumentException();
                    var normalized = NormalizeMapping(p.Mapping);
                    ValidateProfileStructure(p.Fingerprint, normalized);
                    profiles.Add(new(p.Fingerprint, normalized));
                }
                catch { warnings.Add("無効な軸プロファイルを無視しました"); }
            }
            var seconds = data.MeasurementWindowSeconds;
            if (!MeasurementWindow.IsSupported(seconds))
            { seconds = MeasurementWindow.DefaultSeconds; warnings.Add("不正な計測期間を30秒に戻しました"); }
            var settings = new UserSettings(backend, acquisition, method, profiles.ToImmutable(), data.SelectedDeviceHint, xInputMapping, seconds);
            return new(settings, warnings.Count == 0 ? null : string.Join("。", warnings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return Defaults($"設定を読み込めません。既定値を使用します: {ex.Message}"); }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.PreferredBackend)) throw new ArgumentException("Unknown backend", nameof(settings));
        MeasurementWindow.Validate(settings.MeasurementWindowSeconds);
        ValidateAcquisition(settings.Acquisition);
        settings = settings with { Acquisition = settings.Acquisition with { Mode = AcquisitionMode.LowLatency },
            EstimatorMethodId = settings.EstimatorMethodId == ModalStepEstimator.Id ? MeanBitStepEstimator.Id : settings.EstimatorMethodId };
        if (!_registry.Ids.Contains(settings.EstimatorMethodId, StringComparer.Ordinal)) throw new ArgumentException("Unknown estimator method", nameof(settings));
        if (settings.XInputMapping is not null) ValidateXInputMapping(settings.XInputMapping);
        if (settings.Profiles.IsDefault) throw new ArgumentException("Profile list is uninitialized", nameof(settings));
        foreach (var profile in settings.Profiles)
        {
            if (profile is null || profile.Fingerprint is null || profile.Mapping is null)
                throw new ArgumentException("Invalid profile", nameof(settings));
            ValidateProfileStructure(profile.Fingerprint, NormalizeMapping(profile.Mapping));
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(new SettingsDocument(CurrentSchemaVersion, SettingsData.From(settings)), JsonOptions);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(json); writer.Flush(); stream.Flush(true); }
            if (File.Exists(_path)) File.Move(temp, _path, true); else File.Move(temp, _path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Returns a profile only for a complete current HID identity and a mapping valid for its axes.</summary>
    public static AxisMapping? FindApplicableProfile(UserSettings settings, DeviceInfo current)
    {
        if (current.Backend == BackendKind.XInput && settings.XInputMapping is not null)
            try
            {
                var normalizedXInput = ValidateXInputMapping(settings.XInputMapping);
                SuggestedMapping.Validate(current, normalizedXInput);
                return normalizedXInput;
            }
            catch (ArgumentException) { return null; }
        if (settings.Profiles.IsDefaultOrEmpty) return null;
        var fingerprint = DeviceFingerprint.FromDevice(current);
        if (fingerprint is null) return null;
        foreach (var profile in settings.Profiles)
            if (profile is not null && profile.Fingerprint is not null && profile.Mapping is not null && profile.Fingerprint.Matches(current))
                try
                {
                    var normalized = NormalizeMapping(profile.Mapping);
                    ValidateProfileStructure(profile.Fingerprint, normalized);
                    ValidateMappingForDevice(current, normalized);
                    return normalized;
                }
                catch (ArgumentException) { return null; }
        return null;
    }

    /// <summary>Add or replace a HID profile for this exact current device identity.</summary>
    public static UserSettings UpsertProfile(UserSettings settings, DeviceInfo device, AxisMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (device.Backend == BackendKind.XInput)
        {
            mapping = ValidateXInputMapping(mapping);
            SuggestedMapping.Validate(device, mapping);
            return settings with { XInputMapping = mapping };
        }
        var fingerprint = DeviceFingerprint.FromDevice(device) ?? throw new ArgumentException("Device has no persistent HID identity", nameof(device));
        mapping = NormalizeMapping(mapping);
        ValidateProfileStructure(fingerprint, mapping);
        ValidateMappingForDevice(device, mapping);
        var profiles = settings.Profiles.IsDefault ? ImmutableArray<AxisProfile>.Empty : settings.Profiles;
        var kept = profiles.Where(p => p is null || p.Fingerprint is null || !p.Fingerprint.Matches(device)).ToImmutableArray();
        return settings with { Profiles = kept.Add(new AxisProfile(fingerprint, mapping)) };
    }

    private static SettingsLoadResult Defaults(string warning) => new(UserSettings.Defaults, warning);
    private static bool IsFingerprintHex(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidateAcquisition(AcquisitionSettings s)
    {
        if (s is null || !Enum.IsDefined(s.Mode) || !double.IsFinite(s.XInputIntervalMs) || s.XInputIntervalMs <= 0 || s.XInputIntervalMs > 1000 ||
            !double.IsFinite(s.BusyWaitMicroseconds) || s.BusyWaitMicroseconds < 0 || s.BusyWaitMicroseconds > 2000 ||
            !double.IsFinite(s.LowLatencyYieldMicroseconds) || s.LowLatencyYieldMicroseconds < 0 || s.LowLatencyYieldMicroseconds > 100000)
            throw new ArgumentException("Invalid acquisition settings", nameof(s));
    }
    private static void ValidateProfileStructure(DeviceFingerprint fingerprint, AxisMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(fingerprint.Path) || !IsFingerprintHex(fingerprint.DescriptorSignature) ||
            mapping.Bindings.IsDefault || mapping.Bindings.Length != 4 || mapping.Bindings.Any(b => b is null ||
                !Enum.IsDefined(b.Axis) || string.IsNullOrWhiteSpace(b.DescriptorKey)))
            throw new ArgumentException("Invalid profile mapping");
        if (mapping.Bindings.Select(b => b.Axis).Distinct().Count() != 4 ||
            mapping.Bindings.Select(b => b.DescriptorKey).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new ArgumentException("Profile must bind four distinct axes");
        var auxiliary = mapping.AuxiliaryBindings.IsDefault ? ImmutableArray<AuxiliaryBinding>.Empty : mapping.AuxiliaryBindings;
        if (auxiliary.Length > 2 || auxiliary.Any(a => a is null ||
                (a.Name != "lt" && a.Name != "rt") || string.IsNullOrWhiteSpace(a.DescriptorKey)))
            throw new ArgumentException("Invalid auxiliary axis binding");
        if (auxiliary.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != auxiliary.Length ||
            auxiliary.Select(a => a.DescriptorKey).Distinct(StringComparer.Ordinal).Count() != auxiliary.Length ||
            auxiliary.Any(a => mapping.Bindings.Any(b => b.DescriptorKey == a.DescriptorKey)))
            throw new ArgumentException("Auxiliary bindings must use unique descriptors and names");
    }

    internal static AxisMapping NormalizeMapping(AxisMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping with { AuxiliaryBindings = mapping.AuxiliaryBindings.IsDefault ? [] : mapping.AuxiliaryBindings };
    }

    private static AxisMapping ValidateXInputMapping(AxisMapping mapping)
    {
        mapping = NormalizeMapping(mapping);
        var placeholder = new DeviceFingerprint("xinput", 0, 0, new string('0', 64));
        ValidateProfileStructure(placeholder, mapping);
        var nativeKeys = new HashSet<string>(Enum.GetNames<Axis>(), StringComparer.OrdinalIgnoreCase);
        if (!mapping.AuxiliaryBindings.IsEmpty || mapping.Bindings.Any(b => !nativeKeys.Contains(b.DescriptorKey)))
            throw new ArgumentException("XInput mappings must use the standard axis layout and native triggers");
        var canonicalNames = Enum.GetNames<Axis>();
        mapping = mapping with { Bindings = mapping.Bindings.Select(b => b with
            { DescriptorKey = canonicalNames.Single(n => string.Equals(n, b.DescriptorKey, StringComparison.OrdinalIgnoreCase)) }).ToImmutableArray() };
        ValidateProfileStructure(placeholder, mapping);
        return mapping;
    }

    private static void ValidateMappingForDevice(DeviceInfo device, AxisMapping mapping)
    {
        SuggestedMapping.Validate(device, mapping);
        var stickKeys = mapping.Bindings.Select(b => b.DescriptorKey).ToHashSet(StringComparer.Ordinal);
        foreach (var auxiliary in mapping.AuxiliaryBindings)
            if (stickKeys.Contains(auxiliary.DescriptorKey) || !device.Axes.Any(d => d.Key == auxiliary.DescriptorKey &&
                    d.Supported && d.IsAbsolute && d.LogicalMax > d.LogicalMin))
                throw new ArgumentException($"未対応の補助軸です: {auxiliary.DescriptorKey}");
    }
    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        o.Converters.Add(new JsonStringEnumConverter()); return o;
    }

    private sealed record SettingsDocument(int SchemaVersion, SettingsData? Settings);
    private sealed record SettingsData(BackendKind PreferredBackend, AcquisitionSettings? Acquisition, string? EstimatorMethodId,
        List<AxisProfile>? Profiles, string? SelectedDeviceHint, AxisMapping? XInputMapping = null,
        int MeasurementWindowSeconds = MeasurementWindow.DefaultSeconds)
    {
        public static SettingsData From(UserSettings s) => new(s.PreferredBackend, s.Acquisition, s.EstimatorMethodId,
            s.Profiles.Select(p => p with { Mapping = SettingsStore.NormalizeMapping(p.Mapping) }).ToList(),
            s.SelectedDeviceHint, s.XInputMapping is null ? null : SettingsStore.NormalizeMapping(s.XInputMapping), s.MeasurementWindowSeconds);
    }
}
