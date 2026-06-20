using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace m_mslc_overlay.services
{
    public class SystemMonitor
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToULong()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        private ulong _lastSysIdle;
        private ulong _lastSysKernel;
        private ulong _lastSysUser;

        private Process _currentProcess;
        private TimeSpan _lastAppCpuTime;
        private DateTime _lastAppCheckTime;

        public SystemMonitor()
        {
            _currentProcess = Process.GetCurrentProcess();
            _lastAppCpuTime = _currentProcess.TotalProcessorTime;
            _lastAppCheckTime = DateTime.UtcNow;

            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                _lastSysIdle = idle.ToULong();
                _lastSysKernel = kernel.ToULong();
                _lastSysUser = user.ToULong();
            }
        }

        public (double sysCpu, double sysRamMb, double appCpu, double appRamMb) GetMetrics()
        {
            // Global RAM
            double sysRamMb = 0;
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                sysRamMb = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024.0 * 1024.0);
            }

            // Global CPU
            double sysCpu = 0;
            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                ulong currentSysIdle = idle.ToULong();
                ulong currentSysKernel = kernel.ToULong();
                ulong currentSysUser = user.ToULong();

                ulong sysIdleDiff = currentSysIdle - _lastSysIdle;
                ulong sysKernelDiff = currentSysKernel - _lastSysKernel;
                ulong sysUserDiff = currentSysUser - _lastSysUser;
                ulong sysTotalDiff = sysKernelDiff + sysUserDiff;

                if (sysTotalDiff > 0)
                {
                    sysCpu = (sysTotalDiff - sysIdleDiff) * 100.0 / sysTotalDiff;
                }

                _lastSysIdle = currentSysIdle;
                _lastSysKernel = currentSysKernel;
                _lastSysUser = currentSysUser;
            }

            // App RAM
            _currentProcess.Refresh();
            double appRamMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);

            // App CPU
            DateTime now = DateTime.UtcNow;
            TimeSpan currentAppCpuTime = _currentProcess.TotalProcessorTime;
            double appCpu = 0;
            
            TimeSpan cpuUsedMs = currentAppCpuTime - _lastAppCpuTime;
            TimeSpan totalMsPassed = now - _lastAppCheckTime;

            if (totalMsPassed.TotalMilliseconds > 0)
            {
                appCpu = (cpuUsedMs.TotalMilliseconds / totalMsPassed.TotalMilliseconds) * 100.0 / Environment.ProcessorCount;
            }

            _lastAppCpuTime = currentAppCpuTime;
            _lastAppCheckTime = now;

            if (sysCpu < 0) sysCpu = 0;
            if (appCpu < 0) appCpu = 0;

            return (sysCpu, sysRamMb, appCpu, appRamMb);
        }
    }
}
