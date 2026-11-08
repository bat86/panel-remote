using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PanelRemote.Interop;
using PanelRemote.Models;

namespace PanelRemote.Services;

/// <summary>
/// Core Audio: перечисление устройств воспроизведения, определение HDMI/DP-аудио
/// панели и переключение устройства по умолчанию через IPolicyConfig.
/// Всё работает от обычного пользователя.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly object _soundGate = new();
    private IMMDeviceEnumerator? _monitorEnumerator;
    private NotificationClient? _notificationClient;
    private GCHandle _testSoundHandle;
    private bool _testSoundAllocated;
    private bool _disposed;
    private string _lastLoggedFingerprint = string.Empty;

    public AudioService(AppLog log)
    {
        _log = log;
    }

    public event EventHandler? DevicesChanged;

    public void StartMonitoring()
    {
        lock (_gate)
        {
            if (_disposed || _monitorEnumerator is not null)
            {
                return;
            }

            try
            {
                _monitorEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _notificationClient = new NotificationClient(this);
                var result = _monitorEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
                if (result == 0)
                {
                    _log.Info("Подписка на уведомления Core Audio активна.");
                }
                else
                {
                    _log.Warn($"RegisterEndpointNotificationCallback вернул 0x{result:X8}.");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось подписаться на уведомления Core Audio", ex);
            }
        }
    }

    public AudioSnapshot Detect(string? panelName, bool panelConnected, string? computerName = null)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                var defaultId = GetDefaultDeviceId(enumerator);
                var raw = new List<RawEndpoint>();
                var hr = enumerator.EnumAudioEndpoints(
                    EDataFlow.eRender,
                    (uint)DeviceState.Active,
                    out var collection);

                if (hr != 0 || collection is null)
                {
                    throw new COMException($"EnumAudioEndpoints: 0x{hr:X8}", hr);
                }

                try
                {
                    collection.GetCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice? device = null;
                        try
                        {
                            if (collection.Item(i, out device) != 0 || device is null)
                            {
                                continue;
                            }

                            if (device.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id))
                            {
                                continue;
                            }

                            var (friendly, description, interfaceName) = GetDeviceNames(device);
                            var displayName = !string.IsNullOrWhiteSpace(friendly)
                                ? friendly
                                : !string.IsNullOrWhiteSpace(description)
                                    ? description
                                    : "Аудиоустройство";

                            var score = ScorePanelCandidate(
                                $"{friendly} {description} {interfaceName}",
                                panelName,
                                computerName,
                                panelConnected,
                                out var isPanelCandidate);

                            raw.Add(new RawEndpoint(
                                id,
                                displayName,
                                description,
                                string.Join(" · ", new[] { friendly, description, interfaceName }
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                                isPanelCandidate,
                                score,
                                id == defaultId));
                        }
                        finally
                        {
                            ReleaseCom(device);
                        }
                    }
                }
                finally
                {
                    ReleaseCom(collection);
                }

                if (raw.Count == 0)
                {
                    _log.Warn("Windows не вернула ни одного активного устройства воспроизведения.");
                    return EmptySnapshot();
                }

                var panel = panelConnected
                    ? raw.Where(e => e.IsPanelCandidate)
                        .OrderByDescending(e => e.PanelScore)
                        .ThenByDescending(e => e.IsDefault)
                        .FirstOrDefault()
                    : null;

                var computer = raw
                    .Where(e => panel is null || !string.Equals(e.Id, panel.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.IsDefault)
                    .ThenByDescending(e => ScoreComputer(e.CombinedName, e.IsDefault))
                    .FirstOrDefault();

                if (computer is null && panel is null)
                {
                    computer = raw.OrderByDescending(e => e.IsDefault).FirstOrDefault();
                }

                var endpointInfos = raw
                    .Select(e => new AudioEndpointInfo(
                        e.Id,
                        e.FriendlyName,
                        e.Description,
                        e.IsDefault,
                        e.IsPanelCandidate,
                        e.PanelScore,
                        "активно"))
                    .OrderByDescending(e => e.IsPanelCandidate)
                    .ThenByDescending(e => e.IsDefault)
                    .ToList();

                var defaultEndpoint = endpointInfos.FirstOrDefault(e => e.IsDefault);
                var panelEndpoint = panel is null
                    ? null
                    : endpointInfos.FirstOrDefault(e => e.Id == panel.Id);
                var computerEndpoint = computer is null
                    ? null
                    : endpointInfos.FirstOrDefault(e => e.Id == computer.Id);

                var current = defaultEndpoint is null
                    ? AudioTarget.Unknown
                    : panelEndpoint is not null
                      && string.Equals(defaultEndpoint.Id, panelEndpoint.Id, StringComparison.OrdinalIgnoreCase)
                        ? AudioTarget.Panel
                        : AudioTarget.Computer;

                var snapshot = new AudioSnapshot(
                    endpointInfos,
                    defaultEndpoint,
                    panelEndpoint,
                    computerEndpoint,
                    current,
                    panelEndpoint is not null);

                var fingerprint = string.Join("|", raw.Select(e =>
                    $"{e.Id}:{(e.IsDefault ? "1" : "0")}:{e.PanelScore}")) +
                    $"|target={current}|panel={snapshot.PanelAudioAvailable}";
                if (!string.Equals(fingerprint, _lastLoggedFingerprint, StringComparison.Ordinal))
                {
                    _lastLoggedFingerprint = fingerprint;
                    _log.Info("Аудиоустройства: " + string.Join("; ", raw.Select(e =>
                        $"{(e.IsDefault ? "*" : "")}{e.FriendlyName} [panel={e.IsPanelCandidate}, score={e.PanelScore}]")) +
                        $"; default={snapshot.DefaultName}; target={current}.");
                }

                return snapshot;
            }
            finally
            {
                ReleaseCom(enumerator);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось перечислить аудиоустройства", ex);
            return EmptySnapshot();
        }
    }

    public AudioApplyResult SetDefault(
        AudioTarget target,
        string? preferredPanelId,
        string? preferredComputerId,
        string? panelName,
        bool panelConnected,
        string? computerName = null)
    {
        try
        {
            var before = Detect(panelName, panelConnected, computerName);
            Choice? choice;

            if (target == AudioTarget.Panel)
            {
                choice = ResolvePanel(before, preferredPanelId);
                if (choice is null)
                {
                    var message = !panelConnected
                        ? "Панель не подключена — воткните кабель HDMI."
                        : "Windows не видит динамики панели. Проверьте, что панель подключена и звук в ней включён.";
                    _log.Warn("Не найдено аудиоустройство панели. " + message);
                    return new AudioApplyResult(false, target, before, message);
                }
            }
            else
            {
                choice = ResolveComputer(before, preferredComputerId);
                if (choice is null)
                {
                    return new AudioApplyResult(
                        false,
                        target,
                        before,
                        "Не найдено устройство звука компьютера. Нажмите «Диагностика».");
                }
            }

            _log.Info($"Переключаю звук на {target}: {choice.Endpoint.DisplayName} ({choice.Endpoint.Id}).");
            var policy = (IPolicyConfig)new PolicyConfigClient();
            var consoleResult = 0;
            try
            {
                consoleResult = policy.SetDefaultEndpoint(choice.Endpoint.Id, ERole.eConsole);
                var multimedia = policy.SetDefaultEndpoint(choice.Endpoint.Id, ERole.eMultimedia);
                var communications = policy.SetDefaultEndpoint(choice.Endpoint.Id, ERole.eCommunications);
                if (consoleResult != 0)
                {
                    _log.Warn($"SetDefaultEndpoint(eConsole) = 0x{consoleResult:X8}");
                }

                if (multimedia != 0)
                {
                    _log.Warn($"SetDefaultEndpoint(eMultimedia) = 0x{multimedia:X8}");
                }

                if (communications != 0)
                {
                    _log.Warn($"SetDefaultEndpoint(eCommunications) = 0x{communications:X8}");
                }
            }
            finally
            {
                ReleaseCom(policy);
            }

            if (consoleResult != 0)
            {
                return new AudioApplyResult(
                    false,
                    target,
                    before,
                    "Windows не дала переключить звук. Нажмите «Диагностика».");
            }

            AudioSnapshot after = before;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                Thread.Sleep(180);
                after = Detect(panelName, panelConnected, computerName);
                if (string.Equals(after.DefaultEndpoint?.Id, choice.Endpoint.Id, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            var actual = after.DefaultEndpoint;
            var success = actual is not null
                          && string.Equals(actual.Id, choice.Endpoint.Id, StringComparison.OrdinalIgnoreCase);

            var resultMessage = success
                ? target == AudioTarget.Panel ? "Звук переключён на панель." : "Звук переключён на компьютер."
                : "Windows не подтвердила смену устройства звука. Нажмите «Диагностика».";

            _log.Info($"Результат переключения звука: success={success}, default={after.DefaultName}, target={after.CurrentTarget}.");

            return new AudioApplyResult(success, target, after, resultMessage, choice.Endpoint.Id);
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка переключения звука на {target}", ex);
            return new AudioApplyResult(
                false,
                target,
                Detect(panelName, panelConnected, computerName),
                "Не удалось переключить звук. Подробности записаны в журнал.",
                null);
        }
    }

    public bool PlayTestSound()
    {
        try
        {
            lock (_soundGate)
            {
                if (!_testSoundAllocated)
                {
                    var bytes = BuildTestSound();
                    _testSoundHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    _testSoundAllocated = true;
                }

                AudioNativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                var ok = AudioNativeMethods.PlaySound(
                    _testSoundHandle.AddrOfPinnedObject(),
                    IntPtr.Zero,
                    AudioNativeMethods.SND_MEMORY | AudioNativeMethods.SND_ASYNC | AudioNativeMethods.SND_NODEFAULT);

                if (ok)
                {
                    _log.Info("Воспроизведён тестовый звук на текущем устройстве вывода.");
                }
                else
                {
                    _log.Warn("Не удалось воспроизвести тестовый звук.");
                }

                return ok;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка тестового звука", ex);
            return false;
        }
    }

    private static Choice? ResolvePanel(AudioSnapshot snapshot, string? preferredPanelId)
    {
        if (!string.IsNullOrWhiteSpace(preferredPanelId))
        {
            var saved = snapshot.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, preferredPanelId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null && (saved.IsPanelCandidate || saved.Id == snapshot.PanelEndpoint?.Id))
            {
                return new Choice(saved);
            }
        }

        return snapshot.PanelEndpoint is null ? null : new Choice(snapshot.PanelEndpoint);
    }

    private static Choice? ResolveComputer(AudioSnapshot snapshot, string? preferredComputerId)
    {
        if (!string.IsNullOrWhiteSpace(preferredComputerId))
        {
            var saved = snapshot.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, preferredComputerId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null
                && (snapshot.PanelEndpoint is null
                    || !string.Equals(saved.Id, snapshot.PanelEndpoint.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return new Choice(saved);
            }
        }

        if (snapshot.ComputerEndpoint is not null)
        {
            return new Choice(snapshot.ComputerEndpoint);
        }

        var fallback = snapshot.Endpoints
            .FirstOrDefault(e => snapshot.PanelEndpoint is null
                                 || !string.Equals(e.Id, snapshot.PanelEndpoint.Id, StringComparison.OrdinalIgnoreCase));
        return fallback is null ? null : new Choice(fallback);
    }

    private static int ScorePanelCandidate(
        string text,
        string? panelName,
        string? computerName,
        bool panelConnected,
        out bool isCandidate)
    {
        var lower = Normalize(text);
        var score = 0;

        if (!panelConnected)
        {
            isCandidate = false;
            return 0;
        }

        if (lower.Contains("hdmi", StringComparison.Ordinal))
        {
            score += 120;
        }

        if (lower.Contains("displayport", StringComparison.Ordinal)
            || lower.Contains("display port", StringComparison.Ordinal))
        {
            score += 110;
        }

        if (lower.Contains("nvidia high definition audio", StringComparison.Ordinal)
            || lower.Contains("amd high definition audio", StringComparison.Ordinal)
            || lower.Contains("intel(r) display audio", StringComparison.Ordinal)
            || lower.Contains("intel display audio", StringComparison.Ordinal)
            || lower.Contains("display audio", StringComparison.Ordinal))
        {
            score += 100;
        }

        if (lower.Contains("usb-c", StringComparison.Ordinal)
            || lower.Contains("usb c", StringComparison.Ordinal)
            || lower.Contains("usb audio", StringComparison.Ordinal))
        {
            score += 55;
        }

        var matchedPanelName = false;
        foreach (var token in GetPanelNameTokens(panelName))
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                score += 140;
                matchedPanelName = true;
                break;
            }
        }

        if (!matchedPanelName)
        {
            foreach (var token in GetPanelNameTokens(computerName))
            {
                if (lower.Contains(token, StringComparison.Ordinal))
                {
                    // Это, скорее всего, аудио монитора компьютера, а не панели.
                    score -= 130;
                    break;
                }
            }
        }

        if (panelConnected && string.IsNullOrWhiteSpace(panelName) && lower.Contains("hdmi", StringComparison.Ordinal))
        {
            score += 20;
        }

        if (lower.Contains("realtek", StringComparison.Ordinal))
        {
            score -= 15;
        }

        if (lower.Contains("headphone", StringComparison.Ordinal)
            || lower.Contains("headset", StringComparison.Ordinal)
            || lower.Contains("наушник", StringComparison.Ordinal))
        {
            score -= 35;
        }

        isCandidate = score >= 80;
        return score;
    }

    private static int ScoreComputer(string combinedName, bool isDefault)
    {
        var text = Normalize(combinedName);
        var score = 0;
        if (isDefault)
        {
            score += 100;
        }

        if (text.Contains("realtek", StringComparison.Ordinal))
        {
            score += 40;
        }

        if (text.Contains("speaker", StringComparison.Ordinal)
            || text.Contains("динамик", StringComparison.Ordinal))
        {
            score += 30;
        }

        if (text.Contains("headphone", StringComparison.Ordinal)
            || text.Contains("headset", StringComparison.Ordinal)
            || text.Contains("наушник", StringComparison.Ordinal))
        {
            score += 10;
        }

        if (text.Contains("hdmi", StringComparison.Ordinal)
            || text.Contains("displayport", StringComparison.Ordinal)
            || text.Contains("display audio", StringComparison.Ordinal))
        {
            score -= 80;
        }

        return score;
    }

    private static IEnumerable<string> GetPanelNameTokens(string? panelName)
    {
        if (string.IsNullOrWhiteSpace(panelName))
        {
            yield break;
        }

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "monitor", "display", "generic", "pnp", "digital", "panel", "board",
            "smart", "with", "audio", "video", "super", "inc", "corp", "corporation",
            "technologies", "technology", "ltd", "the", "and", "интерактивная", "панель"
        };

        var current = new StringBuilder();
        foreach (var ch in panelName + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (current.Length >= 4)
                {
                    var token = current.ToString();
                    if (!stopWords.Contains(token))
                    {
                        yield return token;
                    }
                }

                current.Clear();
            }
        }
    }

    private static string GetDefaultDeviceId(IMMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
        {
            IMMDevice? device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out device) == 0
                    && device is not null
                    && device.GetId(out var id) == 0
                    && !string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }
            catch
            {
                // роль может быть не назначена
            }
            finally
            {
                ReleaseCom(device);
            }
        }

        return string.Empty;
    }

    private static (string Friendly, string Description, string InterfaceName) GetDeviceNames(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(AudioNativeMethods.STGM_READ, out store) != 0 || store is null)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            return (
                ReadString(store, AudioNativeMethods.PkeyDeviceFriendlyName),
                ReadString(store, AudioNativeMethods.PkeyDeviceDeviceDesc),
                ReadString(store, AudioNativeMethods.PkeyDeviceInterfaceFriendlyName));
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
        finally
        {
            ReleaseCom(store);
        }
    }

    private static string ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        var value = default(PROPVARIANT);
        var hr = store.GetValue(ref key, out value);
        if (hr != 0)
        {
            return string.Empty;
        }

        try
        {
            if (value.vt == AudioNativeMethods.VT_LPWSTR && value.pointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(value.pointerValue)?.Trim() ?? string.Empty;
            }

            if (value.vt == AudioNativeMethods.VT_BSTR && value.pointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringBSTR(value.pointerValue)?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }
        finally
        {
            AudioNativeMethods.PropVariantClear(ref value);
        }
    }

    private static AudioSnapshot EmptySnapshot()
        => new(Array.Empty<AudioEndpointInfo>(), null, null, null, AudioTarget.Unknown, false);

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch
            {
                // игнорируем
            }
        }
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace('\0', ' ').Trim().ToLowerInvariant();

    internal void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка обработчика уведомлений Core Audio", ex);
        }
    }

    private static byte[] BuildTestSound()
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var notes = new[]
        {
            (Frequency: 740.0, Duration: 0.16),
            (Frequency: 0.0, Duration: 0.04),
            (Frequency: 988.0, Duration: 0.24)
        };

        var samples = new List<short>();
        foreach (var note in notes)
        {
            var count = (int)(sampleRate * note.Duration);
            for (var i = 0; i < count; i++)
            {
                if (note.Frequency <= 0)
                {
                    samples.Add(0);
                    continue;
                }

                var fade = Math.Min(1.0, Math.Min(i / (sampleRate * 0.012), (count - i) / (sampleRate * 0.018)));
                var envelope = Math.Max(0, fade);
                var value = Math.Sin(2 * Math.PI * note.Frequency * i / sampleRate) * 0.30 * envelope;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var dataSize = samples.Count * sizeof(short);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_monitorEnumerator is not null && _notificationClient is not null)
                {
                    _monitorEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Не удалось отписаться от уведомлений Core Audio: {ex.Message}");
            }

            ReleaseCom(_monitorEnumerator);
            _monitorEnumerator = null;
            _notificationClient = null;
            DevicesChanged = null;
        }

        lock (_soundGate)
        {
            if (_testSoundAllocated)
            {
                try
                {
                    AudioNativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                    _testSoundHandle.Free();
                }
                catch
                {
                    // игнорируем
                }

                _testSoundAllocated = false;
            }
        }
    }

    private sealed record RawEndpoint(
        string Id,
        string FriendlyName,
        string Description,
        string CombinedName,
        bool IsPanelCandidate,
        int PanelScore,
        bool IsDefault);

    private sealed record Choice(AudioEndpointInfo Endpoint);

    [ComVisible(true)]
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioService _owner;

        public NotificationClient(AudioService owner)
        {
            _owner = owner;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            _owner.RaiseDevicesChanged();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            _owner.RaiseDevicesChanged();
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            _owner.RaiseDevicesChanged();
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.eRender)
            {
                _owner.RaiseDevicesChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key)
            => 0;
    }
}
