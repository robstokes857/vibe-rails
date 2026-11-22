// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Pty.Net
{
    /// <summary>
    /// The console host behind a pseudoterminal. It decides who answers terminal queries: the
    /// host built into Windows answers cursor-position and device-attribute queries itself and
    /// re-renders the program's output, while a bundled host passes output through unchanged and
    /// forwards those queries to whoever reads the pseudoterminal.
    /// </summary>
    public enum PtyConsoleHost
    {
        /// <summary>Not a Windows pseudoconsole (a Unix pty).</summary>
        None = 0,

        /// <summary>The ConPTY built into Windows: kernel32, hosted by conhost.exe.</summary>
        Inbox = 1,

        /// <summary>A bundled conpty.dll, hosted by the OpenConsole.exe beside it.</summary>
        Bundled = 2,
    }
}
