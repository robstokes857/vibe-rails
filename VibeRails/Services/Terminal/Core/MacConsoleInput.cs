using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Relays macOS console input as terminal bytes, without Console.ReadKey interpreting an
/// unknown CSI report as Escape followed by text. Owns the input mode, not the descriptor.
/// </summary>
internal sealed class MacConsoleInput : IDisposable
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    // Darwin values: EINTR, EAGAIN, TCSANOW, and the VMIN/VTIME control-character slots.
    private const int Interrupted = 4;
    private const int WouldBlock = 35;
    private const int SetNow = 0;
    private const int MinimumCharacters = 16;
    private const int TimeoutDeciseconds = 17;
    private readonly int _descriptor;
    private Termios _original;
    private bool _disposed;

    internal MacConsoleInput(int descriptor)
    {
        _descriptor = descriptor;
        if (GetAttributes(descriptor, out _original) != 0)
            throw NativeError("read macOS console settings");

        var raw = _original;
        raw.ControlCharacters = (byte[])_original.ControlCharacters.Clone();
        MakeRaw(ref raw);
        // Only input is raw. Preserve the outer console's output processing for host messages.
        raw.OutputFlags = _original.OutputFlags;
        // Darwin VMIN/VTIME: read returns immediately, including when no key is pending.
        // The existing input loop owns cancellation, PTY-exit and resize polling.
        raw.ControlCharacters[MinimumCharacters] = 0;
        raw.ControlCharacters[TimeoutDeciseconds] = 0;
        if (SetAttributesRetryingInterrupt(descriptor, ref raw) != 0)
            throw NativeError("set macOS console input mode");
    }

    /// <summary>
    /// Opens only a real macOS console; other input paths retain their existing reader. The PTY and
    /// its recorded session already exist when this runs, so a console whose mode cannot be changed
    /// falls back to that reader with a warning instead of failing the launch outside its cleanup.
    /// </summary>
    internal static MacConsoleInput? OpenConsole()
    {
        if (!OperatingSystem.IsMacOS() || Console.IsInputRedirected)
            return null;
        try
        {
            return new(0);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "[Terminal] Raw macOS console input unavailable; using Console.ReadKey");
            return null;
        }
    }

    /// <summary>Reads available bytes without key translation, decoding, or waiting for input.</summary>
    internal int ReadAvailable(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = Read(_descriptor, buffer, (nuint)buffer.Length);
        if (count >= 0)
            return (int)count;
        if (Marshal.GetLastPInvokeError() is Interrupted or WouldBlock)
            return 0;
        throw NativeError("read macOS console input");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // TCSANOW restores without flushing queued input.
        if (SetAttributesRetryingInterrupt(_descriptor, ref _original) != 0)
            Log.Warning("[Terminal] Could not restore macOS console settings: {Error}", Marshal.GetLastPInvokeError());
    }

    private static int SetAttributesRetryingInterrupt(int descriptor, ref Termios attributes)
    {
        int result;
        do { result = SetAttributes(descriptor, SetNow, ref attributes); }
        while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);
        return result;
    }

    private static IOException NativeError(string action) =>
        new($"Could not {action}.", new Win32Exception(Marshal.GetLastPInvokeError()));

    // Darwin's termios ABI is shared by macOS arm64/x64. Do not use this layout on Linux.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Termios
    {
        internal nuint InputFlags;
        internal nuint OutputFlags;
        internal nuint ControlFlags;
        internal nuint LocalFlags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        internal byte[] ControlCharacters;
        internal nuint InputSpeed;
        internal nuint OutputSpeed;
    }

    [DllImport(LibSystem, EntryPoint = "tcgetattr", SetLastError = true)]
    internal static extern int GetAttributes(int descriptor, out Termios attributes);

    [DllImport(LibSystem, EntryPoint = "tcsetattr", SetLastError = true)]
    private static extern int SetAttributes(int descriptor, int action, ref Termios attributes);

    [DllImport(LibSystem, EntryPoint = "cfmakeraw")]
    private static extern void MakeRaw(ref Termios attributes);

    [DllImport(LibSystem, EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int descriptor, [Out] byte[] buffer, nuint count);
}
