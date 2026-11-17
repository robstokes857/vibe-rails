using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Relays macOS and Linux console input as terminal bytes, without Console.ReadKey interpreting
/// an unknown CSI report as Escape followed by text. Owns the input mode, not the descriptor.
/// </summary>
internal sealed class UnixConsoleInput : IDisposable
{
    // Same values on Darwin and Linux: EINTR and TCSANOW.
    private const int Interrupted = 4;
    private const int SetNow = 0;

    /// <summary>
    /// Room for either platform's struct termios (Darwin 72 bytes, Linux glibc 60). libc reads and
    /// writes the whole structure; this class touches only the fields <see cref="TermiosLayout"/>
    /// names, so no managed struct has to match two ABIs.
    /// </summary>
    internal const int AttributesSize = 128;

    private readonly int _descriptor;
    private readonly TermiosLayout _layout = TermiosLayout.Current;
    private readonly byte[] _original = new byte[AttributesSize];
    private bool _disposed;

    internal UnixConsoleInput(int descriptor)
    {
        _descriptor = descriptor;
        if (GetAttributes(descriptor, _original) != 0)
            throw NativeError("read console settings");

        var raw = (byte[])_original.Clone();
        MakeRaw(raw);
        // Only input is raw. Preserve the outer console's output processing for host messages.
        _original.AsSpan(_layout.OutputFlags, _layout.FlagSize).CopyTo(raw.AsSpan(_layout.OutputFlags));
        // VMIN/VTIME = 0: read returns immediately, including when no key is pending.
        // The existing input loop owns cancellation, PTY-exit and resize polling.
        raw[_layout.MinimumCharacters] = 0;
        raw[_layout.TimeoutDeciseconds] = 0;
        if (SetAttributesRetryingInterrupt(descriptor, raw) != 0)
            throw NativeError("set console input mode");
    }

    /// <summary>
    /// Opens only a real macOS or Linux console; other input paths retain their existing reader.
    /// The PTY and its recorded session already exist when this runs, so a console whose mode cannot
    /// be changed (or a libc that cannot be bound) falls back to that reader with a warning instead
    /// of failing the launch outside its cleanup.
    /// </summary>
    internal static UnixConsoleInput? OpenConsole()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) || Console.IsInputRedirected)
            return null;
        try
        {
            return new(0);
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning(ex, "[Terminal] Raw console input unavailable; using Console.ReadKey");
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
        var error = Marshal.GetLastPInvokeError();
        if (error == Interrupted || error == _layout.WouldBlock)
            return 0;
        throw NativeError("read console input");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // TCSANOW restores without flushing queued input.
        if (SetAttributesRetryingInterrupt(_descriptor, _original) != 0)
            Log.Warning("[Terminal] Could not restore console settings: {Error}", Marshal.GetLastPInvokeError());
    }

    private static int SetAttributesRetryingInterrupt(int descriptor, byte[] attributes)
    {
        int result;
        do { result = SetAttributes(descriptor, attributes); }
        while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);
        return result;
    }

    private static IOException NativeError(string action) =>
        new($"Could not {action}.", new Win32Exception(Marshal.GetLastPInvokeError()));

    /// <summary>
    /// struct termios for the shipped ABIs, from each platform's termios.h. Darwin (arm64/x64):
    /// four 8-byte tcflag_t, then c_cc[20] at byte 32 with VMIN 16 and VTIME 17. Linux glibc
    /// (x64/arm64): four 4-byte tcflag_t, c_line, then c_cc[32] at byte 17 with VTIME 5 and VMIN 6.
    /// Both are little-endian. Do not use the Linux layout for musl on other architectures unchecked.
    /// </summary>
    internal sealed record TermiosLayout(
        int FlagSize, int MinimumCharacters, int TimeoutDeciseconds, int WouldBlock,
        ulong Echo, ulong Canonical, ulong Signals)
    {
        internal static readonly TermiosLayout Darwin = new(8, 32 + 16, 32 + 17, 35, 0x8, 0x100, 0x80);
        internal static readonly TermiosLayout Linux = new(4, 17 + 6, 17 + 5, 11, 0x8, 0x2, 0x1);

        internal static TermiosLayout Current =>
            OperatingSystem.IsMacOS() ? Darwin
            : OperatingSystem.IsLinux() ? Linux
            : throw new PlatformNotSupportedException("Raw console input is for macOS and Linux.");

        /// <summary>Byte offset of c_oflag, the second tcflag_t.</summary>
        internal int OutputFlags => FlagSize;

        /// <summary>Byte offset of c_lflag, the fourth tcflag_t.</summary>
        internal int LocalFlags => 3 * FlagSize;

        internal ulong ReadLocalFlags(ReadOnlySpan<byte> termios) => FlagSize == 8
            ? MemoryMarshal.Read<ulong>(termios[LocalFlags..])
            : MemoryMarshal.Read<uint>(termios[LocalFlags..]);
    }

    internal static int GetAttributes(int descriptor, byte[] attributes) =>
        OperatingSystem.IsMacOS() ? DarwinLibc.GetAttributes(descriptor, attributes)
            : LinuxLibc.GetAttributes(descriptor, attributes);

    private static int SetAttributes(int descriptor, byte[] attributes) =>
        OperatingSystem.IsMacOS() ? DarwinLibc.SetAttributes(descriptor, SetNow, attributes)
            : LinuxLibc.SetAttributes(descriptor, SetNow, attributes);

    private static void MakeRaw(byte[] attributes)
    {
        if (OperatingSystem.IsMacOS()) DarwinLibc.MakeRaw(attributes);
        else LinuxLibc.MakeRaw(attributes);
    }

    private static nint Read(int descriptor, byte[] buffer, nuint count) =>
        OperatingSystem.IsMacOS() ? DarwinLibc.Read(descriptor, buffer, count)
            : LinuxLibc.Read(descriptor, buffer, count);

    // Byte arrays are blittable and pinned for the call, so libc writes straight into them.
    // Library names match Pty.Net's own bindings, which already load under Native AOT.
    private static class DarwinLibc
    {
        private const string Library = "/usr/lib/libSystem.B.dylib";

        [DllImport(Library, EntryPoint = "tcgetattr", SetLastError = true)]
        internal static extern int GetAttributes(int descriptor, [Out] byte[] attributes);

        [DllImport(Library, EntryPoint = "tcsetattr", SetLastError = true)]
        internal static extern int SetAttributes(int descriptor, int action, [In] byte[] attributes);

        [DllImport(Library, EntryPoint = "cfmakeraw")]
        internal static extern void MakeRaw([In, Out] byte[] attributes);

        [DllImport(Library, EntryPoint = "read", SetLastError = true)]
        internal static extern nint Read(int descriptor, [Out] byte[] buffer, nuint count);
    }

    private static class LinuxLibc
    {
        private const string Library = "libc.so.6";

        [DllImport(Library, EntryPoint = "tcgetattr", SetLastError = true)]
        internal static extern int GetAttributes(int descriptor, [Out] byte[] attributes);

        [DllImport(Library, EntryPoint = "tcsetattr", SetLastError = true)]
        internal static extern int SetAttributes(int descriptor, int action, [In] byte[] attributes);

        [DllImport(Library, EntryPoint = "cfmakeraw")]
        internal static extern void MakeRaw([In, Out] byte[] attributes);

        [DllImport(Library, EntryPoint = "read", SetLastError = true)]
        internal static extern nint Read(int descriptor, [Out] byte[] buffer, nuint count);
    }
}
