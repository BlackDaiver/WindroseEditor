using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindroseEditor
{
    static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        static void Main()
        {
            // Add exe directory to DLL search path so rocksdb.dll is found
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            SetDllDirectory(exeDir);

            // Also try the parent folder (where the Python editor lives)
            string parentDir = Path.GetFullPath(Path.Combine(exeDir, ".."));
            if (File.Exists(Path.Combine(parentDir, "rocksdb.dll")))
                SetDllDirectory(parentDir);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
