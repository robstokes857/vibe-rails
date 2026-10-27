// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Pty.Net.Mac
{
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using static Pty.Net.Mac.NativeMethods;

    /// <summary>
    /// A connection to a pseudoterminal on MacOS machines.
    /// </summary>
    internal class PtyConnection : Unix.PtyConnection
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PtyConnection"/> class.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="pid">The id of the spawned process.</param>
        public PtyConnection(int controller, int pid)
            : base(controller, pid)
        {
        }

        /// <inheritdoc/>
        protected override bool Kill(int fd)
        {
            // ioctl is variadic on Darwin/arm64; passing its signal as a fixed P/Invoke
            // argument can produce EINVAL. Match the Unix process-based hangup instead.
            // Disposal can follow an observed exit, so an already-gone child is success.
            if (this.WaitForExit(0)) return true;
            return kill(this.Pid, SIGHUP) != -1 || Marshal.GetLastWin32Error() == 3; // ESRCH
        }

        /// <inheritdoc/>
        protected override bool KillProcessGroup(int pid)
        {
            // Negative pid addresses the whole process group (kill(2)).
            return kill(-pid, SIGKILL) != -1;
        }

        /// <inheritdoc/>
        protected override bool Resize(int fd, int cols, int rows)
        {
            var size = new WinSize((ushort)rows, (ushort)cols);
            return ioctl(fd, TIOCSWINSZ, ref size) != -1;
        }

        /// <inheritdoc/>
        protected override bool WaitPid(int pid, ref int status)
        {
            return waitpid(pid, ref status, 0) != -1;
        }
    }
}
