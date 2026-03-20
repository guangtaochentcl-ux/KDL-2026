using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using VideoCapture_uvc;

namespace skdl_new_2025_test_tool
{
    class WindowsFunc
    {
        public static string executeCMDCommand(string command)
        {
            Process process_cmd = new Process();
            //Console.WriteLine(command);
            string output_string = null;
            try
            {
                process_cmd.StartInfo.FileName = "cmd.exe";
                process_cmd.StartInfo.RedirectStandardInput = true;
                process_cmd.StartInfo.RedirectStandardOutput = true;
                process_cmd.StartInfo.CreateNoWindow = true;
                process_cmd.StartInfo.UseShellExecute = false;
                process_cmd.Start();
                process_cmd.StandardInput.WriteLine(command + "&exit");
                process_cmd.StandardInput.AutoFlush = true;
                output_string = process_cmd.StandardOutput.ReadToEnd();
            }
            catch (Exception ex)
            {
                output_string = ex.ToString();
            }
            finally
            {
                process_cmd.WaitForExit();
                process_cmd.Close();
            }
            return output_string;
        }
        public static async Task<string> executeCMDCommand_backGround(string command)
        {
            string output_string = null;
            await Task.Run(() =>
            {
                Process process_cmd = new Process();
                try
                {
                    process_cmd.StartInfo.FileName = "cmd.exe";
                    process_cmd.StartInfo.RedirectStandardInput = true;
                    process_cmd.StartInfo.RedirectStandardOutput = true;
                    process_cmd.StartInfo.CreateNoWindow = true;
                    process_cmd.StartInfo.UseShellExecute = false;
                    process_cmd.Start();
                    process_cmd.StandardInput.WriteLine(command + "&exit");
                    process_cmd.StandardInput.AutoFlush = true;
                    output_string = process_cmd.StandardOutput.ReadToEnd();
                }
                catch (Exception ex)
                {
                    output_string = ex.ToString();
                }
                finally
                {
                    process_cmd.WaitForExit(10000);
                    process_cmd.Close();
                }
            });
            return output_string;
        }

        public static async Task executeCMDCommand_RealTime(string command, Action<string> onOutput)
        {
            await Task.Run(() =>
            {
                Process process_cmd = new Process();
                try
                {
                    process_cmd.StartInfo.FileName = "cmd.exe";

                    // 为了读取输出，必须重定向
                    process_cmd.StartInfo.RedirectStandardInput = true;
                    process_cmd.StartInfo.RedirectStandardOutput = true;
                    process_cmd.StartInfo.RedirectStandardError = true; // 建议同时也捕获错误输出，因为Python报错通常在这里
                    process_cmd.StartInfo.UseShellExecute = false;

                    // 注意：重定向输出后，CMD窗口内将不会显示文字。
                    // 建议隐藏窗口，完全由你的程序接管显示。
                    process_cmd.StartInfo.CreateNoWindow = true;

                    // 绑定输出事件
                    process_cmd.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            onOutput(e.Data); // 实时回调输出内容
                        }
                    };

                    // 绑定错误事件（Python脚本的报错通常在这里）
                    process_cmd.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            onOutput("ERROR: " + e.Data); // 实时回调错误内容
                        }
                    };

                    process_cmd.Start();

                    // 开始异步读取
                    process_cmd.BeginOutputReadLine();
                    process_cmd.BeginErrorReadLine();

                    // 写入命令
                    // 注意：对于Python脚本，建议使用 python -u script.py 
                    // -u 参数能禁止缓冲区，让输出更实时
                    process_cmd.StandardInput.WriteLine(command + "&exit");
                    process_cmd.StandardInput.AutoFlush = true;

                    // 等待退出
                    process_cmd.WaitForExit();
                }
                catch (Exception ex)
                {
                    onOutput("EXCEPTION: " + ex.ToString());
                }
                finally
                {
                    process_cmd.Close();
                }
            });
        }

        // 删除文件夹里面的文件，不删除文件夹本身
        public static void DeleteDir1(string file)
        {
            try
            {
                //去除文件夹和子文件的只读属性
                //去除文件夹的只读属性
                System.IO.DirectoryInfo fileInfo = new DirectoryInfo(file);
                fileInfo.Attributes = FileAttributes.Normal & FileAttributes.Directory;
                //去除文件的只读属性
                System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
                //判断文件夹是否还存在
                if (Directory.Exists(file))
                {
                    foreach (string f in Directory.GetFileSystemEntries(file))
                    {
                        if (File.Exists(f))
                        {
                            //如果有子文件删除文件
                            File.Delete(f);
                            Console.WriteLine(f);
                        }
                        else
                        {
                            //循环递归删除子文件夹
                            DeleteDir1(f);
                        }
                    }
                }
            }
            catch (Exception ex) // 异常处理
            {
                Console.WriteLine(ex.Message.ToString());// 异常信息
            }
        }


        public static SerialPort comm = new SerialPort();
        public static string port_id = "";
        public static string baudrate = "";
        public static bool OpenSerialPort()
        {
            //关闭时点击，则设置好端口，波特率后打开
            try
            {
                if (port_id != null)
                {
                    comm.PortName = port_id.ToString(); //串口名 COM1
                    comm.BaudRate = int.Parse(baudrate.ToString()); //波特率  9600
                    comm.DataBits = 8; // 数据位 8
                    comm.ReadBufferSize = 4096;
                    comm.StopBits = StopBits.One;
                    comm.Parity = Parity.None;
                    comm.Open();
                }

            }
            catch (Exception ex)
            {
                //捕获到异常信息，创建一个新的comm对象，之前的不能用了。
                comm = new SerialPort();
                //现实异常信息给客户。
                //Application.Exit();
                return false;
            }
            return true;
        }

        //public static byte[] sendinfo(byte[] info)
        //{
        //    if (comm == null)
        //    {
        //        comm = new SerialPort();
        //        return null;
        //    }

        //    if (comm.IsOpen == false)
        //    {
        //        OpenSerialPort();
        //        return null;
        //    }
        //    try
        //    {
        //        byte[] data = new byte[2048];
        //        int len = 0;
        //        comm.Write(info, 0, info.Length);
        //        try
        //        {
        //            Thread.Sleep(50);
        //            Stream ns = comm.BaseStream;
        //            ns.ReadTimeout = 50;
        //            len = ns.Read(data, 0, 2048);
        //        }
        //        catch (Exception)
        //        {
        //            return null;
        //        }
        //    }
        //    catch (Exception)
        //    {

        //    }
        //    return null;
        //}

        static int errrcvcnt = 0;
        public static byte[] sendinfo(byte[] info)
        {
            if (comm == null)
            {
                comm = new SerialPort();
                return null;
            }

            if (comm.IsOpen == false)
            {
                OpenSerialPort();
                return null;
            }
            try
            {
                byte[] data = new byte[2048];
                int len = 0;

                comm.Write(info, 0, info.Length);

                try
                {
                    Thread.Sleep(50);
                    Stream ns = comm.BaseStream;
                    ns.ReadTimeout = 50;
                    len = ns.Read(data, 0, 2048);

                }
                catch (Exception)
                {
                    return null;
                }
                errrcvcnt = 0;
                return analysisRcv(data, len);
            }
            catch (Exception)
            {

            }
            return null;
        }

        private static byte[] analysisRcv(byte[] src, int len)
        {
            if (len < 6) return null;
            if (src[0] != Convert.ToInt16("254")) return null;

            switch (src[1])
            {
                case 0x01:
                    if (CMBRTU.CalculateCrc(src, src[2] + 5) == 0x00)
                    {
                        byte[] dst = new byte[src[2]];
                        for (int i = 0; i < src[2]; i++)
                            dst[i] = src[3 + i];
                        return dst;
                    }
                    break;
                case 0x02:
                    if (CMBRTU.CalculateCrc(src, src[2] + 5) == 0x00)
                    {
                        byte[] dst = new byte[src[2]];
                        for (int i = 0; i < src[2]; i++)
                            dst[i] = src[3 + i];
                        return dst;
                    }
                    break;
                case 0x04:
                    if (CMBRTU.CalculateCrc(src, src[2] + 5) == 0x00)
                    {
                        byte[] dst = new byte[src[2]];
                        for (int i = 0; i < src[2]; i++)
                            dst[i] = src[3 + i];
                        return dst;
                    }
                    break;
                case 0x05:
                    if (CMBRTU.CalculateCrc(src, 8) == 0x00)
                    {
                        byte[] dst = new byte[1];
                        dst[0] = src[4];
                        return dst;
                    }
                    break;
                case 0x0f:
                    if (CMBRTU.CalculateCrc(src, 8) == 0x00)
                    {
                        byte[] dst = new byte[1];
                        dst[0] = 1;
                        return dst;
                    }
                    break;
                case 0x06:
                    if (CMBRTU.CalculateCrc(src, 8) == 0x00)
                    {
                        byte[] dst = new byte[4];
                        dst[0] = src[2];
                        dst[1] = src[3];
                        dst[2] = src[4];
                        dst[3] = src[5];
                        return dst;
                    }
                    break;
                case 0x10:
                    if (CMBRTU.CalculateCrc(src, 8) == 0x00)
                    {
                        byte[] dst = new byte[4];
                        dst[0] = src[2];
                        dst[1] = src[3];
                        dst[2] = src[4];
                        dst[3] = src[5];
                        return dst;
                    }
                    break;
            }
            return null;
        }

        public static bool readDO(string DoNum)
        {
            bool isOpen = false;
            Console.WriteLine(Convert.ToInt16("254"));
            Console.WriteLine(DoNum);
            byte[] info = CModbusDll.ReadDO(Convert.ToInt16("254"), Convert.ToInt16(DoNum));
            string hexLog = BitConverter.ToString(info).Replace("-", " ");
            Console.WriteLine($"TX - {hexLog}");
            byte[] rst = sendinfo(info);
            Thread.Sleep(100);
            Console.WriteLine(rst.Length.ToString());
            if (rst == null) return false;
            for (int j = 0; j < rst.Length & j < 4; j++)
            {
                byte status = rst[j];
                for (int i = 0; i < 8; i++)
                {
                    if ((status & (1 << i)) == 0x00)
                        isOpen = false;
                    else
                        isOpen = true;
                }
            }
            return isOpen;

        }


        public static Dictionary<string, string> GetPortDeviceName()
        {
            Dictionary<string, string> comlistDict = new Dictionary<string, string>();
            RegistryKey keyCom = Registry.LocalMachine.OpenSubKey("Hardware\\DeviceMap\\SerialComm");
            if (keyCom != null)
            {
                string[] sSubKeys = keyCom.GetValueNames();
                foreach (string sName in sSubKeys)
                {
                    string sValue = (string)keyCom.GetValue(sName);
                    comlistDict.Add(sName, sValue);
                }
            }
            return comlistDict;
        }

        public static List<string> getAllFtp_LDCFile(string cur_ip)
        {
            Console.WriteLine("稍等1秒，设备获取数据中，误点击窗口……");
            Thread.Sleep(1000);
            List<string> picList = new List<string>();
            string ftpUrl = $"ftp://{cur_ip}//ldc//";
            string username = "ftp_username";
            string password = "ftp_password";

            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);

            FtpWebResponse response = (FtpWebResponse)request.GetResponse();
            //Console.WriteLine("Directory List:");
            //Console.WriteLine();

            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
            {
                while (!reader.EndOfStream)
                {
                    string fileName = reader.ReadLine();
                    if (fileName.Contains("yuv"))
                    {
                        picList.Add(fileName);
                    }
                    //Console.WriteLine(fileName);
                }
            }
            response.Close();
            return picList;
        }

        public static  List<string> getAllFtp_TelePhotoFile(string cur_ip)
        {
            Console.WriteLine("稍等1秒，设备获取数据中，误点击窗口……");
            Thread.Sleep(1000);
            List<string> picList = new List<string>();
            string ftpUrl = $"ftp://{cur_ip}//";
            string username = "ftp_username";
            string password = "ftp_password";

            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);

            FtpWebResponse response = (FtpWebResponse)request.GetResponse();
            //Console.WriteLine("Directory List:");
            //Console.WriteLine();

            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
            {
                while (!reader.EndOfStream)
                {
                    string fileName = reader.ReadLine();
                    if (fileName.Contains("yuv"))
                    {
                        picList.Add(fileName);
                    }
                    //Console.WriteLine(fileName);
                }
            }
            response.Close();
            return picList;
        }


        public static async void extractPicture(string cur_ip, string fileName, string extractLocalPath)
        {
            string ftpUrl = $"ftp://{cur_ip}/{fileName}";
            string localFilePath = extractLocalPath;
            string username = "ftp_username";
            string password = "ftp_password";

            try
            {
                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
                request.Credentials = new NetworkCredential(username, password);
                request.Method = WebRequestMethods.Ftp.DownloadFile;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (FileStream fileStream = File.Create(localFilePath))
                {
                    byte[] buffer = new byte[1024];
                    int bytesRead;
                    do
                    {
                        bytesRead = responseStream.Read(buffer, 0, buffer.Length);
                        fileStream.Write(buffer, 0, bytesRead);
                    } while (bytesRead > 0);
                }
                request = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"取出图片{fileName}失败 错误: {ex.Message},请重启设备重新标定！");
                await Task.Delay(5000);
                extractPicture(cur_ip, fileName, extractLocalPath);
                return;
            }
        }

        public static async void extractLdcFile(string cur_ip, string fileName, string extractLocalPath)
        {
            string ftpUrl = $"ftp://{cur_ip}/ldc/{fileName}";
            string localFilePath = extractLocalPath;
            string username = "ftp_username";
            string password = "ftp_password";

            try
            {
                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
                request.Credentials = new NetworkCredential(username, password);
                request.Method = WebRequestMethods.Ftp.DownloadFile;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (FileStream fileStream = File.Create(localFilePath))
                {
                    byte[] buffer = new byte[1024];
                    int bytesRead;
                    do
                    {
                        bytesRead = responseStream.Read(buffer, 0, buffer.Length);
                        fileStream.Write(buffer, 0, bytesRead);
                    } while (bytesRead > 0);
                }
                request = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"取出文件{fileName}失败 错误: {ex.Message},请重启设备重新标定！");
            }
        }

        public static void shixi_convertYuvToPng(string dataYuvPath)
        {
            string shiXiConvertExe = ".\\shixi_convert_image_yuv420_cli.exe";
            Process myPro = new Process();
            myPro.StartInfo.FileName = shiXiConvertExe;
            myPro.StartInfo.Arguments = "-i " + dataYuvPath + " -c";
            myPro.StartInfo.UseShellExecute = false;
            myPro.StartInfo.RedirectStandardInput = true;
            myPro.StartInfo.RedirectStandardError = true;
            myPro.StartInfo.RedirectStandardOutput = true;
            myPro.StartInfo.CreateNoWindow = true;
            myPro.Start();
            myPro.WaitForExit();
            string content = myPro.StandardOutput.ReadToEnd();
            if (myPro.ExitCode == 0 && content != "")
            {
                Console.WriteLine(content);
                try
                {
                    Console.WriteLine("Convert done！");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Convert Fail, please check!");
                }
            }
        }

        public static void shixi_convertRawToPng(string dataRawPath)
        {
            string shiXiConvertExe = ".\\shixi_convert_image_raw_cli.exe";
            Process myPro = new Process();
            myPro.StartInfo.FileName = shiXiConvertExe;
            myPro.StartInfo.Arguments = "-i " + dataRawPath + " -r -c";
            myPro.StartInfo.UseShellExecute = false;
            myPro.StartInfo.RedirectStandardInput = true;
            myPro.StartInfo.RedirectStandardError = true;
            myPro.StartInfo.RedirectStandardOutput = true;
            myPro.StartInfo.CreateNoWindow = true;
            myPro.Start();
            myPro.WaitForExit();
            string content = myPro.StandardOutput.ReadToEnd();
            if (myPro.ExitCode == 0 && content != "")
            {
                Console.WriteLine(content);
                try
                {
                    Console.WriteLine("Convert done！");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Convert Fail, please check!");
                }
            }
        }

        public static async Task<bool> IsHostReachableAsync(string ipAddress)
        {
            using (Ping ping = new Ping())
            {
                try
                {
                    Console.WriteLine($"Try connect with dev : {ipAddress}^^^^^");
                    PingReply reply = await ping.SendPingAsync(ipAddress);
                    return reply.Status == IPStatus.Success;
                    await Task.Delay(1000);
                }
                catch (PingException)
                {
                    return false;
                }
                ping.Dispose();
            }
        }

        public static void Convert10BitPackedToUnpacked16Bit(string inputFileName, uint width, uint height, uint stride)
        {
            uint pairs = width / 4;
            uint padding = stride - pairs * 5;
            string outputFileName = inputFileName + "_unpacked.raw";
            byte[] inputBuffer = new byte[(pairs * 5 + padding) * height];
            byte[] outputBuffer = new byte[width * height * 2];

            using (FileStream inputFileStream = new FileStream(inputFileName, FileMode.Open, FileAccess.Read))
            {
                if (inputFileStream.Length < inputBuffer.Length)
                {
                    throw new Exception(inputFileName + " File length not correct.");
                }
                inputFileStream.Read(inputBuffer, 0, inputBuffer.Length);
            }

            Console.WriteLine("Converting...{0}", inputFileName);
            using (MemoryStream outputMemoryStream = new MemoryStream(outputBuffer))
            {
                using (BinaryWriter outputBinaryWriter = new BinaryWriter(outputMemoryStream))
                {
                    ulong inputOffset = 0;
                    for (int i = 0; i < height; i++)
                    {
                        for (int j = 0; j < pairs; j++)
                        {
                            outputBinaryWriter.Write((ushort)(((inputBuffer[inputOffset + 1]) & 0x03) << 8 | inputBuffer[inputOffset]));
                            outputBinaryWriter.Write((ushort)(((inputBuffer[inputOffset + 2]) & 0x0F) << 6 | inputBuffer[inputOffset + 1] >> 2));
                            outputBinaryWriter.Write((ushort)(((inputBuffer[inputOffset + 3]) & 0x3F) << 4 | inputBuffer[inputOffset + 2] >> 4));
                            outputBinaryWriter.Write((ushort)(((inputBuffer[inputOffset + 4]) & 0xFF) << 2 | inputBuffer[inputOffset + 3] >> 6));
                            inputOffset += 5;
                        }
                        inputOffset += padding;
                    }
                }
            }

            using (FileStream outputFileStream = new FileStream(outputFileName, FileMode.Create, FileAccess.Write))
            {
                outputFileStream.Write(outputBuffer, 0, outputBuffer.Length);
            }
            File.Delete(inputFileName);
        }

        public static List<string> getAllFtp_RawFile(string ipAddress)
        {
            Console.WriteLine("稍等1秒，设备获取数据中，误点击窗口……\n");
            Thread.Sleep(1000);
            List<string> picList = new List<string>();
            string ftpUrl = $"ftp://{ipAddress}/raw/";
            string username = "ftp_username";
            string password = "ftp_password";

            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);

            FtpWebResponse response = (FtpWebResponse)request.GetResponse();
            Console.WriteLine("Directory List:");
            Console.WriteLine();

            using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
            {
                while (!reader.EndOfStream)
                {
                    string fileName = reader.ReadLine();
                    if (fileName.Contains("raw"))
                    {
                        picList.Add(fileName);
                    }
                    Console.WriteLine(fileName);
                }
            }
            response.Close();
            return picList;
        }

        public static void ResizeVideoCapture(VideoCapturer videoCapture, PictureBox pictureBox)
        {
            if (videoCapture != null)
            {
                try
                {
                    // 更新显示大小
                    videoCapture.SetDisplaySize(
                        pictureBox.Width,
                        pictureBox.Height);

                    // 重新调整视频窗口
                    videoCapture.ResizeVideoWindow();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("调整视频窗口大小时出错: " + ex.Message);
                }
            }
        }
        //KD123456789

        // 判断图片是否正常 （是否损坏、是否能被正确解码等） ---- CGT 0000000000000
        public static bool IsImageValid(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 100)  // 放宽到100字节
                return false;
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                return image.Width > 0 && image.Height > 0;
            }
            catch
            {
                return false;
            }
        }

    }

}
   
