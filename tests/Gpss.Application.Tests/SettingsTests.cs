using Gpss.Application;
using Gpss.Core;
using System.Globalization;
using System.Collections.Immutable;

namespace Gpss.Application.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void SelectedMeasurementPeriodRoundTripsAndInvalidPeriodIsRejected(int seconds)
    {
        using var temp = new TemporaryDirectory(); var store = new SettingsStore(temp.File);
        store.Save(new UserSettings(measurementWindowSeconds: seconds));
        Assert.Equal(seconds, store.Load().Settings.MeasurementWindowSeconds);
        Assert.Null(store.Load().Warning);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new UserSettings(measurementWindowSeconds: 20)));
        var json = File.ReadAllText(temp.File).Replace($"\"MeasurementWindowSeconds\": {seconds}", "\"MeasurementWindowSeconds\": 20");
        File.WriteAllText(temp.File, json);
        Assert.Equal(30, store.Load().Settings.MeasurementWindowSeconds);
        Assert.NotNull(store.Load().Warning);
    }

    [Fact]
    public void LegacyNormalAndModalSettingsMigrateAndSaveAsLowLatencyMean()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(temp.File, """{"SchemaVersion":1,"Settings":{"PreferredBackend":"XInput","Acquisition":{"Mode":"Normal","XInputIntervalMs":3,"BusyWaitMicroseconds":10,"LowLatencyYieldMicroseconds":50},"EstimatorMethodId":"modal_delta_v1","Profiles":[]}}""");
        var store = new SettingsStore(temp.File); var loaded = store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(AcquisitionMode.LowLatency, loaded.Settings.Acquisition.Mode);
        Assert.Equal(MeanBitStepEstimator.Id, loaded.Settings.EstimatorMethodId);
        Assert.Equal(30, loaded.Settings.MeasurementWindowSeconds);
        Assert.Equal(50, loaded.Settings.Acquisition.LowLatencyYieldMicroseconds);
        store.Save(loaded.Settings with { Acquisition = loaded.Settings.Acquisition with { Mode = AcquisitionMode.Normal } });
        var json = File.ReadAllText(temp.File);
        Assert.DoesNotContain("Normal", json); Assert.Contains("LowLatency", json);
        Assert.DoesNotContain("modal_delta_v1", json); Assert.Contains(MeanBitStepEstimator.Id, json);
    }

    private static readonly AxisMapping Mapping = new([
        new(Axis.LX, "x1"), new(Axis.LY, "y1"), new(Axis.RX, "x2"), new(Axis.RY, "y2")], [
        new("lt", "trigger-left"), new("rt", "trigger-right")]);
    private static DeviceInfo Hid(string path, bool changed = false) => new("id", "pad", BackendKind.HidDirect,
        [new("x1", "X", -127, 127, 8, 1, 0x30), new("y1", "Y", -127, 127, 8, 1, 0x31),
         new("x2", "RX", changed ? -32767 : -127, changed ? 32767 : 127, changed ? 16 : 8, 1, 0x33),
         new("y2", "RY", -127, 127, 8, 1, 0x34), new("trigger-left", "LT", 0, 255, 8),
         new("trigger-right", "RT", 0, 255, 8)], path, VendorId: 0x1234, ProductId: 0xabcd);
    private static DeviceInfo XInput(uint slot) => new($"slot:{slot}", "XInput controller", BackendKind.XInput,
        Enum.GetValues<Axis>().Select(a => new AxisDescriptor(a.ToString(), a.ToString(), short.MinValue, short.MaxValue)).ToImmutableArray(),
        XInputSlot: slot);

    [Fact]
    public void MissingCorruptAndUnknownSchemaFallBackWithWarning()
    {
        using var temp = new TemporaryDirectory();
        var store = new SettingsStore(temp.File);
        Assert.Null(store.Load().Warning);
        File.WriteAllText(temp.File, "{bad json");
        Assert.NotNull(store.Load().Warning);
        File.WriteAllText(temp.File, "{\"SchemaVersion\":999,\"Settings\":{}}");
        var unknown = store.Load();
        Assert.NotNull(unknown.Warning);
        Assert.Equal(UserSettings.Defaults, unknown.Settings);
    }

    [Fact]
    public void SaveRoundTripsThroughAtomicSameDirectoryReplacement()
    {
        using var temp = new TemporaryDirectory();
        var device = Hid("\\\\?\\hid#one");
        var profile = new AxisProfile(DeviceFingerprint.FromDevice(device)!, Mapping);
        var expected = new UserSettings(BackendKind.HidDirect, new(AcquisitionMode.LowLatency, 3, 10, 50),
            MeanBitStepEstimator.Id, [profile], "device-display-hint");
        var store = new SettingsStore(temp.File);
        store.Save(expected);
        var loaded = store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(expected.PreferredBackend, loaded.Settings.PreferredBackend);
        Assert.Equal(expected.Acquisition, loaded.Settings.Acquisition);
        Assert.Equal(expected.EstimatorMethodId, loaded.Settings.EstimatorMethodId);
        Assert.Equal(expected.SelectedDeviceHint, loaded.Settings.SelectedDeviceHint);
        Assert.Single(loaded.Settings.Profiles);
        Assert.Equal(expected.Profiles[0].Fingerprint, loaded.Settings.Profiles[0].Fingerprint);
        Assert.Equal(expected.Profiles[0].Mapping.Bindings.ToArray(), loaded.Settings.Profiles[0].Mapping.Bindings.ToArray());
        Assert.Equal(expected.Profiles[0].Mapping.AuxiliaryBindings.ToArray(), loaded.Settings.Profiles[0].Mapping.AuxiliaryBindings.ToArray());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void DeviceProfilesRequirePathAndCompleteUnchangedDescriptor()
    {
        var settings = new UserSettings(profiles: [new(DeviceFingerprint.FromDevice(Hid("path-A"))!, Mapping)]);
        Assert.NotNull(SettingsStore.FindApplicableProfile(settings, Hid("path-A")));
        Assert.Null(SettingsStore.FindApplicableProfile(settings, Hid("path-B")));
        Assert.Null(SettingsStore.FindApplicableProfile(settings, Hid("path-A", changed: true)));
        var xinput = new DeviceInfo("slot:0", "pad", BackendKind.XInput, Hid("path-A").Axes, XInputSlot: 0);
        Assert.Null(DeviceFingerprint.FromDevice(xinput));
        Assert.Null(SettingsStore.FindApplicableProfile(settings, xinput));
    }

    [Fact]
    public void DescriptorSignatureDoesNotDependOnCurrentCulture()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = DeviceFingerprint.FromDevice(Hid("path-A"));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            var second = DeviceFingerprint.FromDevice(Hid("path-A"));
            Assert.Equal(first, second);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    [Fact]
    public void InvalidSettingsAreRecoveredOrRejectedOnSave()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(temp.File, """{"SchemaVersion":1,"Settings":{"PreferredBackend":"Bogus","Acquisition":{"Mode":"Unknown","XInputIntervalMs":1,"BusyWaitMicroseconds":2,"LowLatencyYieldMicroseconds":3},"EstimatorMethodId":"no-such-method","Profiles":[]}}""");
        var loaded = new SettingsStore(temp.File).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal(BackendKind.HidDirect, loaded.Settings.PreferredBackend);
        Assert.Equal(MeanBitStepEstimator.Id, loaded.Settings.EstimatorMethodId);
        Assert.Equal(new AcquisitionSettings(), loaded.Settings.Acquisition);
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(
            acquisition: new(XInputIntervalMs: double.NaN))));
    }

    [Fact]
    public void InvalidMappingIsRejectedBeforeSave()
    {
        using var temp = new TemporaryDirectory();
        var fp = DeviceFingerprint.FromDevice(Hid("path-A"))!;
        var invalid = Mapping with { Bindings = [new(Axis.LX, "x1"), new(Axis.LY, "x1"), new(Axis.RX, "x2"), new(Axis.RY, "y2")] };
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(profiles: [new(fp, invalid)])));
        var badAxis = Mapping with { Bindings = [new((Axis)99, "x1"), new(Axis.LY, "y1"), new(Axis.RX, "x2"), new(Axis.RY, "y2")] };
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(profiles: [new(fp, badAxis)])));
        var missingKey = Mapping with { Bindings = [new(Axis.LX, ""), new(Axis.LY, "y1"), new(Axis.RX, "x2"), new(Axis.RY, "y2")] };
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(profiles: [new(fp, missingKey)])));
        var duplicateAux = Mapping with { AuxiliaryBindings = [new("lt", "trigger-left"), new("rt", "x1")] };
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(profiles: [new(fp, duplicateAux)])));
        var invalidAuxName = Mapping with { AuxiliaryBindings = [new("trigger", "trigger-left")] };
        Assert.Throws<ArgumentException>(() => new SettingsStore(temp.File).Save(new UserSettings(profiles: [new(fp, invalidAuxName)])));
    }

    [Fact]
    public void ProfileUpsertNormalizesDefaultOptionalArrayAndValidatesAuxiliaryAxes()
    {
        using var temp = new TemporaryDirectory();
        var device = Hid("path-A");
        var mappingWithoutAux = Mapping with { AuxiliaryBindings = default };
        var settings = SettingsStore.UpsertProfile(UserSettings.Defaults, device, mappingWithoutAux);
        Assert.Single(settings.Profiles);
        Assert.Empty(settings.Profiles[0].Mapping.AuxiliaryBindings);
        var store = new SettingsStore(temp.File);
        store.Save(settings);
        Assert.Null(store.Load().Warning);

        var missingAuxDescriptor = Mapping with { AuxiliaryBindings = [new("lt", "not-on-device")] };
        var stored = new UserSettings(profiles: [new(DeviceFingerprint.FromDevice(device)!, missingAuxDescriptor)]);
        Assert.Null(SettingsStore.FindApplicableProfile(stored, device));
    }

    [Fact]
    public void XInputMappingIsGlobalAcrossSlotsAndDoesNotCreateDeviceProfile()
    {
        using var temp = new TemporaryDirectory();
        var custom = new AxisMapping([
            new(Axis.LX, "RY", true), new(Axis.LY, "RX"), new(Axis.RX, "LX", true), new(Axis.RY, "LY")]);
        var settings = SettingsStore.UpsertProfile(UserSettings.Defaults, XInput(0), custom);
        Assert.Empty(settings.Profiles);
        Assert.Null(settings.SelectedDeviceHint);
        Assert.Equal(custom.Bindings.ToArray(), SettingsStore.FindApplicableProfile(settings, XInput(0))!.Bindings.ToArray());
        Assert.Equal(custom.Bindings.ToArray(), SettingsStore.FindApplicableProfile(settings, XInput(3))!.Bindings.ToArray());

        var store = new SettingsStore(temp.File);
        store.Save(settings);
        var loaded = store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(custom.Bindings.ToArray(), SettingsStore.FindApplicableProfile(loaded.Settings, XInput(2))!.Bindings.ToArray());

        var badKeys = custom with { Bindings = [new(Axis.LX, "trigger"), new(Axis.LY, "RX"), new(Axis.RX, "LX"), new(Axis.RY, "LY")] };
        Assert.Null(SettingsStore.FindApplicableProfile(new UserSettings(xInputMapping: badKeys), XInput(1)));
        Assert.Throws<ArgumentException>(() => store.Save(new UserSettings(xInputMapping: badKeys)));
        var withAux = custom with { AuxiliaryBindings = [new("lt", "LX")] };
        Assert.Throws<ArgumentException>(() => store.Save(new UserSettings(xInputMapping: withAux)));
    }

    [Fact]
    public void InvalidStoredProfileIsIgnoredWithWarning()
    {
        using var temp = new TemporaryDirectory();
        var fp = DeviceFingerprint.FromDevice(Hid("path-A"))!;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = SettingsStore.CurrentSchemaVersion,
            Settings = new
            {
                PreferredBackend = "HidDirect", Acquisition = new AcquisitionSettings(), EstimatorMethodId = MeanBitStepEstimator.Id,
                SelectedDeviceHint = (string?)null,
                Profiles = new[] { new { Fingerprint = fp, Mapping = Mapping with { Bindings = [new(Axis.LX, "x1"), new(Axis.LY, "x1"), new(Axis.RX, "x2"), new(Axis.RY, "y2")] } } }
            }
        });
        File.WriteAllText(temp.File, json);
        var loaded = new SettingsStore(temp.File).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Empty(loaded.Settings.Profiles);
    }

    [Fact]
    public void NullAndIncompleteProfilesAreSkippedAndBadSettingsTypesFallBack()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(temp.File, """{"SchemaVersion":1,"Settings":{"PreferredBackend":999,"Acquisition":{"Mode":"Normal","XInputIntervalMs":"not-a-number","BusyWaitMicroseconds":2,"LowLatencyYieldMicroseconds":3},"EstimatorMethodId":"modal_delta_v1","Profiles":[null,{}]}}""");
        var loaded = new SettingsStore(temp.File).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal(UserSettings.Defaults, loaded.Settings);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string File => System.IO.Path.Combine(Path, "settings.json");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
