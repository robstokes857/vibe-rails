// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Pty.Net.Windows
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using static Pty.Net.Windows.NativeMethods;

    /// <summary>
    /// Provides a pty connection for windows machines using ConPTY.
    /// </summary>
    internal class PtyProvider : IPtyProvider
    {
        /// <inheritdoc/>
        public Task<IPtyConnection> StartTerminalAsync(
            PtyOptions options,
            TraceSource trace,
            CancellationToken cancellationToken)
        {
            return this.StartPseudoConsoleAsync(options, trace, cancellationToken);
        }

        private static string GetAppOnPath(string app, string cwd, IDictionary<string, string> env)
        {
            bool isWow64 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null;
            var windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            var sysnativePath = Path.Combine(windir, "Sysnative");
            var sysnativePathWithSlash = sysnativePath + Path.DirectorySeparatorChar;
            var system32Path = Path.Combine(windir, "System32");
            var system32PathWithSlash = system32Path + Path.DirectorySeparatorChar;

            try
            {
                // If we have an absolute path then we take it.
                if (Path.IsPathRooted(app))
                {
                    if (isWow64)
                    {
                        // If path is on system32, check sysnative first
                        if (app.StartsWith(system32PathWithSlash, StringComparison.OrdinalIgnoreCase))
                        {
                            var sysnativeApp = Path.Combine(sysnativePath, app.Substring(system32PathWithSlash.Length));
                            if (File.Exists(sysnativeApp))
                            {
                                return sysnativeApp;
                            }
                        }
                    }
                    else if (app.StartsWith(sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Change Sysnative to System32 if the OS is Windows but NOT WoW64. It's
                        // safe to assume that this was used by accident as Sysnative does not
                        // exist and will break in non-WoW64 environments.
                        return Path.Combine(system32Path, app.Substring(sysnativePathWithSlash.Length));
                    }

                    return app;
                }

                if (Path.GetDirectoryName(app) != string.Empty)
                {
                    // We have a directory and the directory is relative. Make the path absolute
                    // to the current working directory.
                    return Path.Combine(cwd, app);
                }
            }
            catch (ArgumentException)
            {
                throw new ArgumentException($"Invalid terminal app path '{app}'");
            }
            catch (PathTooLongException)
            {
                throw new ArgumentException($"Terminal app path '{app}' is too long");
            }

            string? pathEnvironment = (env != null && env.TryGetValue("PATH", out string? p) ? p : null)
                ?? Environment.GetEnvironmentVariable("PATH");

            if (string.IsNullOrWhiteSpace(pathEnvironment))
            {
                // No PATH environment. Make path absolute to the cwd
                return Path.Combine(cwd, app);
            }

            var paths = new List<string>(pathEnvironment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            if (isWow64)
            {
                // On Wow64, if %PATH% contains %WINDIR%\System32 but does not have %WINDIR%\Sysnative, add it before System32.
                // We do that to accomodate terminal app that VSCode may use. VSCode is a 64 bit app,
                // and to access 64 bit System32 from wow64 vsls-agent app, we need to go to sysnative.
                var indexOfSystem32 = paths.FindIndex(entry =>
                    string.Equals(entry, system32Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, system32PathWithSlash, StringComparison.OrdinalIgnoreCase));

                var indexOfSysnative = paths.FindIndex(entry =>
                    string.Equals(entry, sysnativePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase));

                if (indexOfSystem32 >= 0 && indexOfSysnative == -1)
                {
                    paths.Insert(indexOfSystem32, sysnativePath);
                }
            }

            // We have a simple file name. We get the path variable from the env
            // and try to find the executable on the path.
            foreach (string pathEntry in paths)
            {
                bool isPathEntryRooted;
                try
                {
                    isPathEntryRooted = Path.IsPathRooted(pathEntry);
                }
                catch (ArgumentException)
                {
                    // Ignore invalid entry on %PATH%
                    continue;
                }

                // The path entry is absolute.
                string fullPath = isPathEntryRooted ? Path.Combine(pathEntry, app) : Path.Combine(cwd, pathEntry, app);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                var withExtension = fullPath + ".com";
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }

                withExtension = fullPath + ".exe";
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }
            }

            // Not found on PATH. Make path absolute to the cwd
            return Path.Combine(cwd, app);
        }

        private static string GetEnvironmentString(IDictionary<string, string> environment)
        {
            string[] keys = new string[environment.Count];
            environment.Keys.CopyTo(keys, 0);

            string[] values = new string[environment.Count];
            environment.Values.CopyTo(values, 0);

            // Sort both by the keys
            // Windows 2000 requires the environment block to be sorted by the key.
            Array.Sort(keys, values, StringComparer.OrdinalIgnoreCase);

            // Create a list of null terminated "key=val" strings
            var result = new StringBuilder();
            for (int i = 0; i < environment.Count; ++i)
            {
                result.Append(keys[i]);
                result.Append('=');
                result.Append(values[i]);
                result.Append('\0');
            }

            // An extra null at the end indicates end of list.
            result.Append('\0');

            return result.ToString();
        }

        private Task<IPtyConnection> StartPseudoConsoleAsync(
           PtyOptions options,
           TraceSource trace,
           CancellationToken cancellationToken)
        {
            // Create the in/out pipes
            if (!CreatePipe(out SafePipeHandle inPipePseudoConsoleSide, out SafePipeHandle inPipeOurSide, null, 0))
            {
                throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
            }

            if (!CreatePipe(out SafePipeHandle outPipeOurSide, out SafePipeHandle outPipePseudoConsoleSide, null, 0))
            {
                throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
            }

            var coord = new Coord(options.Cols, options.Rows);
            var pseudoConsoleHandle = new SafePseudoConsoleHandle();

            // Create the Pseudo Console, using the pipes
            int hr = CreatePseudoConsole(coord, inPipePseudoConsoleSide.Handle, outPipePseudoConsoleSide.Handle, 0, out IntPtr hPC);

            // Remember the handle to prevent leakage
            if (hPC != IntPtr.Zero && hPC != INVALID_HANDLE_VALUE)
            {
                pseudoConsoleHandle.InitialSetHandle(hPC);
            }

            if (hr != S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            // Prepare the StartupInfoEx structure attached to the ConPTY.
            var startupInfo = default(STARTUPINFOEX);
            startupInfo.InitAttributeListAttachedToConPTY(pseudoConsoleHandle);
            IntPtr lpEnvironment = Marshal.StringToHGlobalUni(GetEnvironmentString(options.Environment));
            try
            {
                string app = GetAppOnPath(options.App, options.Cwd, options.Environment);
                string arguments = options.VerbatimCommandLine ?
                    WindowsArguments.FormatVerbatim(options.CommandLine) :
                    WindowsArguments.Format(options.CommandLine);

                var commandLine = new StringBuilder(app.Length + arguments.Length + 4);
                bool quoteApp = app.Contains(" ") && !app.StartsWith("\"") && !app.EndsWith("\"");
                if (quoteApp)
                {
                    commandLine.Append('"').Append(app).Append('"');
                }
                else
                {
                    commandLine.Append(app);
                }

                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    commandLine.Append(' ');
                    commandLine.Append(arguments);
                }

                var processInfo = default(PROCESS_INFORMATION);
                var processHandle = new SafeProcessHandle();
                var mainThreadHandle = new SafeThreadHandle();

                bool success = CreateProcess(
                    null,   // lpApplicationName
                    commandLine.ToString(),
                    null,   // lpProcessAttributes
                    null,   // lpThreadAttributes
                    false,  // bInheritHandles VERY IMPORTANT that this is false
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, // dwCreationFlags
                    lpEnvironment,
                    options.Cwd,
                    ref startupInfo,
                    out processInfo);

                int errorCode = 0;
                if (!success)
                {
                    errorCode = Marshal.GetLastWin32Error();
                }

                // Remember the handles to prevent leakage
                if (processInfo.hProcess != IntPtr.Zero && processInfo.hProcess != INVALID_HANDLE_VALUE)
                {
                    processHandle.InitialSetHandle(processInfo.hProcess);
                }

                if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != INVALID_HANDLE_VALUE)
                {
                    mainThreadHandle.InitialSetHandle(processInfo.hThread);
                }

                if (!success)
                {
                    var exception = new Win32Exception(errorCode);
                    throw new InvalidOperationException($"Could not start terminal process {commandLine.ToString()}: {exception.Message}", exception);
                }

                var connectionOptions = new PseudoConsoleConnection.PseudoConsoleConnectionHandles(
                    inPipePseudoConsoleSide,
                    outPipePseudoConsoleSide,
                    inPipeOurSide,
                    outPipeOurSide,
                    pseudoConsoleHandle,
                    processHandle,
                    processInfo.dwProcessId,
                    mainThreadHandle);

                var result = new PseudoConsoleConnection(connectionOptions);
                return Task.FromResult<IPtyConnection>(result);
            }
            finally
            {
                startupInfo.FreeAttributeList();
                if (lpEnvironment != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(lpEnvironment);
                }
            }
        }
    }
}
