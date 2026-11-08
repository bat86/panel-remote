using System;
using System.Runtime.InteropServices;

namespace PanelRemote.Interop;

internal static class DisplayConfigNative
{
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint QDC_VIRTUAL_MODE_AWARE = 0x00000010;

    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;

    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    public const uint SDC_ALLOW_CHANGES = 0x00000400;
    public const uint SDC_NO_OPTIMIZATION = 0x00000100;
    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    public const uint DISPLAYCONFIG_TARGET_IN_USE = 0x00000001;
    public const uint DISPLAYCONFIG_TARGET_FORCIBLE = 0x00000002;

    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_VGA = 0;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI = 4;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL = 12;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST = 15;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 16;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL = 17;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL = 18;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    public const uint DISPLAYCONFIG_TOPOLOGY_INTERNAL = 1;
    public const uint DISPLAYCONFIG_TOPOLOGY_CLONE = 2;
    public const uint DISPLAYCONFIG_TOPOLOGY_EXTEND = 3;
    public const uint DISPLAYCONFIG_TOPOLOGY_EXTERNAL = 4;

    public const uint DISPLAYCONFIG_PIXELFORMAT_32BPP = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;

        public bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;

        public int ToHz()
        {
            if (Denominator == 0)
            {
                return 0;
            }

            return (int)Math.Round(Numerator / (double)Denominator);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public int positionX;
        public int positionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public DISPLAYCONFIG_POINT PathSourceSize;
        public DISPLAYCONFIG_RECT DesktopImageRegion;
        public DISPLAYCONFIG_RECT DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint width;
        public uint height;
        public DISPLAYCONFIG_TARGET_MODE targetMode;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    public static extern int QueryDisplayConfigWithTopology(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        out uint currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_TARGET_PREFERRED_MODE preferredMode);
}
