using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace MK.Hq.Api;

internal static class WiaScannerWorker
{
    private const string Png = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const int ScannerDeviceType = 1;

    public static int Run(string[] args) => OperatingSystem.IsWindows() ? RunOnWindows(args) : 1;

    [SupportedOSPlatform("windows")]
    private static int RunOnWindows(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var exitCode = 1;
        var worker = new Thread(() =>
        {
            try
            {
                if (args is ["list"])
                    Console.WriteLine(JsonSerializer.Serialize(WithDevices(List)));
                else if (args is ["scan", var id, var path])
                    ScanWithRetry(id, path);
                else
                    throw new ArgumentException("Invalid scanner command");
                exitCode = 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Scanner failed (0x{e.HResult:X8}): {e.Message}");
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return exitCode;
    }

    [SupportedOSPlatform("windows")]
    private static void ScanWithRetry(string id, string path)
    {
        try { WithDevices(devices => Scan(devices, id, path)); }
        catch (COMException e) when (e.HResult is unchecked((int)0x800703E3) or unchecked((int)0x80210006))
        {
            Thread.Sleep(750);
            WithDevices(devices => Scan(devices, id, path));
        }
    }

    [SupportedOSPlatform("windows")]
    private static T WithDevices<T>(Func<object, T> action)
    {
        dynamic manager = Create("WIA.DeviceManager");
        dynamic? devices = null;
        try
        {
            devices = manager.DeviceInfos;
            return action((object)devices);
        }
        finally
        {
            Release(devices);
            Release(manager);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ScannerDevice[] List(object collection)
    {
        dynamic devices = collection;
        var result = new List<ScannerDevice>();
        for (var i = 1; i <= (int)devices.Count; i++)
        {
            dynamic info = devices[i];
            try
            {
                if ((int)info.Type != ScannerDeviceType) continue;
                dynamic properties = info.Properties;
                try
                {
                    dynamic name = properties["Name"];
                    try { result.Add(new ScannerDevice((string)info.DeviceID, (string)name.Value)); }
                    finally { Release(name); }
                }
                finally { Release(properties); }
            }
            finally { Release(info); }
        }
        return result.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static bool Scan(object collection, string id, string path)
    {
        dynamic devices = collection;
        for (var i = 1; i <= (int)devices.Count; i++)
        {
            dynamic info = devices[i];
            try
            {
                if ((int)info.Type != ScannerDeviceType || !string.Equals((string)info.DeviceID, id, StringComparison.OrdinalIgnoreCase)) continue;
                Transfer((object)info, path);
                return true;
            }
            finally { Release(info); }
        }
        throw new InvalidOperationException("The selected scanner is unavailable");
    }

    [SupportedOSPlatform("windows")]
    private static void Transfer(object scannerInfo, string path)
    {
        dynamic info = scannerInfo;
        dynamic? device = null, items = null, item = null, image = null, converted = null, converter = null;
        try
        {
            device = info.Connect();
            items = device.Items;
            item = items[1];
            image = item.Transfer(Png);
            if (image is null || (int)image.Width < 1 || (int)image.Height < 1)
                throw new InvalidOperationException("The scanner returned no image");
            if (!string.Equals((string)image.FormatID, Png, StringComparison.OrdinalIgnoreCase))
            {
                converter = Create("WIA.ImageProcess");
                converter.Filters.Add(converter.FilterInfos["Convert"].FilterID);
                converter.Filters[1].Properties["FormatID"].Value = Png;
                converted = converter.Apply(image);
            }
            (converted ?? image).SaveFile(path);
        }
        finally
        {
            Release(converted);
            Release(converter);
            Release(image);
            Release(item);
            Release(items);
            Release(device);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    [SupportedOSPlatform("windows")]
    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
    }
}
