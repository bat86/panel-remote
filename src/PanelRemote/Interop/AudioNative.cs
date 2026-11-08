using System;
using System.Runtime.InteropServices;

namespace PanelRemote.Interop;

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid formatId, uint propertyId)
    {
        fmtid = formatId;
        pid = propertyId;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;
    [FieldOffset(8)] public IntPtr pointerValue;
    [FieldOffset(8)] public int intValue;
    [FieldOffset(8)] public uint uintValue;
    [FieldOffset(8)] public long longValue;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid iid,
        uint dwClsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PROPERTYKEY key);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        uint newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        PROPERTYKEY key);
}

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient
{
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        out IntPtr format);

    [PreserveSig]
    int GetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        [MarshalAs(UnmanagedType.Bool)] bool isDefault,
        out IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

    [PreserveSig]
    int SetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        IntPtr endpointFormat,
        IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        [MarshalAs(UnmanagedType.Bool)] bool isDefault,
        out long defaultPeriod,
        out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        ref long period);

    [PreserveSig]
    int GetShareMode(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        out IntPtr mode);

    [PreserveSig]
    int SetShareMode(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        IntPtr mode);

    [PreserveSig]
    int GetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        ref PROPERTYKEY key,
        out PROPVARIANT value);

    [PreserveSig]
    int SetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        ref PROPERTYKEY key,
        ref PROPVARIANT value);

    [PreserveSig]
    int SetDefaultEndpoint(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        ERole role);

    [PreserveSig]
    int SetEndpointVisibility(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        [MarshalAs(UnmanagedType.Bool)] bool visible);
}

internal static class AudioNativeMethods
{
    public const uint STGM_READ = 0;

    public static readonly PROPERTYKEY PkeyDeviceFriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static readonly PROPERTYKEY PkeyDeviceDeviceDesc =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    public static readonly PROPERTYKEY PkeyDeviceInterfaceFriendlyName =
        new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    public const ushort VT_LPWSTR = 31;
    public const ushort VT_BSTR = 8;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT propVariant);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    public const uint SND_ASYNC = 0x0001;
    public const uint SND_NODEFAULT = 0x0002;
    public const uint SND_MEMORY = 0x0004;
}
