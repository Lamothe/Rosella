using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rosella.Compositor;

internal static class ClientProcess
{
    private const int SigTerm = 15;
    private const short PollIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "pidfd_send_signal")]
    private static extern int SendSignal(SafeFileHandle pidFd, int signal, IntPtr info, uint flags);

    [DllImport("libc", EntryPoint = "poll")]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeoutMilliseconds);

    internal static bool IsChatGpt(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            return File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.FullName
                == "/usr/lib/chatgpt/ChatGPT";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool Terminate(SafeFileHandle process)
    {
        try
        {
            return SendSignal(process, SigTerm, IntPtr.Zero, 0) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool HasExited(SafeFileHandle process, int timeoutMilliseconds = 0)
    {
        var descriptor = new PollDescriptor { Fd = (int)process.DangerousGetHandle(), Events = PollIn };
        return Poll(ref descriptor, 1, timeoutMilliseconds) > 0 && (descriptor.ReturnedEvents & PollIn) != 0;
    }
}
