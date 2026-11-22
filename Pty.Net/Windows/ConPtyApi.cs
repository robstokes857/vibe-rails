// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Pty.Net.Windows
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using static Pty.Net.Windows.NativeMethods;

    /// <summary>
    /// The three pseudoconsole calls, taken from whichever console host a terminal uses: the
    /// ConPTY built into Windows (kernel32, hosted by conhost.exe) or a bundled conpty.dll, which
    /// starts the OpenConsole.exe beside it. A handle is resized and closed only by the API that
    /// created it; the two hosts' handles are not interchangeable.
    /// </summary>
    internal abstract class ConPtyApi
    {
        /// <summary>The executable a bundled conpty.dll starts. It looks beside itself first.</summary>
        internal const string HostExecutableName = "OpenConsole.exe";

        /// <summary>The ConPTY built into Windows.</summary>
        internal static readonly ConPtyApi Inbox = new InboxApi();

        // A loaded library is kept for the life of the process: pseudoconsoles created through it
        // may be closed at any later time, including during shutdown.
        private static readonly Dictionary<string, BundledApi> Loaded = new(StringComparer.OrdinalIgnoreCase);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CreatePseudoConsoleDelegate(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ResizePseudoConsoleDelegate(IntPtr handle, Coord size);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void ClosePseudoConsoleDelegate(IntPtr handle);

        /// <summary>Gets the console host this API creates pseudoconsoles under.</summary>
        internal abstract PtyConsoleHost Host { get; }

        /// <summary>Creates a pseudoconsole on the given pipe ends. Returns an HRESULT.</summary>
        internal abstract int Create(Coord size, IntPtr input, IntPtr output, out IntPtr handle);

        /// <summary>Resizes a pseudoconsole this API created. Returns an HRESULT.</summary>
        internal abstract int Resize(SafePseudoConsoleHandle handle, Coord size);

        /// <summary>Closes a pseudoconsole this API created.</summary>
        internal abstract void Close(IntPtr handle);

        /// <summary>
        /// Loads a bundled conpty.dll by its absolute path. Returns <c>null</c> with the reason
        /// when the pair of files is not usable; it never throws, so an optional host cannot fail
        /// a terminal that the built-in host would have started.
        /// </summary>
        /// <remarks>
        /// OpenConsole.exe is checked here, before the library is asked for anything: a conpty.dll
        /// that cannot find it starts conhost.exe and still reports success, which would leave
        /// the caller believing it has the pass-through host.
        /// </remarks>
        internal static ConPtyApi? TryLoadBundled(string libraryPath, out string? failure)
        {
            try
            {
                // Never a search: a relative path or bare name could resolve against the working
                // directory, which is usually the repository the terminal was opened for.
                if (string.IsNullOrWhiteSpace(libraryPath) || !Path.IsPathFullyQualified(libraryPath))
                {
                    failure = "the console host path is not an absolute path";
                    return null;
                }

                string fullPath = Path.GetFullPath(libraryPath);
                if (!File.Exists(fullPath))
                {
                    failure = $"{Path.GetFileName(fullPath)} was not found at {fullPath}";
                    return null;
                }

                string hostPath = Path.Combine(Path.GetDirectoryName(fullPath)!, HostExecutableName);
                if (!File.Exists(hostPath))
                {
                    failure = $"{HostExecutableName} is missing beside {Path.GetFileName(fullPath)} at {hostPath}";
                    return null;
                }

                lock (Loaded)
                {
                    if (Loaded.TryGetValue(fullPath, out BundledApi? known))
                    {
                        failure = null;
                        return known;
                    }

                    if (!NativeLibrary.TryLoad(fullPath, out IntPtr library))
                    {
                        failure = $"{fullPath} could not be loaded (wrong architecture or a damaged file)";
                        return null;
                    }

                    if (!NativeLibrary.TryGetExport(library, "ConptyCreatePseudoConsole", out IntPtr create)
                        || !NativeLibrary.TryGetExport(library, "ConptyResizePseudoConsole", out IntPtr resize)
                        || !NativeLibrary.TryGetExport(library, "ConptyClosePseudoConsole", out IntPtr close))
                    {
                        NativeLibrary.Free(library);
                        failure = $"{fullPath} does not export the Conpty pseudoconsole functions";
                        return null;
                    }

                    var api = new BundledApi(
                        Marshal.GetDelegateForFunctionPointer<CreatePseudoConsoleDelegate>(create),
                        Marshal.GetDelegateForFunctionPointer<ResizePseudoConsoleDelegate>(resize),
                        Marshal.GetDelegateForFunctionPointer<ClosePseudoConsoleDelegate>(close));
                    Loaded[fullPath] = api;
                    failure = null;
                    return api;
                }
            }
            catch (Exception ex)
            {
                failure = $"loading the console host failed: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private sealed class InboxApi : ConPtyApi
        {
            internal override PtyConsoleHost Host => PtyConsoleHost.Inbox;

            internal override int Create(Coord size, IntPtr input, IntPtr output, out IntPtr handle) =>
                CreatePseudoConsole(size, input, output, 0, out handle);

            internal override int Resize(SafePseudoConsoleHandle handle, Coord size) =>
                ResizePseudoConsole(handle, size);

            internal override void Close(IntPtr handle) => ClosePseudoConsole(handle);
        }

        private sealed class BundledApi : ConPtyApi
        {
            private readonly CreatePseudoConsoleDelegate create;
            private readonly ResizePseudoConsoleDelegate resize;
            private readonly ClosePseudoConsoleDelegate close;

            internal BundledApi(
                CreatePseudoConsoleDelegate create,
                ResizePseudoConsoleDelegate resize,
                ClosePseudoConsoleDelegate close)
            {
                this.create = create;
                this.resize = resize;
                this.close = close;
            }

            internal override PtyConsoleHost Host => PtyConsoleHost.Bundled;

            internal override int Create(Coord size, IntPtr input, IntPtr output, out IntPtr handle) =>
                this.create(size, input, output, 0, out handle);

            internal override int Resize(SafePseudoConsoleHandle handle, Coord size)
            {
                // The delegate takes the raw handle, so hold a reference across the call the way
                // SafeHandle marshalling does for the kernel32 import.
                bool referenced = false;
                try
                {
                    handle.DangerousAddRef(ref referenced);
                    return this.resize(handle.DangerousGetHandle(), size);
                }
                finally
                {
                    if (referenced)
                    {
                        handle.DangerousRelease();
                    }
                }
            }

            internal override void Close(IntPtr handle) => this.close(handle);
        }
    }
}
