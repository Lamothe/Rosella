using System.Globalization;
using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// Minimal Vulkan enumeration: create an instance with the system loader and
/// ask which physical devices come back. No device is created, so nothing is
/// submitted; the question is only whether Haswell is enumerated at all.
/// </summary>
internal static unsafe class VulkanProbe
{
    private const string Lib = "libvulkan.so.1";
    private const uint StructureTypeApplicationInfo = 0;
    private const uint StructureTypeInstanceCreateInfo = 1;

    private enum VkPhysicalDeviceType
    {
        Other = 0,
        IntegratedGpu = 1,
        DiscreteGpu = 2,
        VirtualGpu = 3,
        Cpu = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        internal uint StructureType;
        internal nint Next;
        internal nint ApplicationName;
        internal uint ApplicationVersion;
        internal nint EngineName;
        internal uint EngineVersion;
        internal uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        internal uint StructureType;
        internal nint Next;
        internal uint Flags;
        internal nint ApplicationInfo;
        internal uint EnabledLayerCount;
        internal nint EnabledLayerNames;
        internal uint EnabledExtensionCount;
        internal nint EnabledExtensionNames;
    }

    [DllImport(Lib, EntryPoint = "vkCreateInstance")]
    private static extern int CreateInstance(ref VkInstanceCreateInfo createInfo, nint allocator, out nint instance);

    [DllImport(Lib, EntryPoint = "vkDestroyInstance")]
    private static extern void DestroyInstance(nint instance, nint allocator);

    [DllImport(Lib, EntryPoint = "vkEnumeratePhysicalDevices")]
    private static extern int EnumeratePhysicalDevices(nint instance, ref uint count, nint* devices);

    [DllImport(Lib, EntryPoint = "vkGetPhysicalDeviceProperties")]
    private static extern void GetPhysicalDeviceProperties(nint physicalDevice, byte* properties);

    internal static void Run(TextWriter output)
    {
        output.WriteLine("== Vulkan ==");
        output.WriteLine($"  VK_DRIVER_FILES  = {Environment.GetEnvironmentVariable("VK_DRIVER_FILES") ?? "(unset)"}");
        output.WriteLine($"  VK_ICD_FILENAMES = {Environment.GetEnvironmentVariable("VK_ICD_FILENAMES") ?? "(unset)"}");
        WriteIcdFiles(output);

        var application = "Rosella.GpuProbe"u8;
        var engine = "Rosella.GpuProbe"u8;
        fixed (byte* applicationName = application)
        fixed (byte* engineName = engine)
        {
            var applicationInfo = new VkApplicationInfo
            {
                StructureType = StructureTypeApplicationInfo,
                ApplicationName = (nint)applicationName,
                ApplicationVersion = 1,
                EngineName = (nint)engineName,
                EngineVersion = 1,
                ApiVersion = 1u << 22,
            };

            var createInfo = new VkInstanceCreateInfo
            {
                StructureType = StructureTypeInstanceCreateInfo,
                ApplicationInfo = (nint)(&applicationInfo),
            };

            var result = CreateInstance(ref createInfo, 0, out var instance);
            if (result != 0)
            {
                output.WriteLine($"  vkCreateInstance failed: VkResult {result}");
                return;
            }

            try
            {
                var count = 0u;
                result = EnumeratePhysicalDevices(instance, ref count, null);
                if (result != 0)
                {
                    output.WriteLine($"  vkEnumeratePhysicalDevices(count) failed: VkResult {result}");
                    return;
                }

                if (count == 0)
                {
                    output.WriteLine("  vkEnumeratePhysicalDevices: 0 physical devices (loader initialised, nothing enumerated)");
                    return;
                }

                var devices = stackalloc nint[checked((int)count)];
                result = EnumeratePhysicalDevices(instance, ref count, devices);
                if (result != 0)
                {
                    output.WriteLine($"  vkEnumeratePhysicalDevices(list) failed: VkResult {result}");
                    return;
                }

                output.WriteLine($"  {count} physical device(s):");
                var properties = stackalloc byte[1600];
                for (var i = 0; i < count; i++)
                {
                    new Span<byte>(properties, 1600).Clear();
                    GetPhysicalDeviceProperties(devices[i], properties);

                    var apiVersion = *(uint*)properties;
                    var driverVersion = *(uint*)(properties + 4);
                    var vendorId = *(uint*)(properties + 8);
                    var deviceId = *(uint*)(properties + 12);
                    var deviceType = *(uint*)(properties + 16);
                    var name = Marshal.PtrToStringUTF8((nint)(properties + 20)) ?? "(unnamed)";

                    output.WriteLine(
                        $"    [{i}] {name} | type={(VkPhysicalDeviceType)deviceType} | api={Decode(apiVersion)} | driver=0x{driverVersion:X8} | vendor=0x{vendorId:X4} device=0x{deviceId:X4}");
                }
            }
            finally
            {
                DestroyInstance(instance, 0);
            }
        }
    }

    private static string Decode(uint version)
        => string.Create(CultureInfo.InvariantCulture, $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}");

    private static void WriteIcdFiles(TextWriter output)
    {
        const string Directory = "/usr/share/vulkan/icd.d";
        if (!System.IO.Directory.Exists(Directory))
        {
            output.WriteLine($"  {Directory}: absent");
            return;
        }

        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json").Order(StringComparer.Ordinal))
        {
            output.WriteLine($"  ICD {System.IO.Path.GetFileName(file)}");
        }
    }
}