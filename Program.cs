using System.Diagnostics;
using System.Text;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace skdl_new_2025_test_tool
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {

            //// To customize application configuration such as set high DPI settings or default font,
            //// see https://aka.ms/applicationconfiguration.
            //ApplicationConfiguration.Initialize();
            //Application.Run(new Form1());


            // 网络授权
            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine("Cannot find current executable path.");
                return;
            }


            Console.WriteLine($"Path: {exePath}");
            Console.WriteLine();

            if (NetworkAuthorization.CheckNetworkAccess(exePath))
            {
                Console.WriteLine("Network access already granted.");
            }
            else
            {
                Console.WriteLine("Granting network access...");
                try
                {
                    NetworkAuthorization.GrantNetworkAccessByNetsh(exePath, System.IO.Path.GetFileNameWithoutExtension(exePath));
                    Console.WriteLine("Network access granted successfully!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }


            //  程序一启动就给ffmpeg/ffprobe授权
            FirewallHelper.InitFFmpegAuthorization();

            Application.ThreadException += (s, e) =>
            File.AppendAllText("crash.log", $"[ThreadException] {e.Exception}\n");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            File.AppendAllText("crash.log", $"[UnhandledException] {e.ExceptionObject}\n");

            AntdUI.Config.TextRenderingHighQuality = true;
            AntdUI.Config.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Application.SetHighDpiMode(HighDpiMode.SystemAware); // 默认强制按100% dpi兼容性运行在各种显示屏上
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            //Application.Run(new CalibAndTestTool());

            // ================================================
            // 1. 显示启动画面
            // ================================================
            FrmSplash splash = new FrmSplash();
            splash.Show();
            // 强制刷新界面，确保图片能立即显示出来，而不是白板
            Application.DoEvents();

            Form1 mainForm = new Form1();

            // ================================================
            // 3. 主窗口准备就绪，关闭启动画面
            // ================================================
            splash.Close();
            splash.Dispose();

            // ================================================
            // 4. 运行主程序
            // ================================================
            // 此时 mainForm 已经加载完毕，Show 出来是秒开的
            Application.Run(mainForm);
        }
    }
}