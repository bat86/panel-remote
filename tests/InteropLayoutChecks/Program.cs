using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PanelRemote.Interop;

internal static class Program
{
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        // Размеры структур должны совпадать с нативными x64-структурами Windows.
        CheckSize<DisplayConfigNative.LUID>("LUID", 8);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_PATH_SOURCE_INFO>("PATH_SOURCE_INFO", 20);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_PATH_TARGET_INFO>("PATH_TARGET_INFO", 48);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_PATH_INFO>("PATH_INFO", 72);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_VIDEO_SIGNAL_INFO>("VIDEO_SIGNAL_INFO", 48);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_TARGET_MODE>("TARGET_MODE", 48);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_SOURCE_MODE>("SOURCE_MODE", 20);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_DESKTOP_IMAGE_INFO>("DESKTOP_IMAGE_INFO", 40);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_MODE_INFO>("MODE_INFO", 64);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_DEVICE_INFO_HEADER>("DEVICE_INFO_HEADER", 20);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_TARGET_DEVICE_NAME>("TARGET_DEVICE_NAME", 420);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME>("SOURCE_DEVICE_NAME", 84);
        CheckSize<DisplayConfigNative.DISPLAYCONFIG_TARGET_PREFERRED_MODE>("TARGET_PREFERRED_MODE", 80);
        CheckSize<NativeMethods.DISPLAY_DEVICE>("DISPLAY_DEVICE", 840);
        CheckSize<NativeMethods.DEVMODE>("DEVMODE", 220);
        CheckSize<PROPERTYKEY>("PROPERTYKEY", 20);
        CheckSize<PROPVARIANT>("PROPVARIANT", 24);

        CheckFlag("SDC_APPLY", DisplayConfigNative.SDC_APPLY, 0x80);
        CheckFlag("SDC_SAVE_TO_DATABASE", DisplayConfigNative.SDC_SAVE_TO_DATABASE, 0x200);
        CheckFlag("SDC_ALLOW_CHANGES", DisplayConfigNative.SDC_ALLOW_CHANGES, 0x400);
        CheckFlag("SDC_PATH_PERSIST_IF_REQUIRED", DisplayConfigNative.SDC_PATH_PERSIST_IF_REQUIRED, 0x800);
        CheckFlag("SDC_USE_SUPPLIED_DISPLAY_CONFIG", DisplayConfigNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG, 0x20);
        CheckFlag("SDC_TOPOLOGY_CLONE", DisplayConfigNative.SDC_TOPOLOGY_CLONE, 0x2);
        CheckFlag("QDC_DATABASE_CURRENT", DisplayConfigNative.QDC_DATABASE_CURRENT, 0x4);

        if (Failures.Count == 0)
        {
            Console.WriteLine("Interop layout checks: OK");
            return 0;
        }

        Console.Error.WriteLine("Interop layout checks FAILED:");
        foreach (var failure in Failures)
        {
            Console.Error.WriteLine(" - " + failure);
        }

        return 1;
    }

    private static void CheckSize<T>(string name, int expected) where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual == expected)
        {
            Console.WriteLine($"{name,-38} {actual,4} OK");
            return;
        }

        Failures.Add($"{name}: got {actual}, expected {expected}");
    }

    private static void CheckFlag(string name, uint actual, uint expected)
    {
        if (actual != expected)
        {
            Failures.Add($"{name}: got 0x{actual:X}, expected 0x{expected:X}");
        }
    }
}
