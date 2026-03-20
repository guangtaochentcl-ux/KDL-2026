using AForge.Video.DirectShow;
using AntdUI;
using BaseProjejct;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using Emgu.CV.Cuda;
using Emgu.CV.Dnn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using System;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Net.Http.Json;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using System.Xml.Linq;
using VideoCapture_uvc;
using Path = System.IO.Path;

namespace skdl_new_2025_test_tool
{
    // 继承经过优化的 AutoScaleForm
    public partial class Form1 : AutoScaleForm
    {
        // 导入删除GDI对象的函数，防止内存泄漏
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        private HttpApi_stu _api; // 统一变量名
        private string _currentIp;

        // 定义播放器对象
        public OpenCvRtspPlayer player_panoramicMain, player_panoramicSub,
                                player_CloseUpMain, player_CloseUpSub,
                                player_ai1, player_ai2, player_ai3,
                                player_rtmp_panoramicMain, player_rtmp_panoramicSub,
                                player_rtmp_closeUpMain, player_rtmp_closeUpSub;

        public VideoCapturer camera1;
        private int checkStreamStatusWaitingTime = 5000; // 拉流状态检查间隔，单位毫秒
        private int checkVideoConfigTestStreamStatusWaitingTime = 5000; // 视频配置测试拉流状态检查间隔，单位毫秒
        private int checkRtmpStreamStatusWaitingTime = 10000; // RTMP拉流状态检查间隔，适当放宽一些

        private static readonly object _failFileLock = new object(); //用于记录UVC失败类型,方便查看

        bool Stop_uvc = true;//用于UVC停止拉流的标志位
        public Form1()
        {
            InitializeComponent();

            // 初始化播放器
            InitPlayers();

            // 设置初始圆角
            UpdateRegion();

            // 初始化测试case
            initTestCaseTable();
            initTestCaseTable2();

            // 初始化底部case切换栏样式
            initBottomSwitchSideStyle();
        }

        private void initBottomSwitchSideStyle()
        {
            tabControl3.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl3.SizeMode = TabSizeMode.Fixed;  // 关键：固定宽度
            tabControl3.ItemSize = new System.Drawing.Size(350, 60);
            tabControl3.DrawItem += (s, e) =>
            {
                System.Drawing.Rectangle bounds = tabControl3.GetTabRect(e.Index);
                bool isSelected = e.Index == tabControl3.SelectedIndex;
                using (Font tabFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
                using (SolidBrush brush = new SolidBrush(isSelected ? Color.FromArgb(100, 215, 234) : Color.FromArgb(240, 240, 240)))
                using (SolidBrush textBrush = new SolidBrush(Color.Black))  // 统一白色文字
                {
                    e.Graphics.FillRectangle(brush, bounds);
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString(tabControl3.TabPages[e.Index].Text,
                        tabFont, textBrush, new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), sf);
                }
            };
        }

        private void InitPlayers()
        {
            // 绑定控件
            player_panoramicMain = new OpenCvRtspPlayer(pictureBox1_panoramicMain);
            player_panoramicSub = new OpenCvRtspPlayer(pictureBox1_panoramicSub);
            player_CloseUpMain = new OpenCvRtspPlayer(pictureBox1_CloseUpMain);
            player_CloseUpSub = new OpenCvRtspPlayer(pictureBox1_CloseUpSub);

            player_ai1 = new OpenCvRtspPlayer(pictureBox1_ai1);
            player_ai2 = new OpenCvRtspPlayer(pictureBox1_ai2);
            player_ai3 = new OpenCvRtspPlayer(pictureBox1_ai3);

            player_rtmp_panoramicMain = new OpenCvRtspPlayer(pictureBox_rtmp_panoramicMain);
            player_rtmp_panoramicSub = new OpenCvRtspPlayer(pictureBox_rtmp_panoramicSub);
            player_rtmp_closeUpMain = new OpenCvRtspPlayer(pictureBox_rtmp_closeUpMain);
            player_rtmp_closeUpSub = new OpenCvRtspPlayer(pictureBox_rtmp_closeUpSub);
        }

        // 优化：安全的设置圆角，防止 GDI 泄漏
        private void UpdateRegion()
        {
            IntPtr hRgn = CreateRoundRectRgn(0, 0, Width, Height, 35, 35);
            try
            {
                this.Region = Region.FromHrgn(hRgn);
            }
            finally
            {
                // 关键：必须释放句柄，否则程序运行一会就会崩溃
                DeleteObject(hRgn);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 只有当窗体不是最小化时才更新圆角，减少计算
            if (this.WindowState != FormWindowState.Minimized)
            {
                UpdateRegion();
            }
        }

        // 窗体关闭时统一释放资源
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // 释放所有播放器
            player_panoramicMain?.Dispose();
            player_panoramicSub?.Dispose();
            player_CloseUpMain?.Dispose();
            player_CloseUpSub?.Dispose();
            player_ai1?.Dispose();
            player_ai2?.Dispose();
            player_ai3?.Dispose();
        }

        #region 辅助逻辑 (日志 & 校验)

        private bool IpCheckValid(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            // 优化正则
            return Regex.IsMatch(ip, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$");
        }

        private readonly string _logFilePath = "Seevision[SKDL-2025-new-test-tool]RunningLog.txt";
        private static readonly object _fileLock = new object();
        private const int MaxLogCount = 200;

        private void LogSaveOutput(string log)
        {
            // 防止 log 字符串本身为 null 导致后续报错
            if (string.IsNullOrEmpty(log)) return;

            try
            {
                string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string fullLog = $"【{timeStr}】{log}";

                // 1. 异步写文件 (这部分通常没问题，除非 _fileLock 为 null)
                Task.Run(() =>
                {
                    try
                    {
                        // 确保锁对象存在
                        if (_fileLock != null)
                        {
                            lock (_fileLock)
                            {
                                File.AppendAllText(_logFilePath, fullLog + Environment.NewLine);
                            }
                        }
                    }
                    catch { /* 忽略文件写入错误 */ }
                });

                // 2. UI 更新 (这是最容易报空引用的地方)
                // === 关键修复 1：检查控件对象是否为 null ===
                if (txtBoxRcv == null || txtBoxRcv.IsDisposed) return;

                Action updateUi = () =>
                {
                    try
                    {
                        // === 关键修复 2：再次检查，防止在 Invoke 等待期间控件被销毁 ===
                        if (txtBoxRcv == null || txtBoxRcv.IsDisposed) return;

                        // 检查资源图片是否存在，防止图片资源为 null 导致报错
                        var icon = Properties.Resources.seevi_64;

                        // 添加日志
                        txtBoxRcv.AddToBottom(new AntdUI.Chat.TextChatItem(log, icon, $"【{timeStr}】日志："));
                        txtBoxRcv.ToBottom();

                        // 检查 Items 集合是否为 null (防御性编程)
                        if (txtBoxRcv.Items != null && txtBoxRcv.Items.Count > MaxLogCount)
                        {
                            txtBoxRcv.Items.RemoveRange(0, 20);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 调试时可以打印错误，发布时忽略 UI 错误
                        System.Diagnostics.Debug.WriteLine("UI Log Error: " + ex.Message);
                    }
                };

                // === 关键修复 3：调用 Invoke 时加 try-catch，防止窗口句柄正好被销毁 ===
                try
                {
                    if (txtBoxRcv.InvokeRequired)
                    {
                        txtBoxRcv.BeginInvoke(updateUi);
                    }
                    else
                    {
                        updateUi();
                    }
                }
                catch
                {
                    // 忽略跨线程调用时的句柄错误
                }
            }
            catch
            {
                // 兜底捕获所有异常，保证日志系统不会弄崩主程序
            }
        }




        // 定义变量
        private System.Drawing.Point _startMousePos; // 鼠标按下时的【屏幕坐标】
        private System.Drawing.Point _startFormPos;  // 鼠标按下时的【窗体坐标】
        private bool _isDragging = false;

        // 1. 鼠标按下 (绑定到 PageHeader 的 MouseDown)
        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                // 关键点：全部记录【屏幕绝对坐标】
                _startMousePos = Control.MousePosition;
                _startFormPos = this.Location;
            }
        }

        // 2. 鼠标移动 (绑定到 PageHeader 的 MouseMove)
        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && e.Button == MouseButtons.Left)
            {
                // 1. 获取当前鼠标的屏幕坐标
                System.Drawing.Point currentMousePos = Control.MousePosition;

                // 2. 计算鼠标移动了多少距离 (当前 - 起始)
                int offsetX = currentMousePos.X - _startMousePos.X;
                int offsetY = currentMousePos.Y - _startMousePos.Y;

                // 3. 把这个距离加到窗体的起始位置上
                this.Location = new System.Drawing.Point(_startFormPos.X + offsetX, _startFormPos.Y + offsetY);

                // 如果还觉得卡，可以强制刷新，但在手动计算模式下通常不需要
                // this.Update(); 
            }
        }

        // 3. 鼠标松开 (绑定到 PageHeader 的 MouseUp)
        private void TitleBar_MouseUp(object sender, MouseEventArgs e)
        {
            _isDragging = false;
        }

        #region 业务逻辑

        private bool checkPICValid(string compareImgName, string picName)
        {
            if (!File.Exists(compareImgName) || !File.Exists(picName))
            {
                LogSaveOutput($"【图片丢失】无法找到文件: {compareImgName} 或 {picName}");
                return false;
            }

            try
            {
                // 1. 加载原始图片
                using (Mat mat1Raw = Cv2.ImRead(compareImgName, ImreadModes.Color))
                using (Mat mat2Raw = Cv2.ImRead(picName, ImreadModes.Color))
                {
                    if (mat1Raw.Empty() || mat2Raw.Empty())
                    {
                        LogSaveOutput("【图片损坏】读取失败。");
                        return false;
                    }

                    // === 优化核心：如果图片太大，先缩小 ===
                    // 限制最大宽度为 1024，足够判断差异了，内存占用减少 90%
                    int maxWidth = 1024;

                    Mat mat1, mat2;
                    bool needDisposeProcessed = false; // 标记是否需要手动释放缩放后的图

                    if (mat1Raw.Width > maxWidth)
                    {
                        // 计算缩放比例
                        double scale = (double)maxWidth / mat1Raw.Width;
                        int newH = (int)(mat1Raw.Height * scale);

                        // 创建新 Mat 存缩放后的图
                        mat1 = new Mat();
                        mat2 = new Mat();
                        Cv2.Resize(mat1Raw, mat1, new OpenCvSharp.Size(maxWidth, newH));
                        Cv2.Resize(mat2Raw, mat2, new OpenCvSharp.Size(maxWidth, newH));
                        needDisposeProcessed = true; // 标记需要释放
                    }
                    else
                    {
                        // 如果本来就很小，直接用原图引用
                        mat1 = mat1Raw;
                        mat2 = mat2Raw;
                    }

                    try
                    {
                        // 2. 计算公共区域
                        int minW = Math.Min(mat1.Width, mat2.Width);
                        int minH = Math.Min(mat1.Height, mat2.Height);
                        Rect roi = new Rect(0, 0, minW, minH);

                        using (Mat crop1 = mat1[roi])
                        using (Mat crop2 = mat2[roi])
                        using (Mat diffMat = new Mat())
                        {
                            // 3. 计算差异
                            Cv2.Absdiff(crop1, crop2, diffMat);
                            Scalar sum = Cv2.Sum(diffMat);
                            double totalDifference = sum.Val0 + sum.Val1 + sum.Val2;

                            // 注意：因为图片缩小了，像素变少了，差异总和也会变小。
                            // 应该计算“平均差异”或按比例调整阈值
                            // 这里我们计算【平均每像素差异】，这样阈值就跟分辨率无关了
                            double averageDiff = totalDifference / (minW * minH);

                            LogSaveOutput($"【对比结果】：总差异={totalDifference:F0}, 平均差异={averageDiff:F2}");

                            // 重新定义阈值逻辑：
                            // 建议 input1_testdiffer 填 "平均差异阈值" (比如 10.0)
                            // 或者如果你依然想用总差异，记得这里的 totalDifference 比原来小了很多

                            if (!long.TryParse(input1_testdiffer.Text, out long threshold))
                            {
                                // 兼容旧逻辑：如果不想改阈值输入，可以把当前计算结果反推回原分辨率量级
                                // 但推荐改用平均值判断
                                return totalDifference <= 100000;
                            }

                            // 简单粗暴兼容：如果用户输入的阈值很大(比如10万)，说明是针对大图的
                            // 如果我们缩小了图片(面积缩小约16倍)，阈值也该缩小
                            if (needDisposeProcessed)
                            {
                                // 4K -> 1024，面积缩小约 14 倍
                                return totalDifference <= (threshold / 14);
                            }

                            return totalDifference <= threshold;
                        }
                    }
                    finally
                    {
                        // 如果创建了缩放后的 Mat，需要手动释放，否则内存泄漏
                        if (needDisposeProcessed)
                        {
                            mat1?.Dispose();
                            mat2?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 捕获内存不足异常
                if (ex.Message.Contains("Failed to allocate"))
                {
                    LogSaveOutput($"【内存溢出】内存不足，建议将程序编译为 x64 版本或增加物理内存。");
                    // 尝试强制 GC
                    GC.Collect();
                }
                else
                {
                    LogSaveOutput($"【比对异常】{ex.Message}");
                }
                return false;
            }
        }

        private async void buttonGetToken_Click(object sender, EventArgs e)
        {
            _currentIp = textBox_ip.Text.Trim();
            //if (IpCheckValid(_currentIp))
            //{
            LogSaveOutput("正在获取 Token...");
            _api = new HttpApi_stu(_currentIp);
            try
            {
                string token = await _api.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                if (!string.IsNullOrEmpty(token))
                {
                    token_input.Text = token;
                    LogSaveOutput("Token 获取成功");
                }
                else
                {
                    LogSaveOutput("Token 获取失败");
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"登录异常: {ex.Message}");
            }
            //}
            //else
            //{
            //    LogSaveOutput($"{_currentIp} -- IP地址格式错误！");            //}
        }

        // === 通用截图辅助方法 (解决死循环问题) ===
        private async Task<string> SafeSnapshotAsync(OpenCvRtspPlayer player, string dir, string name)
        {
            try
            {
                //Directory.GetFiles(dir, "*.png")
                //    .Select(x => new FileInfo(x))
                //    .OrderBy(f => f.CreationTime) // 排序：索引0是最旧的
                //    .ToList() // 转为List以便复用
                //    .Skip(10) // 核心修改：保留最初的10张（跳过它们，不进入删除列表）
                //    .Take((Directory.GetFiles(dir, "*.png").Length - 10) / 2) // 核心修改：取剩余数量的一半
                //    .ToList()
                //    .ForEach(f => File.Delete(f.FullName));

                var files = new DirectoryInfo(dir).GetFiles("*.png").OrderBy(f => f.CreationTime).ToList();

                // 只有超过100张才触发
                if (files.Count > 100)
                {
                    // 跳过前10张(最旧的保留)，取剩下数量的一半进行并行删除
                    Parallel.ForEach(files.Skip(10).Take((files.Count - 10) / 2), f => f.Delete());
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput("skip this judge!");
            }

            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string fileName = $"[{DateTime.Now:yyyyMMddHHmmss}]{name}.png";
                string fullPath = Path.Combine(dir, fileName);

                //Console.WriteLine(fullPath);

                LogSaveOutput($"正在截图 {name}...");

                // 最多尝试 5 次，每次间隔 500ms，防止死循环
                for (int i = 0; i < 300; i++)
                {
                    if (player.Snapshot(fullPath)) // 假设使用了上一轮优化后的 Snapshot 方法
                    {
                        LogSaveOutput($"【{name}】截图成功: {fileName}");
                        LogSaveOutput(player.GetPlayerStatus().TotalBitrateKbps.ToString());
                        return fullPath;
                    }
                    await Task.Delay(5000);
                }
                LogSaveOutput($"【{name}】截图失败 (超时)");
            }

            catch (Exception ex)
            {
                LogSaveOutput($"【{name}】截图异常: {ex.Message}， 防止退出工具，返回FAIL - 空");
            }

            return "";
        }

        // === 通用截图辅助方法 (解决死循环问题) ===
        private async Task<string> SafeSnapshotAsync(OpenCvRtspPlayer player, string name)
        {
            //if (!player.IsPlaying)
            //{
            //    LogSaveOutput($"【{name}】未在播放，无法截图");
            //    return "";
            //}
            string ipSafe = _currentIp.Replace(".", "_");
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", ipSafe);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string fileName = $"[{DateTime.Now:yyyyMMddHHmmss}]{name}.png";
            string fullPath = Path.Combine(dir, fileName);

            LogSaveOutput($"正在截图 {name}...");

            // 最多尝试 5 次，每次间隔 500ms，防止死循环
            for (int i = 0; i < 30; i++)
            {
                if (player.Snapshot(fullPath)) // 假设使用了上一轮优化后的 Snapshot 方法
                {
                    LogSaveOutput($"【{name}】截图成功: {fileName}");
                    player.SetManualBitrate(1);
                    return fullPath;
                }
                await Task.Delay(500);
            }
            LogSaveOutput($"【{name}】截图失败 (超时)");
            return "";
        }

        private async Task rtspStreamOn(string ip, OpenCvRtspPlayer player, string id, string streamName)
        {
            string url = $"rtsp://{ip}/{id}";
            player.Start(url, checkBoxDecodeTest.Checked);
            LogSaveOutput($"【{streamName}】开始拉流: {url}");
            await Task.Delay(100);
        }

        private async void rtspStreamOff(OpenCvRtspPlayer player, string streamName)
        {
            // 修复：只 Stop 不 Dispose，方便下次重新 Start
            player.Stop();
            LogSaveOutput($"【{streamName}】已停止");
            await Task.Delay(100);
            if (player != null)
            {
                player.Dispose();
            }
        }




        // === 全景主流 ===
        private void panoramicMainStreamOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            string url = $"rtsp://{_currentIp}/2";
            player_panoramicMain.Start(url, checkBoxDecodeTest.Checked);
            LogSaveOutput($"【全景主流】开始拉流: {url}");
        }

        private void panoramicMainStreamOffBtn_Click(object sender, EventArgs e)
        {
            // 修复：只 Stop 不 Dispose，方便下次重新 Start
            player_panoramicMain.Stop();
            LogSaveOutput("【全景主流】已停止");
        }

        private async void panoramicMainStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_panoramicMain, "全景主流");
        }

        // === 全景辅流 ===
        private void panoramicSubStreamOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_panoramicSub.Start($"rtsp://{_currentIp}/4", checkBoxDecodeTest.Checked);
            LogSaveOutput("【全景辅流】开始拉流");
        }

        private void panoramicSubStreamOffBtn_Click(object sender, EventArgs e)
        {
            player_panoramicSub.Stop();
            LogSaveOutput("【全景辅流】已停止");
        }

        private async void panoramicSubStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_panoramicSub, "全景辅流");
        }

        // === 特写主流 ===
        private void closeUpMainStreamOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_CloseUpMain.Start($"rtsp://{_currentIp}/1", checkBoxDecodeTest.Checked);
            LogSaveOutput("【特写主流】开始拉流");
        }

        private void closeUpMainStreamOffBtn_Click(object sender, EventArgs e)
        {
            player_CloseUpMain.Stop();
            LogSaveOutput("【特写主流】已停止");
        }

        private async void closeUpMainStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_CloseUpMain, "特写主流");
        }

        // === 特写辅流 ===
        private void closeUpSubStreamOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_CloseUpSub.Start($"rtsp://{_currentIp}/3", checkBoxDecodeTest.Checked);
            LogSaveOutput("【特写辅流】开始拉流");
        }

        private void closeUpSubStreamOffBtn_Click(object sender, EventArgs e)
        {
            player_CloseUpSub.Stop();
            LogSaveOutput("【特写辅流】已停止");
        }

        private async void closeUpSubStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_CloseUpSub, "特写辅流");
        }

        // === AI 1 ===
        private void ai1StreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_ai1.Start($"rtsp://{_currentIp}/ai1", checkBoxDecodeTest.Checked);
            LogSaveOutput("【AI1前排】开始拉流");
        }

        private void ai1StreanOffBtn_Click(object sender, EventArgs e)
        {
            player_ai1.Stop();
            LogSaveOutput("【AI1前排】已停止");
        }

        private async void ai1StreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_ai1, "AI1前排流");
        }

        // === AI 2 ===
        private void ai2StreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_ai2.Start($"rtsp://{_currentIp}/ai2", checkBoxDecodeTest.Checked);
            LogSaveOutput("【AI2左后】开始拉流");
        }

        private void ai2StreanOffBtn_Click(object sender, EventArgs e)
        {
            player_ai2.Stop();
            LogSaveOutput("【AI2左后】已停止");
        }

        private async void ai2StreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_ai2, "AI2左后排流");
        }

        // === AI 3 ===
        private void ai3StreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_ai3.Start($"rtsp://{_currentIp}/ai3", checkBoxDecodeTest.Checked);
            LogSaveOutput("【AI3右后】开始拉流");
        }

        private void ai3StreanOffBtn_Click(object sender, EventArgs e)
        {
            player_ai3.Stop();
            LogSaveOutput("【AI3右后】已停止");
        }

        private async void ai3StreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_ai3, "AI3右后排流");
        }
        #endregion

        private void addResolutionRadioIntoPanel(List<string> resolutions, AntdUI.FlowPanel panel)
        {
            panel.Controls.Clear();
            panel.AutoScroll = true;
            foreach (var res in resolutions)
            {
                AntdUI.Radio radio = new AntdUI.Radio();
                radio.Text = res;
                radio.Tag = res;
                radio.Margin = new Padding(5);
                radio.AutoSize = true;
                panel.Controls.Add(radio);
            }
        }

        private async void getAllResolutionBtn_Click(object sender, EventArgs e)
        {
            try
            {
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("panoramicMain"), panel_panoramicMain_resolution);
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("panoramicSub"), panel_panoramicSub_resolution);
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("closeUpMain"), panel_closeUpMain_resolution);
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("closeUpSub"), panel_closeUpSub_resolution);
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取所有分辨率异常！\n{ex.ToString()}");
            }
        }

        private async void getAllResolution2()
        {
            try
            {
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("sub"), panel_panoramicMain_resolution);
                addResolutionRadioIntoPanel(await _api.GetSpecVideoStreamConfig("main"), panel_closeUpMain_resolution);
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取所有分辨率异常！\n{ex.ToString()}");
            }
        }





























        // 1. 在类级别定义变量（放在方法外面，作为类的成员变量）
        private int _currentOrderIndex = 0; // 用于记录顺序取出的当前位置
        private readonly Random _rnd = new Random(); // Random 实例复用，避免伪随机重复

        /// <summary>
        /// 从字典中选择一个流
        /// </summary>
        /// <param name="type">模式："random" 或 "order"</param>
        /// <param name="openCvRtspPlayersDict">字典源</param>
        /// <returns>选中的 Key，如果字典为空返回 null</returns>
        private string chooseAStreamByQueue(string type, Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict)
        {
            // 安全检查：防止字典为空导致报错
            if (openCvRtspPlayersDict == null || openCvRtspPlayersDict.Count == 0)
            {
                LogSaveOutput("【错误】播放器列表为空，无法选择流！");
                return null;
            }

            // 将 Key 转为 List 以便通过索引访问
            // 注意：Dictionary 的 Keys 顺序通常是固定的，但转为 List 更保险
            List<string> keyList = openCvRtspPlayersDict.Keys.ToList();
            string selectedKey = "";

            if (type == "random")
            {
                // === 随机模式 ===
                int randomIndex = _rnd.Next(keyList.Count);
                selectedKey = keyList[randomIndex];
                LogSaveOutput($"[随机] 选中了: {selectedKey}");
            }
            else if (type == "order")
            {
                // === 顺序模式 ===

                // 容错处理：如果字典数量变了，或者索引越界，重置为 0
                if (_currentOrderIndex >= keyList.Count)
                {
                    _currentOrderIndex = 0;
                }

                // 取出当前索引对应的 Key
                selectedKey = keyList[_currentOrderIndex];
                LogSaveOutput($"[顺序] 选中了 ({_currentOrderIndex + 1}/{keyList.Count}): {selectedKey}");

                // 索引递增，并实现循环（取模运算）
                // 例如 Count=7：索引 0->1->2...->6->0
                _currentOrderIndex++;
                if (_currentOrderIndex >= keyList.Count)
                {
                    _currentOrderIndex = 0; // 回到第一个
                }
            }

            return selectedKey;
        }

        private string StreamUrlBack(string curStreamName)
        {
            string cur_url = "";
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            if (curStreamName == "全景主流")
            {
                cur_url = $"rtsp://{_currentIp}/2";
            }
            if (curStreamName == "全景辅流")
            {
                cur_url = $"rtsp://{_currentIp}/4";
            }
            if (curStreamName == "特写主流")
            {
                cur_url = $"rtsp://{_currentIp}/1";
            }
            if (curStreamName == "特写辅流")
            {
                cur_url = $"rtsp://{_currentIp}/3";
            }
            if (curStreamName == "AI前排流")
            {
                cur_url = $"rtsp://{_currentIp}/ai1";
            }
            if (curStreamName == "AI左后排流")
            {
                cur_url = $"rtsp://{_currentIp}/ai2";
            }
            if (curStreamName == "AI右后排流")
            {
                cur_url = $"rtsp://{_currentIp}/ai3";
            }
            if (curStreamName == "性能模式流1")

            {
                cur_url = $"rtsp://{_currentIp}/extreme";
            }
            if (curStreamName == "性能模式流2")
            {
                cur_url = $"rtsp://{_currentIp}/extreme_2";
            }

            if (curStreamName == "性能模式流1_2")

            {
                cur_url = $"rtsp://{_currentIp}/1";
            }
            if (curStreamName == "性能模式流2_2")
            {
                cur_url = $"rtsp://{_currentIp}/2";
            }
            return cur_url;
        }







        // 停止测试标志位，通用
        bool stopTest = false;

        /**
         * SKDL0402(VC16S)&SKDL0503(VC35P)项目case
         */
        // 1. 使用 BindingList 而不是 List
        private BindingList<TestCases> testCases = new BindingList<TestCases>();

        private void initTestCaseTable()
        {
            table1_testCase.Columns.Clear();

            table1_testCase.Columns.Add(new AntdUI.Column("Name", "测试用例 -- 带自动IP"));
            table1_testCase.Columns.Add(new AntdUI.Column("Description", "用例描述"));
            table1_testCase.Columns.Add(new AntdUI.Column("TestCount", "测试次数"));

            var colResult = new AntdUI.Column("TestResult", "测试结果");
            table1_testCase.Columns.Add(colResult);

            var colAction = new AntdUI.Column("BtnText", "操作");
            colAction.Align = AntdUI.ColumnAlign.Center;
            table1_testCase.Columns.Add(colAction);

            TestCases t1 = new TestCases();
            t1.Name = "case1_分辨率轮询压测";
            t1.Description = "1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，切换完所有分辨率为1轮；\r\n特写主流 rtsp://[ip]/1   -- closeUpMain\r\n全景主流 rtsp://[ip]/2 -- panoramicMain\r\n特写辅流 rtsp://[ip]/3 -- closeUpSub\r\n全景辅流 rtsp://[ip]/4 -- panoramicSub\r\nAI前排流 rtsp://[ip]/ai1  (单拉流，不能切换分辨率)\r\nAI左后排流 rtsp://[ip]/ai2  (单拉流，不能切换分辨率)\r\nAI右后排流rtsp://[ip]/ai3 (单拉流，不能切换分辨率)";
            t1.TestCount = 0;
            t1.TestResult = "待测试";
            testCases.Add(t1);

            TestCases t2 = new TestCases();
            t2.Name = "case2_随机拉流切换压测";
            t2.Description = "1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t2.TestCount = 0;
            t2.TestResult = "待测试";
            testCases.Add(t2);

            TestCases t3 = new TestCases();
            t3.Name = "case3_clumsy拉流限速压测-1%";
            t3.Description = "1、控制clumsy.exe进行限速\r\n2、ping控制丢包率在1%\r\n3、控制clumsy.exe解除限速\r\n4、RTSP 同时拉”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n5、循环测试500次";
            t3.TestCount = 0;
            t3.TestResult = "待测试";
            testCases.Add(t3);

            TestCases t3_1 = new TestCases();
            t3_1.Name = "case3_clumsy拉流限速压测-8%";
            t3_1.Description = "1、控制clumsy.exe进行限速\r\n2、ping控制丢包率在8%\r\n3、控制clumsy.exe解除限速\r\n4、RTSP 同时拉”全景主流、全景辅流 、特写主流、特写辅流“\r\n5、循环测试500次";
            t3_1.TestCount = 0;
            t3_1.TestResult = "待测试";
            testCases.Add(t3_1);

            TestCases t4 = new TestCases();
            t4.Name = "case4_64M码率循环拉流压测";
            t4.Description = "1、所有码率设置64M -- 65536\r\n2、RTSP拉流：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n3、关流、开流循环500次";
            t4.TestCount = 0;
            t4.TestResult = "待测试";
            testCases.Add(t4);

            TestCases t5 = new TestCases();
            t5.Name = "case5_16M码率循环拉流压测";
            t5.Description = "1、所有码率设置16M -- 16384\r\n2、RTSP拉流：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n3、关流、开流循环500次";
            t5.TestCount = 0;
            t5.TestResult = "待测试";
            testCases.Add(t5);

            TestCases t6 = new TestCases();
            t6.Name = "case6_1M码率循环拉流压测";
            t6.Description = "1、所有码率设置1M -- 1024\r\n2、RTSP拉流：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n3、关流、开流循环500次";
            t6.TestCount = 0;
            t6.TestResult = "待测试";
            testCases.Add(t6);

            TestCases t7 = new TestCases();
            t7.Name = "case7_extreme性能模式拉流压测";
            t7.Description = "1、设置为性能模式（ 分辨率会默认到4K，帧率60帧）\r\n2、RTSP拉流：全景主流、特写主流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/extreme, \r\nrtsp://[IP]/extreme_2";
            t7.TestCount = 0;
            t7.TestResult = "待测试";
            testCases.Add(t7);

            TestCases t8 = new TestCases();
            t8.Name = "case8_highFPS高帧率模式拉流压测";
            t8.Description = "1、设置为高帧模式 （自动会默认分辨率主流到1080P60帧 + 辅流 720P 30帧 ）\r\n2、RTSP拉流：全景主流、特写主流、全景辅流、特写辅流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/1\r\nrtsp://[IP]/2\r\nrtsp://[IP]/3  \r\nrtsp://[IP]/4";
            t8.TestCount = 0;
            t8.TestResult = "待测试";
            testCases.Add(t8);

            TestCases t9 = new TestCases();
            t9.Name = "case9_highFPS高帧率模式拉流压测";
            t9.Description = "1、设置为高帧模式 （自动会默认分辨率主流到1080P60帧 + 切换 帧率辅流 720P 60帧(60fps需要手动切换) ）\r\n2、RTSP拉流：全景主流、特写主流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/1\r\nrtsp://[IP]/2\r\nrtsp://[IP]/3  \r\nrtsp://[IP]/4";
            t9.TestCount = 0;
            t9.TestResult = "待测试";
            testCases.Add(t9);

            TestCases t10 = new TestCases();
            t10.Name = "case10_all30FPS_main4K_sub720P拉流压测";
            t10.Description = "1、所有帧率设置30fps （设置为高分辨率模式，分辨率为4K（主流设））+ 720P（辅流设）））\r\n2、RTSP拉流：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n3、关流、开流循环500次";
            t10.TestCount = 0;
            t10.TestResult = "待测试";
            testCases.Add(t10);

            TestCases t11 = new TestCases();
            t11.Name = "case11_all25FPS_main1080P_sub576P拉流压测";
            t11.Description = "1、所有帧率设置25fps（设置为高分辨率模式，分辨率为1080P（主流设） + 576P（辅流设））\r\n2、RTSP拉流：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\n3、关流、开流循环500次";
            t11.TestCount = 0;
            t11.TestResult = "待测试";
            testCases.Add(t11);

            TestCases t12 = new TestCases();
            t12.Name = "case12_clumsy拉流包括RTMP全流限速压测";
            t12.Description = "1、控制clumsy.exe进行限速\r\n2、ping控制丢包率在5%\r\n3、控制clumsy.exe解除限速\r\n4、RTSP同时拉：”全景主流、全景辅流 、特写主流、特写辅流、AI前排流、AI左后排流、AI右后排流“\r\nRTMP同时拉：全景主流、全景辅流、特写主流、特写辅流\r\n5、循环测试500次(需要手动先在上面填入当前ip对应的rtmp流地址)";
            t12.TestCount = 0;
            t12.TestResult = "待测试";
            testCases.Add(t12);


            TestCases t13 = new TestCases();
            t13.Name = "case13_uvc全景高分辨率模式切换分辨率压测";
            t13.Description = "1、设置UVC教师全景流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t13.TestCount = 0;
            t13.TestResult = "待测试";
            testCases.Add(t13);

            TestCases t14 = new TestCases();
            t14.Name = "case14_uvc全景高帧率模式切换分辨率压测";
            t14.Description = "1、设置UVC教师全景流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t14.TestCount = 0;
            t14.TestResult = "待测试";
            testCases.Add(t14);

            TestCases t15 = new TestCases();
            t15.Name = "case15_uvc特写高分辨率模式切换分辨率压测";
            t15.Description = "1、设置UVC教师特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t15.TestCount = 0;
            t15.TestResult = "待测试";
            testCases.Add(t15);

            TestCases t16 = new TestCases();
            t16.Name = "case16_uvc特写高帧率模式切换分辨率压测";
            t16.Description = "1、设置UVC教师特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t16.TestCount = 0;
            t16.TestResult = "待测试";
            testCases.Add(t16);

            TestCases t17 = new TestCases();
            t17.Name = "case17_gbs通道1和2拉流压测-python";
            t17.Description = "" +
                "【测试前修改对应文件夹内python脚本的 gbs_url1和 urlList后再开始测试】\n" +
                "注意事项如下：\n" +
                "1、修改gbs_url为对应需要测试设备的LiveGBS的地址\n" +
                "\n1、使用浏览器打开GBS平台\r\n2、打开通道一拉流、打开通道二拉流、打开通道三拉流、打开通道四拉流\r\n3、关闭通道一拉流\r\n4、打开通道一拉流\r\n5、关闭通道二拉流\r\n6、打开通道二拉流\r\n7、关闭通道三拉流\r\n8、打开通道三拉流\r\n9、关闭通道四拉流\r\n10、打开通道四拉流\r\n11、3-6步骤测试500次\r\n备注：通道（channel0、channel1、channel2、channel3）";
            t17.TestCount = 0;
            t17.TestResult = "待测试";
            testCases.Add(t17);

            TestCases t18 = new TestCases();
            t18.Name = "case18_全功能持续老化测试";
            t18.Description = "【点击后开始全功能老化测试计时，勾选上面需要测试的内容，即可自动开始老化测试，取消勾选该测试即自动停止！】" +
                "\n工具端实现：（勾选对应测试项，确认测试内容）\r\n1、全部7路流RTSP拉出来 -- 流全开\r\n2、云台不断转动 --- 云台不断来回转动\r\n3、光变变焦不断运行 -- 光变设置最大然后从近到远来回切\r\n4、HDMI OUT接出来 -- 连接HDMI OUT到其他显示器";
            t18.TestCount = 0;
            t18.TestResult = "待测试";
            testCases.Add(t18);

            TestCases t19 = new TestCases();
            t19.Name = "case19_重启全视频流压测";
            t19.Description = "1、软件重启设备\r\n2、重启设备查看HDMI主动出流 （HDMI 主画面跟随web设置全景\\特写）---VC35P\r\n3、重启设备后RTSP 同时拉流：全景主码流、全景辅码流、特写主码流、\n特写辅码流、AI前排流、AI左后排流、AI右后排流（默认视频配置）\r\n5、检查帧率、码率、画面显示正常\r\n6、重启压测1000次";
            t19.TestCount = 0;
            t19.TestResult = "待测试";
            testCases.Add(t19);


            TestCases t20 = new TestCases();
            t20.Name = "case20_红外控制休眠唤醒拉流压测";
            t20.Description = "case20_红外控制休眠唤醒拉流压测 -- 待OK调试脚本\r\n\r\n需要教授找电机模拟按压遥控器的开关机键,\r\n然后板端需要给红外模块供电，后压测\r\n\r\nKDL0105的休眠和唤醒刚刚和炜豪确认，\r\n休眠唤醒红外控制，要后面硬件改版后，\r\n给红外模块独立供电后，就可以正常通过遥控器控制开关机了";
            t20.TestCount = 0;
            t20.TestResult = "待测试";
            testCases.Add(t20);


            TestCases t21 = new TestCases();
            t21.Name = "case21_ota双版本互刷升级后拉流压测";
            t21.Description = "测试步骤：\r\n1、选择ota包1和ota包2（需要进行双固件升级压测的版本）\r\n2、点击开始测试即可\r\n\r\n1、使用自动化工具进行OTA升级1000次 \r\n2、每次刷机后工具HDMI出流（VC35P）、RTSP拉流正常\r\n3、查看工具每次刷机是否正常";
            t21.TestCount = 0;
            t21.TestResult = "待测试";
            testCases.Add(t21);

            TestCases t22 = new TestCases();
            t22.Name = "case22_u盘救砖刷机后拉流压测";
            t22.Description = "（需要单独出一个默认自动以udhcpc true上电的固件用于压测，\n自己在jenkins编一个带这笔patch（40151）的固件用于测试即可）\r\n测试操作步骤：\r\n1、U盘里面放入带该patch的U盘升级固件\r\n2、点击开始测试即可\r\n\r\ncase描述：\r\n1、准备FAT32格式U盘，U盘内存在救砖文件；插入U盘；\r\n2、设备掉电情况下，保持针戳复位按键上电，进入救砖模式（我可以通过重启来进救砖模式）\r\n3、刷机后工具HDMI出流（VC35P）、RTSP拉流\r\n4、查看工具每次刷机是否正常\r\n5、循环500次";
            t22.TestCount = 0;
            t22.TestResult = "待测试";
            testCases.Add(t22);

            TestCases t23 = new TestCases();
            t23.Name = "case23_重启5000次后拉流压测";
            t23.Description = "1、软件重启设备\r\n2、重启压测5000次\r\n3、压测5000次后进行 HDMI OUT出流和RTSP拉流测试";
            t23.TestCount = 0;
            t23.TestResult = "待测试";
            testCases.Add(t23);

            TestCases t24 = new TestCases();
            t24.Name = "case24_继电器上下电压测逻辑1";
            t24.Description = "测试准备前提：\r\n1、测试工具一个工具可以带5路压测，填入对应IP\r\n2、提前将继电器接好对应测试设备，\r\n3、分别勾选需要测试的开关连接设备口即可测试对应设备，\r\n\r\n1、接Sensor ，使用 DC 12V 供电，测试系统起来后断电\r\n2、使用继电器设置上电45S，下电10S\r\n3、压测5000+次，9H";
            t24.TestCount = 0;
            t24.TestResult = "待测试";
            testCases.Add(t24);

            TestCases t25 = new TestCases();
            t25.Name = "case25_继电器上下电压测逻辑2";
            t25.Description = "测试准备前提：\r\n1、测试工具一个工具可以带5路压测，填入对应IP\r\n2、提前将继电器接好对应测试设备，\r\n3、分别勾选需要测试的开关连接设备口即可测试对应设备，\r\n\r\n1、接Sensor ，使用 DC 12V 供电，测试系统起来期间断电\r\n2、使用继电器设置设置上电5S，下电5S   500次\r\n3、使用继电器设置设置上电25S，下电10S   500次\r\n4、使用继电器设置设置上电30S，下电5S   500次\r\n5、测试完系统正常上电开机起来，HDMI出流，RTSP拉流";
            t25.TestCount = 0;
            t25.TestResult = "待测试";
            testCases.Add(t25);

            TestCases t26 = new TestCases();
            t26.Name = "case26_高帧模式分辨率轮询压测";
            t26.Description = "高帧模式：\r\n1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”全景主流、全景辅流 、特写主流、特写辅流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，切换完所有分辨率为1轮；\r\n特写主流 rtsp://[ip]/1   -- closeUpMain\r\n全景主流 rtsp://[ip]/2 -- panoramicMain\r\n特写辅流 rtsp://[ip]/3 -- closeUpSub\r\n全景辅流 rtsp://[ip]/4 -- panoramicSub";
            t26.TestCount = 0;
            t26.TestResult = "待测试";
            testCases.Add(t26);

            TestCases t27 = new TestCases();
            t27.Name = "case27_高帧模式随机拉流切换压测";
            t27.Description = "高帧率模式：\r\n1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”全景主流、全景辅流 、特写主流、特写辅流“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t27.TestCount = 0;
            t27.TestResult = "待测试";
            testCases.Add(t27);

            TestCases t28 = new TestCases();
            t28.Name = "case28_性能模式分辨率轮询压测";
            t28.Description = "性能模式：\r\n1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”全景主流、特写主流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，切换完所有分辨率为1轮；\r\n特写主流 rtsp://[ip]/1   -- closeUpMain\r\n全景主流 rtsp://[ip]/2 -- panoramicMain";
            t28.TestCount = 0;
            t28.TestResult = "待测试";
            testCases.Add(t28);


            TestCases t29 = new TestCases();
            t29.Name = "case29_性能模式随机拉流切换压测";
            t29.Description = "性能模式：\r\n1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”extreme1、extreme2“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t29.TestCount = 0;
            t29.TestResult = "待测试";
            testCases.Add(t29);

            TestCases t30 = new TestCases();
            t30.Name = "case30_uvc和RTSP高分辨率模式编码复用压测";
            t30.Description = "UVC设置全景流\r\n1、网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流\r\n2、UVC 拉流H264 4K\r\n3、重新RTSP拉主流\r\n4、UCV 拉流MJPEG 1080P\r\n5、重新RTSP拉流\r\n6、网络流设置为主流 4K 30帧、辅流默认，RTSP 拉流主流\r\n7、重复1-6 500次";
            t30.TestCount = 0;
            t30.TestResult = "待测试";
            testCases.Add(t30);

            TestCases t31 = new TestCases();
            t31.Name = "case31_uvc和RTSP高分辨率模式编码复用压测";
            t31.Description = "UVC设置特写流\r\n1、网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流\r\n2、UVC 拉流H264 4K\r\n3、重新RTSP拉主流\r\n4、UCV 拉流MJPEG 1080P\r\n5、重新RTSP拉流\r\n6、网络流设置为主流 4KP30帧、辅流默认，RTSP 拉流主流\r\n7、重复1-6 500次";
            t31.TestCount = 0;
            t31.TestResult = "待测试";
            testCases.Add(t31);

            TestCases t32 = new TestCases();
            t32.Name = "case32_软重启检查系统是否正常启动";
            t32.Description = "工具会自动出发软重启，并检测系统是否正常上线（不检测camera功能）";
            t32.TestCount = 0;
            t32.TestResult = "待测试";
            testCases.Add(t32);


            TestCases t33 = new TestCases();
            t33.Name = "case33_上下电检测系统是否启动正常";
            t33.Description = "工具将进行5000次上下电并同时检测系统是否启动正常";
            t33.TestCount = 0;
            t33.TestResult = "待测试";
            testCases.Add(t33);

            TestCases t34 = new TestCases();
            t34.Name = "case34_模式切换压测";
            t34.Description = "1、切换高分辨率模式拉流7路，切换到高帧模式拉流4路\r\n2、从高帧模式再切换到高分辨率模式拉流7路\r\n3、高分辨率模式切换到性能模式，拉流2路\r\n4、性能模式切换回到高分辨率模式拉流7路\r\n5、切换到高帧模式拉流4路，切换到性能模式拉流2路\r\n6、性能模式切换到高帧模式拉流4路\r\n7、以上循环测试1-6 1000次";
            t34.TestCount = 0;
            t34.TestResult = "待测试";
            testCases.Add(t34);


            table1_testCase.DataSource = testCases;
            table1_testCase.CellClick += Table1_CellClick;
        }

        private async void Table1_CellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            // e.Record 是当前行的数据对象
            if (e.Record is TestCases item)
            {
                DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "是否运行当前点击的测试用例",
                    "如果当前case正在运行，请勿重复触发！！！",
                    AntdUI.TType.Warn));

                if (result != DialogResult.Yes && result != DialogResult.OK)
                {
                    // 用户点了取消，取消关闭，恢复Loading状态
                    return;
                }
                else
                { // 判断是否点击了“操作”列 (Key 为 "BtnText")
                    if (e.Column.Key == "BtnText")
                    {
                        // 防止重复点击
                        if (item.BtnText == "测试中...") return;

                        // 1. 更新 UI 状态
                        item.BtnText = "测试中...";
                        item.TestResult = "运行中...";
                        stopTest = false;
                        try
                        {
                            item.TestCount = 0;
                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            // 设置自动ip
                            getNetWorkConfigBtn_Click(null, null);
                            await Task.Delay(1000);
                            setUdhcpcBtn_Click(null, null);
                            await Task.Delay(1000);

                            resetAllStreamDefaultConfigBtn_Click(null, null);
                            await Task.Delay(1000);


                            // 按照case运行，并实时修改结果
                            if (item.Name == "case1_分辨率轮询压测")
                            {
                                TestCase1(item);
                            }
                            if (item.Name == "case2_随机拉流切换压测")
                            {
                                TestCase2(item);
                            }
                            if (item.Name == "case3_clumsy拉流限速压测-1%")
                            {
                                TestCase3(item);
                            }
                            if (item.Name == "case3_clumsy拉流限速压测-8%")
                            {
                                TestCase3_1(item);
                            }
                            if (item.Name == "case4_64M码率循环拉流压测")
                            {
                                TestCase4(item);
                            }
                            if (item.Name == "case5_16M码率循环拉流压测")
                            {
                                TestCase5(item);
                            }
                            if (item.Name == "case6_1M码率循环拉流压测")
                            {
                                TestCase6(item);
                            }
                            if (item.Name == "case7_extreme性能模式拉流压测")
                            {
                                TestCase7(item);
                            }
                            if (item.Name == "case8_highFPS高帧率模式拉流压测")
                            {
                                TestCase8(item);
                            }
                            if (item.Name == "case9_highFPS高帧率模式拉流压测")
                            {
                                TestCase9(item);
                            }
                            if (item.Name == "case10_all30FPS_main4K_sub720P拉流压测")
                            {
                                TestCase10(item);
                            }
                            if (item.Name == "case11_all25FPS_main1080P_sub576P拉流压测")
                            {
                                TestCase11(item);
                            }
                            if (item.Name == "case12_clumsy拉流包括RTMP全流限速压测")
                            {
                                TestCase12(item);
                            }
                            if (item.Name == "case13_uvc全景高分辨率模式切换分辨率压测")
                            {
                                TestCase13(item);
                            }
                            if (item.Name == "case14_uvc全景高帧率模式切换分辨率压测")
                            {
                                TestCase14(item);
                            }
                            if (item.Name == "case15_uvc特写高分辨率模式切换分辨率压测")
                            {
                                TestCase15(item);
                            }
                            if (item.Name == "case16_uvc特写高帧率模式切换分辨率压测")
                            {
                                TestCase16(item);
                            }
                            if (item.Name == "case17_gbs通道1和2拉流压测-python")
                            {
                                TestCase17(item);
                            }
                            if (item.Name == "case18_全功能持续老化测试")
                            {
                                TestCase18(item);
                            }
                            if (item.Name == "case19_重启全视频流压测")
                            {
                                TestCase19(item);
                            }
                            if (item.Name == "case20_红外控制休眠唤醒拉流压测")
                            {
                                TestCase20(item);
                            }
                            if (item.Name == "case21_ota双版本互刷升级后拉流压测")
                            {
                                TestCase21(item);
                            }
                            if (item.Name == "case22_u盘救砖刷机后拉流压测")
                            {
                                TestCase22(item);
                            }
                            if (item.Name == "case23_重启5000次后拉流压测")
                            {
                                TestCase23(item);
                            }
                            if (item.Name == "case24_继电器上下电压测逻辑1")
                            {
                                TestCase24(item);
                            }
                            if (item.Name == "case25_继电器上下电压测逻辑2")
                            {
                                TestCase25(item);
                            }
                            if (item.Name == "case26_高帧模式分辨率轮询压测")
                            {
                                TestCase26(item);
                            }
                            if (item.Name == "case27_高帧模式随机拉流切换压测")
                            {
                                TestCase27(item);
                            }
                            if (item.Name == "case28_性能模式分辨率轮询压测")
                            {
                                TestCase28(item);
                            }
                            if (item.Name == "case29_性能模式随机拉流切换压测")
                            {
                                TestCase29(item);
                            }
                            if (item.Name == "case30_uvc和RTSP高分辨率模式编码复用压测")
                            {
                                TestCase30(item);
                            }
                            if (item.Name == "case31_uvc和RTSP高分辨率模式编码复用压测")
                            {
                                TestCase31(item);
                            }
                            if (item.Name == "case32_软重启检查系统是否正常启动")
                            {
                                TestCase32(item);
                            }
                            if (item.Name == "case33_上下电检测系统是否启动正常")
                            {
                                TestCase33(item);
                            }
                            if (item.Name == "case34_模式切换压测")
                            {
                                TestCase34(item);
                            }

                        }
                        catch (Exception ex)
                        {
                            item.TestResult = "ERROR";
                            MessageBox.Show("测试异常: " + ex.Message);
                        }
                        finally
                        {
                            // 4. 恢复按钮状态
                            item.BtnText = "开始测试";
                        }
                    }

                }
            }
        }

        // 单独检查某一路流是否拉流成功的函数，返回截图路径或者错误信息
        int waitStreamStableSeconds = 5; // 等待流稳定的时间，单位秒
        private async Task<bool> CheckStreamPlayedOK(string streamType, string ip, OpenCvRtspPlayer player, string testFolder)
        {
            try
            {
                string curPicPath = "";
                LogSaveOutput($"视频流测试是否播放正常：【{streamType}】，测试完成后会返回结果！");
                await rtspStreamOn(ip, player, streamType, $"视频流{streamType}");
                await Task.Delay(waitStreamStableSeconds * 1000);
                curPicPath = await SafeSnapshotAsync(player, testFolder, $"视频流{streamType}");
                await Task.Delay(3000);
                LogSaveOutput(curPicPath);
                if (WindowsFunc.IsImageValid(curPicPath))
                {
                    rtspStreamOff(player, $"视频流{streamType}");
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"检查 - {streamType} - 流是否正常拉取时发生异常: {ex.Message}");
                return false;
            }
        }


        int switchModeTime = 80; // 切换模式后等待的时间，单位秒
        private async void TestCase34(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置dhcp 为true，自动获取ip
            getNetWorkConfigBtn_Click(null, null);
            await Task.Delay(100);
            setUdhcpcBtn_Click(null, null);
            await Task.Delay(100);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        bool logic1 = false;
                        bool logic2 = false;
                        bool logic3 = false;
                        bool logic4 = false;
                        bool logic5 = false;
                        bool logic6 = false;
                        bool logic7 = false;

                        // 逻辑测试开始
                        //Logic 1 -  切到高分模式：高分模式-拉7路 [1、2、3、4、ai1、ai2、ai3]
                        hiResModeBtn_Click(null, null);
                        LogSaveOutput($"即将切到高分辨率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                        await Task.Delay(switchModeTime * 1000);

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        string stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                        string stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");
                        string stream3Result = await CheckStreamPlayedOK("3", _currentIp, player_CloseUpSub, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 3 测试结果为：{stream3Result}");
                        string stream4Result = await CheckStreamPlayedOK("4", _currentIp, player_panoramicSub, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 4 测试结果为：{stream4Result}");
                        string streamAI1Result = await CheckStreamPlayedOK("ai1", _currentIp, player_ai1, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - AI1 测试结果为：{streamAI1Result}");
                        string streamAI2Result = await CheckStreamPlayedOK("ai2", _currentIp, player_ai2, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - AI2 测试结果为：{streamAI2Result}");
                        string streamAI3Result = await CheckStreamPlayedOK("ai3", _currentIp, player_ai3, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - AI3 测试结果为：{streamAI3Result}");

                        List<string> streamResults = new List<string>() { stream1Result, stream2Result, stream3Result, stream4Result, streamAI1Result, streamAI2Result, streamAI3Result };

                        logic1 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                        LogSaveOutput($"逻辑1测试结果为：{(logic1 ? "PASS" : "FAIL")}");

                        if (logic1)
                        {
                            //Logic 2 -  切到高帧率模式：高帧模式-拉4路 [1、2、3、4]
                            hiFpsModeBtn_Click(null, null);
                            LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                            await Task.Delay(switchModeTime * 1000);

                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                            stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");
                            stream3Result = await CheckStreamPlayedOK("3", _currentIp, player_CloseUpSub, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 3 测试结果为：{stream3Result}");
                            stream4Result = await CheckStreamPlayedOK("4", _currentIp, player_panoramicSub, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 4 测试结果为：{stream4Result}");

                            streamResults = new List<string>() { stream1Result, stream2Result, stream3Result, stream4Result };

                            logic2 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                            LogSaveOutput($"逻辑2测试结果为：{(logic2 ? "PASS" : "FAIL")}");

                            if (logic2)
                            {
                                //Logic 3 -  切到性能模式：性能模式-拉2路 [extreme、extreme_2]
                                extremeModeBtn_Click(null, null);
                                LogSaveOutput($"即将切到性能模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                await Task.Delay(switchModeTime * 1000);

                                // 获取token
                                buttonGetToken_Click(null, null);
                                await Task.Delay(1000);

                                stream1Result = await CheckStreamPlayedOK("extreme", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                LogSaveOutput($"视频流 - extreme 测试结果为：{stream1Result}");
                                stream2Result = await CheckStreamPlayedOK("extreme_2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                LogSaveOutput($"视频流 - extreme_2 测试结果为：{stream2Result}");

                                streamResults = new List<string>() { stream1Result, stream2Result };

                                logic3 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                LogSaveOutput($"逻辑3测试结果为：{(logic3 ? "PASS" : "FAIL")}");

                                if (logic3)
                                {
                                    //Logic 4 -  切到高分模式：高分模式-拉7路 [1、2、3、4、ai1、ai2、ai3]
                                    hiResModeBtn_Click(null, null);
                                    LogSaveOutput($"即将切到高分辨率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                    await Task.Delay(switchModeTime * 1000);

                                    // 获取token
                                    buttonGetToken_Click(null, null);
                                    await Task.Delay(1000);

                                    stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                    stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");
                                    stream3Result = await CheckStreamPlayedOK("3", _currentIp, player_CloseUpSub, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - 3 测试结果为：{stream3Result}");
                                    stream4Result = await CheckStreamPlayedOK("4", _currentIp, player_panoramicSub, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - 4 测试结果为：{stream4Result}");
                                    streamAI1Result = await CheckStreamPlayedOK("ai1", _currentIp, player_ai1, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - AI1 测试结果为：{streamAI1Result}");
                                    streamAI2Result = await CheckStreamPlayedOK("ai2", _currentIp, player_ai2, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - AI2 测试结果为：{streamAI2Result}");
                                    streamAI3Result = await CheckStreamPlayedOK("ai3", _currentIp, player_ai3, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - AI3 测试结果为：{streamAI3Result}");

                                    streamResults = new List<string>() { stream1Result, stream2Result, stream3Result, stream4Result, streamAI1Result, streamAI2Result, streamAI3Result };

                                    logic4 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                    LogSaveOutput($"逻辑4测试结果为：{(logic4 ? "PASS" : "FAIL")}");

                                    if (logic4)
                                    {
                                        //Logic 5 -  切到高帧率模式：高帧模式-拉4路 [1、2、3、4]
                                        hiFpsModeBtn_Click(null, null);
                                        LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                        await Task.Delay(switchModeTime * 1000);

                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);

                                        stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                        stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");
                                        stream3Result = await CheckStreamPlayedOK("3", _currentIp, player_CloseUpSub, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 3 测试结果为：{stream3Result}");
                                        stream4Result = await CheckStreamPlayedOK("4", _currentIp, player_panoramicSub, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 4 测试结果为：{stream4Result}");

                                        streamResults = new List<string>() { stream1Result, stream2Result, stream3Result, stream4Result };

                                        logic5 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                        LogSaveOutput($"逻辑5测试结果为：{(logic5 ? "PASS" : "FAIL")}");

                                        if (logic5)
                                        {
                                            //Logic 6 -  切到性能模式：性能模式-拉2路 [extreme、extreme_2]
                                            extremeModeBtn_Click(null, null);
                                            LogSaveOutput($"即将切到性能模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                            await Task.Delay(switchModeTime * 1000);

                                            // 获取token
                                            buttonGetToken_Click(null, null);
                                            await Task.Delay(1000);

                                            stream1Result = await CheckStreamPlayedOK("extreme", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                            LogSaveOutput($"视频流 - extreme 测试结果为：{stream1Result}");
                                            stream2Result = await CheckStreamPlayedOK("extreme_2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                            LogSaveOutput($"视频流 - extreme_2 测试结果为：{stream2Result}");

                                            streamResults = new List<string>() { stream1Result, stream2Result };

                                            logic6 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                            LogSaveOutput($"逻辑6测试结果为：{(logic6 ? "PASS" : "FAIL")}");

                                            if (logic6)
                                            {
                                                //Logic 7 -  切到高帧率模式：高帧模式-拉4路 [1、2、3、4]
                                                hiFpsModeBtn_Click(null, null);
                                                LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                                await Task.Delay(switchModeTime * 1000);

                                                // 获取token
                                                buttonGetToken_Click(null, null);
                                                await Task.Delay(1000);

                                                stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                                stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");
                                                stream3Result = await CheckStreamPlayedOK("3", _currentIp, player_CloseUpSub, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - 3 测试结果为：{stream3Result}");
                                                stream4Result = await CheckStreamPlayedOK("4", _currentIp, player_panoramicSub, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - 4 测试结果为：{stream4Result}");

                                                streamResults = new List<string>() { stream1Result, stream2Result, stream3Result, stream4Result };

                                                logic7 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                                LogSaveOutput($"逻辑7测试结果为：{(logic7 ? "PASS" : "FAIL")}");

                                                if (logic7)
                                                {
                                                    LogSaveOutput("所有逻辑测试均通过，本轮测试结束！");
                                                }
                                                else
                                                {
                                                    LogSaveOutput("逻辑7测试失败，测试结束！");
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                LogSaveOutput("逻辑6测试失败，测试结束！");
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            LogSaveOutput("逻辑5测试失败，测试结束！");
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        LogSaveOutput("逻辑4测试失败，测试结束！");
                                        break;
                                    }
                                }
                                else
                                {
                                    LogSaveOutput("逻辑3测试失败，测试结束！");
                                    break;
                                }
                            }
                            else
                            {
                                LogSaveOutput("逻辑2测试失败，测试结束！");
                                break;
                            }
                        }
                        else
                        {
                            LogSaveOutput("逻辑1测试失败，测试结束！");
                            break;
                        }



                        // 结果呈现，次数增加
                        bool isSuccess = logic1 && logic2 && logic3 && logic4 && logic5 && logic6 && logic7;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase33(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前上下电5000次压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text;
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {
                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"{curTestIP_now} - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";

                                // 结果呈现，次数增加
                                bool isSuccess = false;
                                while (true)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试开始……");
                                    // 先下电
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(3000);
                                    // 再上电
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(3000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(3000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(3000);

                                    int bootCountTimes = 0;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(1000);
                                        string token_now = await apiTestItem.LoginAsync();
                                        LogSaveOutput($"token now is : {token_now}");
                                        if (bootCountTimes >= 300)
                                        {
                                            LogSaveOutput($"{curTestIP_now} - 测试结束，当前第{testCount}次上电重启5分钟超时，无法获取到token，请检查，测试停止！");
                                            item.TestResult = "FAIL";
                                            isSuccess = false;
                                            return;
                                        }
                                        if (!string.IsNullOrEmpty(token_now))
                                        {
                                            isSuccess = true;
                                            LogSaveOutput($"{curTestIP_now} - 第{testCount}次上电重启完成，Token 获取成功{token_now},设备重启完成，即将开始测试！");
                                            break;
                                        }
                                        else
                                        {
                                            LogSaveOutput($"{curTestIP_now} - Token 获取中，重启中，请稍等……");
                                            continue;
                                        }
                                    }

                                    LogSaveOutput($"{curTestIP_now} - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestCount++;
                                        item.TestResult = "PASS";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(10000);
                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束FAIL");
                                        return;
                                    }
                                }
                            });

                            await Task.Delay(3000);
                        }


                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase32(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前软重启压测测试项？",
                "点击确认后，当前测试会开始测试，望悉知！",
                AntdUI.TType.Warn));

            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }
                // 获取token
                buttonGetToken_Click(null, null);
                await Task.Delay(1000);
                bool isSuccess = false;
                this.BeginInvoke(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            // 触发重启 5000次
                            // 3. 更新测试结果
                            item.TestCount++; // 次数+1

                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            // 重启流程
                            rebootDevBtn_Click(null, null);
                            await Task.Delay(5000);

                            bool rebootResult = false;
                            int rebootCount = 0;
                            LogSaveOutput($"第{item.TestCount}轮重启开始……");
                            while (true)
                            {
                                rebootCount++;
                                await Task.Delay(1000);
                                string token = await _api.LoginAsync();
                                if (rebootCount >= 3000)
                                {
                                    LogSaveOutput($"测试结束，当前第{item.TestCount}次重启超时，无法获取到token，请检查，测试停止！");
                                    item.TestResult = "FAIL";
                                    isSuccess = false;
                                    return;
                                }
                                if (!string.IsNullOrEmpty(token))
                                {
                                    token_input.Text = token;
                                    isSuccess = true;
                                    LogSaveOutput($"第{item.TestCount}次重启完成，Token 获取成功,设备重启完成，即将开始下一次重启操作！");
                                    break;
                                }
                                else
                                {
                                    LogSaveOutput("Token 获取失败，重启中，请稍等……");
                                    continue;
                                }

                            }
                        }
                        catch (Exception ex)
                        {
                            isSuccess = false;
                            LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                        }

                        if (item.TestCount >= 5000)
                        {
                            isSuccess = true;
                            LogSaveOutput($"当前重启次数达到：{item.TestCount}, 软重启测试完成");
                            break;
                        }

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 重启结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                        }

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"【第{item.TestCount}次重启测试结束】");
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                        }
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase31(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);


                        // Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K
                        // 设置到uvc出特写模式
                        setUvcCloseUpBtn_Click(null, null);
                        await Task.Delay(100);
                        // uvc 拉流4K 0 3840x2160
                        int width = 3840;
                        int height = 2160;
                        input1_uvc_x.Text = width.ToString();
                        input2_uvc_y.Text = height.ToString();
                        string format = "H264";
                        string devicePath = null;


                        // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                        if (GetCameras("Seewo Lubo").Count > 1)
                        {
                            devicePath = input_curUvcDevicePath.Text; // 使用当前选中的设备路径
                        }
                        //如果devicepath是空,会运行默认摄像头
                        bool uvcStarted = await StartUVC(width, height, format, devicePath);
                        if (!uvcStarted)
                        {
                            LogSaveOutput("UVC 启动失败，停止测试");
                            break;
                        }
                        //等待12秒,预览
                        await Task.Delay(12000);
                        LogSaveOutput($"预览高分辨率模式教师UVC特写[{width}x{height}],格式{format} 12秒");


                        string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC特写[{width}x{height}]");
                        LogSaveOutput(uvc_pic);
                        await Task.Delay(100);

                        bool highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                        LogSaveOutput($"Logic1 -- uvc 特写[{width}x{height}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");

                        if (!highResolutionTeacherResult)
                        {
                            LogSaveOutput($"UVC异常，停止测试");
                            break;
                        }

                        // 先读取当前配置
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 设置主流到1080P - 30fps
                        LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                        LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));

                        // 特写主流拉流
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        bool closeUpMainResult = checkPICValid(closeUpMain_pic, closeUpMain_pic);
                        LogSaveOutput($"Logic1 -- rtsp 特写主流测试结果：{closeUpMainResult}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"Logic1 -- 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        if (highResolutionTeacherResult && closeUpMainResult && closeUpMainStatusResult)
                        {
                            // Logic 2 重新RTSP拉主流 + UCV 拉流MJPEG 1080P

                            // 1、先关流
                            // 所有流关流
                            uvc_streamOffBtn_Click(null, null);
                            await Task.Delay(100);
                            // 所有流关流
                            closeUpMainStreamOffBtn_Click(null, null);
                            await Task.Delay(100);

                            await Task.Delay(circleTestDelayTime * 1000);

                            // 2、再拉流
                            // logic 2 uvc 拉流1080P  1920x1080
                            input1_uvc_x.Text = "1920";
                            input2_uvc_y.Text = "1080";
                            input_Uvctype.Text = "H264";

                            // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                            if (GetCameras("Seewo Lubo").Count > 1)
                            {
                                uvcStreamOnSpecificDevicePathBtn_Click(null, null);
                            }
                            else
                            {
                                uvc_streamOnBtn_Click(null, null);
                            }
                            await Task.Delay(5000);
                            LogSaveOutput("预览10秒，请稍等……");
                            await Task.Delay(10000);

                            uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC特写[{1920}x{1080}]");
                            LogSaveOutput(uvc_pic);
                            await Task.Delay(100);

                            highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                            LogSaveOutput($"Logic2 -- uvc 特写主流[{1920}x{1080}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");
                            if (!highResolutionTeacherResult)
                            {
                                LogSaveOutput($"UVC异常，停止测试");
                                break;
                            }
                            // logic 2 rtsp 
                            // 特写主流拉流
                            closeUpMainStreamOnBtn_Click(null, null);
                            await Task.Delay(100);

                            // 特写主流拉流测试出结果
                            closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                            LogSaveOutput(closeUpMain_pic);
                            await Task.Delay(100);

                            closeUpMainResult = checkPICValid(closeUpMain_pic, closeUpMain_pic);
                            LogSaveOutput($"Logic2 -- rtsp 特写主流测试结果：{closeUpMainResult}");

                            LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                            await Task.Delay(checkStreamStatusWaitingTime);
                            // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                            closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                            LogSaveOutput($"Logic2 -- 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                            if (highResolutionTeacherResult && closeUpMainResult && closeUpMainStatusResult)
                            {
                                // logic 3 重新RTSP拉流 + 网络流设置为主流 4KP30帧、辅流默认，RTSP 拉流主流
                                // 1、先关流
                                // 所有流关流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                // 所有流关流
                                closeUpMainStreamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                await Task.Delay(circleTestDelayTime * 1000);
                                // rtsp 4k
                                // 先读取当前配置
                                readAllStreamCurConfigBtn_Click(null, null);
                                await Task.Delay(1000);

                                // 设置主流到4k - 30fps
                                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));

                                // 特写主流拉流
                                closeUpMainStreamOnBtn_Click(null, null);
                                await Task.Delay(100);

                                // 特写主流拉流测试出结果
                                closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                LogSaveOutput(closeUpMain_pic);
                                await Task.Delay(100);

                                closeUpMainResult = checkPICValid(closeUpMain_pic, closeUpMain_pic);
                                LogSaveOutput($"Logic3 -- rtsp 特写主流测试结果：{closeUpMainResult}");

                                LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                await Task.Delay(checkStreamStatusWaitingTime);
                                // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                LogSaveOutput($"Logic3 -- 当前特写主流4K30FPS状态测试结果：{closeUpMainStatusResult}");

                                if (closeUpMainResult && closeUpMainStatusResult)
                                {
                                    item.TestCount++;
                                    item.TestResult = "PASS";
                                    LogSaveOutput($"【Logic3 测试完成 -- 第{item.TestCount}次测试PASS，即将开始下一次测试……】");
                                    await Task.Delay(circleTestDelayTime * 1000);
                                    continue;
                                }
                                else
                                {
                                    LogSaveOutput("测试停止，当前Logic 3 测试失败：\n Logic 3 网络流设置为主流 4K 30帧、辅流默认，RTSP 拉流主流" +
                                    $"rtsp 结果：{closeUpMainResult} - {closeUpMainStatusResult}");
                                    item.TestResult = "FAIL";
                                    break;
                                }

                            }
                            else
                            {
                                LogSaveOutput("测试停止，当前Logic 1 测试失败：\n Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K" +
                                    $"uvc 结果：{highResolutionTeacherResult} + rtsp 结果：{closeUpMainResult} - {closeUpMainStatusResult}");
                                item.TestResult = "FAIL";
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }


                }
            });
        }



        private async void TestCase30(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);
            

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);


                        // Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K
                        // 设置到uvc出全景模式
                        setUvcPanoramicBtn_Click(null, null);
                        await Task.Delay(100);
                        int width = 3840;
                        int height = 2160;
                        input1_uvc_x.Text = width.ToString();
                        input2_uvc_y.Text = height.ToString();
                        string format = "H264";
                        string devicePath = null;
                        // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                        if (GetCameras("Seewo Lubo").Count > 1)
                        {
                            devicePath = input_curUvcDevicePath.Text; // 使用当前选中的设备路径
                        }
                        //没有输入path,默认null,会自动使用默认摄像头
                        bool uvcStarted = await StartUVC(width, height, format, devicePath);
                        if (!uvcStarted)
                        {
                            LogSaveOutput("UVC 启动失败，停止测试");
                            break;
                        }
                        
                        await Task.Delay(10000);//等待12秒使流稳定
                        LogSaveOutput($"预览高分辨率模式教师UVC全景[{width}x{height}] {format} - 10s");


                        string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC全景[{width}x{height}]");
                        LogSaveOutput(uvc_pic);
                        await Task.Delay(100);

                        bool highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                        LogSaveOutput($"Logic1 -- uvc 全景主流[{width}x{height}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");

                        if (!highResolutionTeacherResult)
                        {
                            LogSaveOutput($"UVC异常，停止测试");
                            break;
                        }


                        // 先读取当前配置
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 设置主流到1080P - 30fps
                        LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                        LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));

                        // 全景主流拉流
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        bool panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                        LogSaveOutput($"Logic1 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"Logic1 -- 当前全景主流状态测试结果：{panoramicMainStatusResult}");

                        if (highResolutionTeacherResult && panoramicMainResult && panoramicMainStatusResult)
                        {
                            // Logic 2 重新RTSP拉主流 + UCV 拉流MJPEG 1080P

                            // 1、先关流
                            // 所有流关流
                            uvc_streamOffBtn_Click(null, null);
                            await Task.Delay(100);
                            // 所有流关流
                            panoramicMainStreamOffBtn_Click(null, null);
                            await Task.Delay(100);
                            await Task.Delay(circleTestDelayTime * 1000);

                            // 2、再拉流
                            // logic 2 uvc 拉流1080P  1920x1080
                            input1_uvc_x.Text = "1920";
                            input2_uvc_y.Text = "1080";
                            input_Uvctype.Text = "H264";

                            // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                            if (GetCameras("Seewo Lubo").Count > 1)
                            {
                                uvcStreamOnSpecificDevicePathBtn_Click(null, null);
                            }
                            else
                            {
                                uvc_streamOnBtn_Click(null, null);
                            }
                            await Task.Delay(5000);
                            LogSaveOutput("预览10秒，请稍等……");
                            await Task.Delay(10000);

                            uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC全景[{1920}x{1080}]");
                            LogSaveOutput(uvc_pic);
                            await Task.Delay(100);

                            highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                            LogSaveOutput($"Logic2 -- uvc 全景主流[{1920}x{1080}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");
                            if (!highResolutionTeacherResult)
                            {
                                LogSaveOutput($"UVC异常，停止测试");
                                break;
                            }

                            // logic 2 rtsp 
                            // 全景主流拉流
                            panoramicMainStreamOnBtn_Click(null, null);
                            await Task.Delay(100);

                            // 全景主流拉流测试出结果
                            panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                            LogSaveOutput(panoramicMain_pic);
                            await Task.Delay(100);

                            panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                            LogSaveOutput($"Logic2 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                            LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                            await Task.Delay(checkStreamStatusWaitingTime);
                            // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                            panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                            LogSaveOutput($"Logic2 -- 当前全景主流状态测试结果：{panoramicMainStatusResult}");

                            if (highResolutionTeacherResult && panoramicMainResult && panoramicMainStatusResult)
                            {
                                // logic 3 重新RTSP拉流 + 网络流设置为主流 4KP30帧、辅流默认，RTSP 拉流主流
                                // 1、先关流
                                // 所有流关流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                // 所有流关流
                                panoramicMainStreamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                await Task.Delay(circleTestDelayTime * 1000);

                                // rtsp 4k
                                // 先读取当前配置
                                readAllStreamCurConfigBtn_Click(null, null);
                                await Task.Delay(1000);

                                // 设置主流到4k - 30fps
                                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));

                                // 全景主流拉流
                                panoramicMainStreamOnBtn_Click(null, null);
                                await Task.Delay(100);

                                // 全景主流拉流测试出结果
                                panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                LogSaveOutput(panoramicMain_pic);
                                await Task.Delay(100);

                                panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                                LogSaveOutput($"Logic3 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                                LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                await Task.Delay(checkStreamStatusWaitingTime);
                                // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                LogSaveOutput($"Logic3 -- 当前全景主流4K30FPS状态测试结果：{panoramicMainStatusResult}");

                                if (panoramicMainResult && panoramicMainStatusResult)
                                {
                                    item.TestCount++;
                                    item.TestResult = "PASS";
                                    LogSaveOutput($"【Logic3 测试完成 -- 第{item.TestCount}次测试PASS，即将开始下一次测试……】");
                                    await Task.Delay(circleTestDelayTime * 1000);
                                    continue;
                                }
                                else
                                {
                                    LogSaveOutput("测试停止，当前Logic 3 测试失败：\n Logic 3 网络流设置为主流 4K 30帧、辅流默认，RTSP 拉流主流" +
                                    $"rtsp 结果：{panoramicMainResult} - {panoramicMainStatusResult}");
                                    item.TestResult = "FAIL";
                                    break;
                                }

                            }
                            else
                            {
                                LogSaveOutput("测试停止，当前Logic 1 测试失败：\n Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K" +
                                    $"uvc 结果：{highResolutionTeacherResult} + rtsp 结果：{panoramicMainResult} - {panoramicMainStatusResult}");
                                item.TestResult = "FAIL";
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }


                }
            });
        }

        private async void TestCase29(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("性能模式流1", player_panoramicMain);
            openCvRtspPlayersDict.Add("性能模式流2", player_panoramicSub);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;

            // 切换到性能模式 -- 等待150秒
            LogSaveOutput("即将切换到性能模式，请稍等150秒……");
            extremeModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url} -- 预览10分钟，请稍等……");
                        await Task.Delay(60000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"性能模式下 -- 当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"性能模式下 -- {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }



        private async void TestCase28(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_extreme_pic, next_extreme_pic = "";
            string ori_extreme2_pic, next_extreme2_pic = "";

            // 切换到性能模式 -- 等待150秒
            LogSaveOutput("即将切换到性能模式，请稍等150秒……");
            extremeModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolutionBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeResolutionOrderBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);


                        // 每一路拉流，并比对结果
                        extreme1StreamOnBtn_Click(null, null);
                        await Task.Delay(5000);
                        extreme2StreamOnBtn_Click(null, null);
                        await Task.Delay(5000);

                        // 性能模式流1测试出结果
                        string extreme_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "性能模式流1");
                        LogSaveOutput(extreme_pic);
                        await Task.Delay(100);

                        // 性能模式流2测试出结果
                        string extreme2_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "性能模式流2");
                        LogSaveOutput(extreme2_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_extreme_pic = extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }
                        else
                        {
                            ori_extreme_pic = next_extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = next_extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }


                        bool extremeResult = checkPICValid(ori_extreme_pic, next_extreme_pic);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式后 -- 性能模式流 - 1测试结果：{extremeResult} -- {ori_extreme_pic} : {next_extreme_pic}");
                        bool extreme2Result = checkPICValid(ori_extreme2_pic, next_extreme2_pic);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式后 -- 性能模式流 - 2测试结果：{extreme2Result} -- {ori_extreme2_pic} : {next_extreme2_pic}");


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool extremeStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式流1状态测试结果：{extremeStatusResult}");
                        bool extreme2StatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式流2状态测试结果：{extreme2StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = extremeResult && extreme2Result
                        && extremeStatusResult && extreme2StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        extreme1StreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        extreme2StreamOffBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }



        private async void TestCase27(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("全景主流", player_panoramicMain);
            openCvRtspPlayersDict.Add("全景辅流", player_panoramicSub);
            openCvRtspPlayersDict.Add("特写主流", player_CloseUpMain);
            openCvRtspPlayersDict.Add("特写辅流", player_CloseUpSub);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;

            // 切换到高帧率模式 -- 等待150秒
            LogSaveOutput("即将切换到高帧率模式，请稍等150秒……");
            hiFpsModeBtn_Click(null, null);
            await Task.Delay(150000);


            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url}");
                        await Task.Delay(10000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"高帧率模式下 -- 当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"高帧率模式下 -- {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }

        private async void TestCase26(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");


            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";

            // 切换到高帧率模式 -- 等待150秒
            LogSaveOutput("即将切换到高帧率模式，请稍等150秒……");
            hiFpsModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {

                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolutionBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeResolutionOrderBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");

                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase25(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前逻辑2上下电间歇时间短电压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text.Trim();
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {

                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"【{curTestIP_now}】 - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化

                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";
                                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                                string ori_panoramicSub_pic, next_panoramicSub_pic = "";
                                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                                string ori_closeUpSub_pic, next_closeUpSub_pic = "";
                                string ori_ai1_pic, next_ai1_pic = "";
                                string ori_ai2_pic, next_ai2_pic = "";
                                string ori_ai3_pic, next_ai3_pic = "";
                                int onWaitTime = 5000, offWaitTime = 5000;
                                while (true)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试开始……");
                                    // 先下电
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    // 再上电
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);

                                    int bootCountTimes = 0;
                                    bool pingSuccess = false;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(5000);
                                        WindowsFunc.IsHostReachableAsync(curTestIP_now).ContinueWith(reachabilityTask =>
                                        {
                                            if (reachabilityTask.Result)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备在线");
                                                pingSuccess = true;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备离线");
                                            }
                                        });
                                        if (pingSuccess)
                                        {
                                            apiTestItem = null;
                                            apiTestItem = new HttpApi_stu(curTestIP_now);
                                            string token_now = await apiTestItem.LoginAsync();
                                            if (bootCountTimes >= 3000)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 测试结束，当前第{item.TestCount}次上电重启超时，无法获取到token，请检查，测试停止！");
                                                item.TestResult = "FAIL";
                                                return;
                                            }
                                            if (!string.IsNullOrEmpty(token_now))
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 第{item.TestCount}次上电重启完成，Token 获取成功,设备重启完成，即将开始测试！");
                                                break;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败，重启中，请稍等……");
                                                continue;
                                            }
                                        }
                                    }

                                    // 开始拉流测试 -- 更新item的testcount和result
                                    LogSaveOutput($"【{curTestIP_now}】 - 拉流测试中……");
                                    await Task.Delay(onWaitTime);

                                    // 1. 先在外部定义变量，确保后面能访问到
                                    OpenCvRtspPlayer pm = null, ps = null, cm = null, cs = null, ai1 = null, ai2 = null, ai3 = null;
                                    PictureBox pb1 = null, pb2 = null, pb3 = null, pb4 = null, pb5 = null, pb6 = null, pb7 = null;
                                    bool isSuccess = false;
                                    void SafeDisposePlayer(OpenCvRtspPlayer player)
                                    {
                                        try { player?.Dispose(); } catch { }
                                    }

                                    void SafeDisposePictureBox(PictureBox pb)
                                    {
                                        if (pb == null) return;
                                        if (IsDisposed || Disposing)
                                        {
                                            try { pb.Dispose(); } catch { }
                                            return;
                                        }
                                        BeginInvoke(new Action(() =>
                                        {
                                            try
                                            {
                                                if (pb.Parent != null)
                                                {
                                                    pb.Parent.Controls.Remove(pb);
                                                }
                                                pb.Dispose();
                                            }
                                            catch { }
                                        }));
                                    }

                                    try
                                    {
                                        // 2. 使用 Invoke 强制回到主 UI 线程执行控件创建（解决卡死/报错的关键）
                                        this.Invoke(new Action(() =>
                                        {
                                            // 定义一个临时的本地函数，避免重复写 7 遍相同的代码
                                            PictureBox CreateHiddenPb()
                                            {
                                                var pb = new PictureBox()
                                                {
                                                    Size = new System.Drawing.Size(320, 180), // 保持合理大小以确保画质
                                                    Location = new System.Drawing.Point(-10000, -10000), // 移出屏幕
                                                    Visible = true, // 必须为 true，否则 OpenCvPlayer 逻辑会跳过解码
                                                    Parent = this   // 明确指定父容器
                                                };
                                                this.Controls.Add(pb); // 必须加入窗体集合
                                                return pb;
                                            }

                                            // 批量初始化
                                            pb1 = CreateHiddenPb(); pm = new OpenCvRtspPlayer(pb1);
                                            pb2 = CreateHiddenPb(); ps = new OpenCvRtspPlayer(pb2);
                                            pb3 = CreateHiddenPb(); cm = new OpenCvRtspPlayer(pb3);
                                            pb4 = CreateHiddenPb(); cs = new OpenCvRtspPlayer(pb4);
                                            pb5 = CreateHiddenPb(); ai1 = new OpenCvRtspPlayer(pb5);
                                            pb6 = CreateHiddenPb(); ai2 = new OpenCvRtspPlayer(pb6);
                                            pb7 = CreateHiddenPb(); ai3 = new OpenCvRtspPlayer(pb7);
                                        }));

                                        // 每一路拉流，并比对结果
                                        await rtspStreamOn(curTestIP_now, pm, "2", "全景主流");
                                        await rtspStreamOn(curTestIP_now, ps, "4", "全景辅流");
                                        await rtspStreamOn(curTestIP_now, cm, "1", "特写主流");
                                        await rtspStreamOn(curTestIP_now, cs, "3", "特写辅流");
                                        await rtspStreamOn(curTestIP_now, ai1, "ai1", "ai1");
                                        await rtspStreamOn(curTestIP_now, ai2, "ai2", "ai2");
                                        await rtspStreamOn(curTestIP_now, ai3, "ai3", "ai3");


                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(pm, testFolder_item, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);

                                        // 全景辅流拉流测试出结果
                                        string panoramicSub_pic = await SafeSnapshotAsync(ps, testFolder_item, "全景辅流");
                                        LogSaveOutput(panoramicSub_pic);
                                        await Task.Delay(100);

                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(cm, testFolder_item, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);

                                        // 特写辅流拉流测试出结果
                                        string closeUpSub_pic = await SafeSnapshotAsync(cs, testFolder_item, "特写辅流");
                                        LogSaveOutput(closeUpSub_pic);
                                        await Task.Delay(100);

                                        // AI1前排流拉流测试出结果
                                        string ai1_pic = await SafeSnapshotAsync(ai1, testFolder_item, "AI1前排流");
                                        LogSaveOutput(ai1_pic);
                                        await Task.Delay(100);

                                        // AI左后排流拉流测试出结果
                                        string ai2_pic = await SafeSnapshotAsync(ai2, testFolder_item, "AI左后排流");
                                        LogSaveOutput(ai2_pic);
                                        await Task.Delay(100);

                                        // AI右后排流拉流测试出结果
                                        string ai3_pic = await SafeSnapshotAsync(ai3, testFolder_item, "AI右后排流");
                                        LogSaveOutput(ai3_pic);
                                        await Task.Delay(100);

                                        if (testCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                                        LogSaveOutput($"【{curTestIP_now}】 - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(pm);
                                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool panoramicSubStatusResult = getStreamStatusResult(ps);
                                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(cm);
                                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                                        bool closeUpSubStatusResult = getStreamStatusResult(cs);
                                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                                        bool ai1StatusResult = getStreamStatusResult(ai1);
                                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                                        bool ai2StatusResult = getStreamStatusResult(ai2);
                                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                                        bool ai3StatusResult = getStreamStatusResult(ai3);
                                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");


                                        // 结果呈现，次数增加
                                        isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                                        // 所有流关流

                                        rtspStreamOff(pm, "全景主流");
                                        rtspStreamOff(ps, "全景辅流");
                                        rtspStreamOff(cm, "特写主流");
                                        rtspStreamOff(cs, "特写辅流");
                                        rtspStreamOff(ai1, "ai1流");
                                        rtspStreamOff(ai2, "ai2流");
                                        rtspStreamOff(ai3, "ai3流");
                                    }
                                    finally
                                    {
                                        SafeDisposePlayer(pm);
                                        SafeDisposePlayer(ps);
                                        SafeDisposePlayer(cm);
                                        SafeDisposePlayer(cs);
                                        SafeDisposePlayer(ai1);
                                        SafeDisposePlayer(ai2);
                                        SafeDisposePlayer(ai3);

                                        SafeDisposePictureBox(pb1);
                                        SafeDisposePictureBox(pb2);
                                        SafeDisposePictureBox(pb3);
                                        SafeDisposePictureBox(pb4);
                                        SafeDisposePictureBox(pb5);
                                        SafeDisposePictureBox(pb6);
                                        SafeDisposePictureBox(pb7);
                                    }

                                    LogSaveOutput($"【{curTestIP_now}】 - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestResult = "PASS";
                                        item.TestCount++;
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(offWaitTime);

                                        // 修改间歇上下电逻辑
                                        if (testCount == 500)
                                        {
                                            onWaitTime = 25000;
                                            offWaitTime = 10000;
                                        }
                                        if (testCount == 1000)
                                        {
                                            onWaitTime = 30000;
                                            offWaitTime = 5000;
                                        }

                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束FAIL");
                                        return;
                                    }


                                }
                            });

                            await Task.Delay(3000);
                        }


                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }


        private async void TestCase24(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前逻辑1上下电5000次压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text.Trim();
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {
                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"【{curTestIP_now}】 - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";
                                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                                string ori_panoramicSub_pic, next_panoramicSub_pic = "";
                                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                                string ori_closeUpSub_pic, next_closeUpSub_pic = "";
                                string ori_ai1_pic, next_ai1_pic = "";
                                string ori_ai2_pic, next_ai2_pic = "";
                                string ori_ai3_pic, next_ai3_pic = "";
                                while (true)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试开始……");
                                    // 先下电
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    // 再上电
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);

                                    int bootCountTimes = 0;
                                    bool pingSuccess = false;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(5000);
                                        WindowsFunc.IsHostReachableAsync(curTestIP_now).ContinueWith(reachabilityTask =>
                                        {
                                            if (reachabilityTask.Result)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备在线");
                                                pingSuccess = true;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备离线");
                                            }
                                        });
                                        if (pingSuccess)
                                        {
                                            apiTestItem = null; // 先释放之前的实例，确保资源清理
                                            apiTestItem = new HttpApi_stu(curTestIP_now);
                                            string token_now = await apiTestItem.LoginAsync();
                                            if (bootCountTimes >= 3000)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 测试结束，当前第{item.TestCount}次上电重启超时，无法获取到token，请检查，测试停止！");
                                                item.TestResult = "FAIL";
                                                return;
                                            }
                                            if (!string.IsNullOrEmpty(token_now))
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 第{item.TestCount}次上电重启完成，Token 获取成功,设备重启完成，即将开始测试！");
                                                break;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败，重启中，请稍等……");
                                                continue;
                                            }
                                        }
                                    }

                                    // 开始拉流测试 -- 更新item的testcount和result
                                    LogSaveOutput($"【{curTestIP_now}】 - 拉流测试中……");
                                    await Task.Delay(5000);

                                    // 1. 先在外部定义变量，确保后面能访问到
                                    OpenCvRtspPlayer pm = null, ps = null, cm = null, cs = null, ai1 = null, ai2 = null, ai3 = null;
                                    PictureBox pb1 = null, pb2 = null, pb3 = null, pb4 = null, pb5 = null, pb6 = null, pb7 = null;
                                    bool isSuccess = false;
                                    void SafeDisposePlayer(OpenCvRtspPlayer player)
                                    {
                                        try { player?.Dispose(); } catch { }
                                    }

                                    void SafeDisposePictureBox(PictureBox pb)
                                    {
                                        if (pb == null) return;
                                        if (IsDisposed || Disposing)
                                        {
                                            try { pb.Dispose(); } catch { }
                                            return;
                                        }
                                        BeginInvoke(new Action(() =>
                                        {
                                            try
                                            {
                                                if (pb.Parent != null)
                                                {
                                                    pb.Parent.Controls.Remove(pb);
                                                }
                                                pb.Dispose();
                                            }
                                            catch { }
                                        }));
                                    }


                                    try
                                    {
                                        // 2. 使用 Invoke 强制回到主 UI 线程执行控件创建（解决卡死/报错的关键）
                                        this.Invoke(new Action(() =>
                                        {
                                            // 定义一个临时的本地函数，避免重复写 7 遍相同的代码
                                            PictureBox CreateHiddenPb()
                                            {
                                                var pb = new PictureBox()
                                                {
                                                    Size = new System.Drawing.Size(320, 180), // 保持合理大小以确保画质
                                                    Location = new System.Drawing.Point(-10000, -10000), // 移出屏幕
                                                    Visible = true, // 必须为 true，否则 OpenCvPlayer 逻辑会跳过解码
                                                    Parent = this   // 明确指定父容器
                                                };
                                                this.Controls.Add(pb); // 必须加入窗体集合
                                                return pb;
                                            }

                                            // 批量初始化
                                            pb1 = CreateHiddenPb(); pm = new OpenCvRtspPlayer(pb1);
                                            pb2 = CreateHiddenPb(); ps = new OpenCvRtspPlayer(pb2);
                                            pb3 = CreateHiddenPb(); cm = new OpenCvRtspPlayer(pb3);
                                            pb4 = CreateHiddenPb(); cs = new OpenCvRtspPlayer(pb4);
                                            pb5 = CreateHiddenPb(); ai1 = new OpenCvRtspPlayer(pb5);
                                            pb6 = CreateHiddenPb(); ai2 = new OpenCvRtspPlayer(pb6);
                                            pb7 = CreateHiddenPb(); ai3 = new OpenCvRtspPlayer(pb7);
                                        }));

                                        // 每一路拉流，并比对结果
                                        await rtspStreamOn(curTestIP_now, pm, "2", "全景主流");
                                        await rtspStreamOn(curTestIP_now, ps, "4", "全景辅流");
                                        await rtspStreamOn(curTestIP_now, cm, "1", "特写主流");
                                        await rtspStreamOn(curTestIP_now, cs, "3", "特写辅流");
                                        await rtspStreamOn(curTestIP_now, ai1, "ai1", "ai1");
                                        await rtspStreamOn(curTestIP_now, ai2, "ai2", "ai2");
                                        await rtspStreamOn(curTestIP_now, ai3, "ai3", "ai3");


                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(pm, testFolder_item, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);

                                        // 全景辅流拉流测试出结果
                                        string panoramicSub_pic = await SafeSnapshotAsync(ps, testFolder_item, "全景辅流");
                                        LogSaveOutput(panoramicSub_pic);
                                        await Task.Delay(100);

                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(cm, testFolder_item, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);

                                        // 特写辅流拉流测试出结果
                                        string closeUpSub_pic = await SafeSnapshotAsync(cs, testFolder_item, "特写辅流");
                                        LogSaveOutput(closeUpSub_pic);
                                        await Task.Delay(100);

                                        // AI1前排流拉流测试出结果
                                        string ai1_pic = await SafeSnapshotAsync(ai1, testFolder_item, "AI1前排流");
                                        LogSaveOutput(ai1_pic);
                                        await Task.Delay(100);

                                        // AI左后排流拉流测试出结果
                                        string ai2_pic = await SafeSnapshotAsync(ai2, testFolder_item, "AI左后排流");
                                        LogSaveOutput(ai2_pic);
                                        await Task.Delay(100);

                                        // AI右后排流拉流测试出结果
                                        string ai3_pic = await SafeSnapshotAsync(ai3, testFolder_item, "AI右后排流");
                                        LogSaveOutput(ai3_pic);
                                        await Task.Delay(100);

                                        if (testCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                                        LogSaveOutput($"【{curTestIP_now}】 - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(pm);
                                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool panoramicSubStatusResult = getStreamStatusResult(ps);
                                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(cm);
                                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                                        bool closeUpSubStatusResult = getStreamStatusResult(cs);
                                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                                        bool ai1StatusResult = getStreamStatusResult(ai1);
                                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                                        bool ai2StatusResult = getStreamStatusResult(ai2);
                                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                                        bool ai3StatusResult = getStreamStatusResult(ai3);
                                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                                        // 结果呈现，次数增加
                                        isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                                        // 所有流关流

                                        rtspStreamOff(pm, "全景主流");
                                        rtspStreamOff(ps, "全景辅流");
                                        rtspStreamOff(cm, "特写主流");
                                        rtspStreamOff(cs, "特写辅流");
                                        rtspStreamOff(ai1, "ai1流");
                                        rtspStreamOff(ai2, "ai2流");
                                        rtspStreamOff(ai3, "ai3流");
                                    }
                                    finally
                                    {
                                        SafeDisposePlayer(pm);
                                        SafeDisposePlayer(ps);
                                        SafeDisposePlayer(cm);
                                        SafeDisposePlayer(cs);
                                        SafeDisposePlayer(ai1);
                                        SafeDisposePlayer(ai2);
                                        SafeDisposePlayer(ai3);

                                        SafeDisposePictureBox(pb1);
                                        SafeDisposePictureBox(pb2);
                                        SafeDisposePictureBox(pb3);
                                        SafeDisposePictureBox(pb4);
                                        SafeDisposePictureBox(pb5);
                                        SafeDisposePictureBox(pb6);
                                        SafeDisposePictureBox(pb7);
                                    }

                                    LogSaveOutput($"【{curTestIP_now}】 - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestCount++;
                                        item.TestResult = "PASS";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(10000);

                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束FAIL");
                                        return;
                                    }
                                }
                            });

                            await Task.Delay(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }


        private async void TestCase23(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前重启5000次压测测试项？",
                "点击确认后，当前测试会开始，设备将会先重启5000次，之后进行拉流测试，望悉知！",
                AntdUI.TType.Warn));

            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }
                // 获取token
                buttonGetToken_Click(null, null);
                await Task.Delay(1000);

                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                string ori_panoramicSub_pic, next_panoramicSub_pic = "";
                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                string ori_closeUpSub_pic, next_closeUpSub_pic = "";
                string ori_ai1_pic, next_ai1_pic = "";
                string ori_ai2_pic, next_ai2_pic = "";
                string ori_ai3_pic, next_ai3_pic = "";

                this.BeginInvoke(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            // 触发重启 5000次
                            // 3. 更新测试结果
                            item.TestCount++; // 次数+1

                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            // 重启流程
                            rebootDevBtn_Click(null, null);
                            await Task.Delay(5000);

                            bool rebootResult = false;
                            int rebootCount = 0;
                            LogSaveOutput($"第{item.TestCount}轮重启开始……");
                            while (true)
                            {
                                rebootCount++;
                                await Task.Delay(1000);
                                string token = await _api.LoginAsync();
                                if (rebootCount >= 5000)
                                {
                                    LogSaveOutput($"测试结束，当前第{item.TestCount}次重启超时，无法获取到token，请检查，测试停止！");
                                    item.TestResult = "FAIL";
                                    return;
                                }
                                if (stopTest)
                                {
                                    LogSaveOutput("手动停止测试！");
                                }
                                if (!string.IsNullOrEmpty(token))
                                {
                                    token_input.Text = token;
                                    LogSaveOutput($"第{item.TestCount}次重启完成，Token 获取成功,设备重启完成，即将开始下一次重启操作！");
                                    break;
                                }
                                else
                                {
                                    LogSaveOutput("Token 获取失败，重启中，请稍等……");
                                    continue;
                                }

                            }
                        }
                        catch (Exception ex)
                        {
                            LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                        }

                        if (item.TestCount >= 5000)
                        {
                            LogSaveOutput($"当前重启次数达到：{item.TestCount}, 即将开始拉流测试");
                            break;
                        }
                    }


                    await Task.Delay(5000);
                    // 进行拉流压测
                    // 每一路拉流，并比对结果
                    panoramicMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    panoramicSubStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpSubStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai1StreanOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai2StreanOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai3StreanOnBtn_Click(null, null);
                    await Task.Delay(100);

                    // 全景主流拉流测试出结果
                    string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                    LogSaveOutput(panoramicMain_pic);
                    await Task.Delay(100);

                    // 全景辅流拉流测试出结果
                    string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                    LogSaveOutput(panoramicSub_pic);
                    await Task.Delay(100);

                    // 特写主流拉流测试出结果
                    string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 特写辅流拉流测试出结果
                    string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                    LogSaveOutput(closeUpSub_pic);
                    await Task.Delay(100);

                    // AI1前排流拉流测试出结果
                    string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                    LogSaveOutput(ai1_pic);
                    await Task.Delay(100);

                    // AI左后排流拉流测试出结果
                    string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                    LogSaveOutput(ai2_pic);
                    await Task.Delay(100);

                    // AI右后排流拉流测试出结果
                    string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                    LogSaveOutput(ai3_pic);
                    await Task.Delay(100);

                    if (item.TestCount == 1)
                    {
                        ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                        ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                        ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                        ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                        ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                    }
                    else
                    {
                        ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                        ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                        ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                        ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                        ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                    }


                    bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                    bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                    bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                    bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                    bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                    bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                    bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                    LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                    await Task.Delay(checkStreamStatusWaitingTime);
                    // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                    bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                    LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                    bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                    LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                    bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                    LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                    bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                    LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                    bool ai1StatusResult = getStreamStatusResult(player_ai1);
                    LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                    bool ai2StatusResult = getStreamStatusResult(player_ai2);
                    LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                    bool ai3StatusResult = getStreamStatusResult(player_ai3);
                    LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                    // 结果呈现，次数增加
                    bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                    && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                    // 所有流关流
                    panoramicMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    panoramicSubStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpSubStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai1StreanOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai2StreanOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai3StreanOffBtn_Click(null, null);
                    await Task.Delay(100);

                    LogSaveOutput($"{item.Name} 第{item.TestCount}次 重启后拉流结束，测试结果为：{item.TestResult}");
                    if (stopTest)
                    {
                        LogSaveOutput("手动停止测试！");
                    }

                    if (isSuccess)
                    {
                        item.TestCount++;
                        item.TestResult = "PASS";
                        LogSaveOutput($"【第{item.TestCount}次重启后拉流测试结束】");
                    }
                    else
                    {
                        item.TestResult = "FAIL";
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }


        private async void TestCase22(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前U盘升级拉流压测测试项？",
                "点击确认后，当前测试会开始，请务必保证已经把U盘插入，并且把固件放到U盘里面！望悉知！",
                AntdUI.TType.Warn));

            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                // 3. 更新测试结果
                item.TestCount++; // 次数+1

                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                // 获取token
                buttonGetToken_Click(null, null);
                await Task.Delay(1000);


                if (SKDL0503_CB.Checked)
                {
                    // 如果是0503项目，就不测AI流
                    string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                    string ori_panoramicSub_pic, next_panoramicSub_pic = "";
                    string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                    string ori_closeUpSub_pic, next_closeUpSub_pic = "";

                    this.BeginInvoke(async () =>
                    {
                        while (true)
                        {
                            try
                            {// U盘升级拉流压测
                                string nextUpgradePath = await getSysVersion();
                                bool upgradeResult = false;

                                int update_checkVersionCount = 0;
                                // oU盘升级测试结果更新
                                if (await _api.RebootCurDevice() == "success")
                                {
                                    LogSaveOutput("重启U盘升级中，请稍等！");
                                    await Task.Delay(10000);
                                    // U盘升级进度完成，等待重启完成
                                    while (true)
                                    {
                                        update_checkVersionCount += 1;
                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);
                                        string upgradeDoneVersion = await _api.GetSysVerison();
                                        string diskStatus = await _api.GetDiskStatus();
                                        if (upgradeDoneVersion != null)
                                        {
                                            if (nextUpgradePath.Contains(upgradeDoneVersion) && diskStatus.Contains("SUCCESS"))
                                            {
                                                item.TestResult = "PASS";
                                                upgradeResult = true;
                                                LogSaveOutput($"设备【{_currentIp}】U盘升级完成，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");

                                                // 升级成功，即将开始拉流压测
                                                // 每一路拉流，并比对结果
                                                panoramicMainStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                panoramicSubStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpMainStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpSubStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);

                                                // 全景主流拉流测试出结果
                                                string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                                LogSaveOutput(panoramicMain_pic);
                                                await Task.Delay(100);

                                                // 全景辅流拉流测试出结果
                                                string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                                                LogSaveOutput(panoramicSub_pic);
                                                await Task.Delay(100);

                                                // 特写主流拉流测试出结果
                                                string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                                LogSaveOutput(closeUpMain_pic);
                                                await Task.Delay(100);

                                                // 特写辅流拉流测试出结果
                                                string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                                                LogSaveOutput(closeUpSub_pic);
                                                await Task.Delay(100);


                                                if (item.TestCount == 1)
                                                {
                                                    ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                    ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                                    ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                                    ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                                }
                                                else
                                                {
                                                    ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                    ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                                    ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                                    ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                                }


                                                bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                                bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                                                bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                                                bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");

                                                LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                                await Task.Delay(checkStreamStatusWaitingTime);
                                                // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                                bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                                LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                                bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                                                LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                                                bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                                LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                                                bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                                                LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");

                                                // 结果呈现，次数增加
                                                bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult
                                                && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult;

                                                // 所有流关流
                                                panoramicMainStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                panoramicSubStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpMainStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpSubStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);

                                                LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                                                if (stopTest)
                                                {
                                                    LogSaveOutput("手动停止测试！");
                                                    return;
                                                }

                                                if (isSuccess)
                                                {
                                                    item.TestCount++;
                                                    item.TestResult = "PASS";
                                                    LogSaveOutput($"【第{item.TestCount}次U盘升级后拉流测试结束，下一次测试即将开始……】");
                                                    break;
                                                }
                                                else
                                                {
                                                    item.TestResult = "FAIL";
                                                    return;
                                                }

                                            }
                                            else
                                            {
                                                LogSaveOutput($"设备【{_currentIp}】U盘升级失败，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");
                                                item.TestResult = "FAIL";
                                                upgradeResult = false;
                                                return;
                                            }
                                        }
                                        if (update_checkVersionCount >= 300)
                                        {
                                            item.TestResult = "FAIL";
                                            upgradeResult = false;
                                            LogSaveOutput($"长时间没有起来，当前设备 【{_currentIp}】 U盘升级失败，期望版本：【{nextUpgradePath}】");
                                            return;
                                        }
                                    }
                                }
                                else
                                {
                                    upgradeResult = false;
                                    item.TestResult = "FAIL";
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                            }

                        }
                    });
                }
                else
                {
                    // 其他项目正常测试
                    string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                    string ori_panoramicSub_pic, next_panoramicSub_pic = "";
                    string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                    string ori_closeUpSub_pic, next_closeUpSub_pic = "";
                    string ori_ai1_pic, next_ai1_pic = "";
                    string ori_ai2_pic, next_ai2_pic = "";
                    string ori_ai3_pic, next_ai3_pic = "";

                    this.BeginInvoke(async () =>
                    {
                        while (true)
                        {
                            try
                            {// U盘升级拉流压测
                                string nextUpgradePath = await getSysVersion();
                                bool upgradeResult = false;

                                int update_checkVersionCount = 0;
                                // oU盘升级测试结果更新
                                if (await _api.RebootCurDevice() == "success")
                                {
                                    LogSaveOutput("重启U盘升级中，请稍等！");
                                    await Task.Delay(10000);
                                    // U盘升级进度完成，等待重启完成
                                    while (true)
                                    {
                                        update_checkVersionCount += 1;
                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);
                                        string upgradeDoneVersion = await _api.GetSysVerison();
                                        if (upgradeDoneVersion != null)
                                        {
                                            if (nextUpgradePath.Contains(upgradeDoneVersion))
                                            {
                                                item.TestResult = "PASS";
                                                upgradeResult = true;
                                                LogSaveOutput($"设备【{_currentIp}】U盘升级完成，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");

                                                // 升级成功，即将开始拉流压测
                                                // 每一路拉流，并比对结果
                                                panoramicMainStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                panoramicSubStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpMainStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpSubStreamOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai1StreanOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai2StreanOnBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai3StreanOnBtn_Click(null, null);
                                                await Task.Delay(100);

                                                // 全景主流拉流测试出结果
                                                string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                                LogSaveOutput(panoramicMain_pic);
                                                await Task.Delay(100);

                                                // 全景辅流拉流测试出结果
                                                string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                                                LogSaveOutput(panoramicSub_pic);
                                                await Task.Delay(100);

                                                // 特写主流拉流测试出结果
                                                string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                                LogSaveOutput(closeUpMain_pic);
                                                await Task.Delay(100);

                                                // 特写辅流拉流测试出结果
                                                string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                                                LogSaveOutput(closeUpSub_pic);
                                                await Task.Delay(100);

                                                // AI1前排流拉流测试出结果
                                                string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                                                LogSaveOutput(ai1_pic);
                                                await Task.Delay(100);

                                                // AI左后排流拉流测试出结果
                                                string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                                                LogSaveOutput(ai2_pic);
                                                await Task.Delay(100);

                                                // AI右后排流拉流测试出结果
                                                string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                                                LogSaveOutput(ai3_pic);
                                                await Task.Delay(100);

                                                if (item.TestCount == 1)
                                                {
                                                    ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                    ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                                    ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                                    ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                                    ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                                                    ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                                                    ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                                                }
                                                else
                                                {
                                                    ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                    ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                                    ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                                    ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                                    ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                                                    ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                                                    ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                                                }


                                                bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                                bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                                                bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                                                bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                                                bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                                                bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                                                bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                                                LogSaveOutput($"u盘升级后拉流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                                                LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                                await Task.Delay(checkStreamStatusWaitingTime);
                                                // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                                bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                                LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                                bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                                                LogSaveOutput($"当前全景辅流状态测试结果：{panoramicMainStatusResult}");
                                                bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                                LogSaveOutput($"当前特写主流状态测试结果：{panoramicMainStatusResult}");
                                                bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                                                LogSaveOutput($"当前特写辅流状态测试结果：{panoramicMainStatusResult}");
                                                bool ai1StatusResult = getStreamStatusResult(player_ai1);
                                                LogSaveOutput($"当前AI1流状态测试结果：{panoramicMainStatusResult}");
                                                bool ai2StatusResult = getStreamStatusResult(player_ai2);
                                                LogSaveOutput($"当前AI2左后排流状态测试结果：{panoramicMainStatusResult}");
                                                bool ai3StatusResult = getStreamStatusResult(player_ai3);
                                                LogSaveOutput($"当前AI3右后排流状态测试结果：{panoramicMainStatusResult}");

                                                // 结果呈现，次数增加
                                                bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                                                && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                                                // 所有流关流
                                                panoramicMainStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                panoramicSubStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpMainStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                closeUpSubStreamOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai1StreanOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai2StreanOffBtn_Click(null, null);
                                                await Task.Delay(100);
                                                ai3StreanOffBtn_Click(null, null);
                                                await Task.Delay(100);

                                                LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                                                if (stopTest)
                                                {
                                                    LogSaveOutput("手动停止测试！");
                                                    return;
                                                }

                                                if (isSuccess)
                                                {
                                                    item.TestCount++;
                                                    item.TestResult = "PASS";
                                                    LogSaveOutput($"【第{item.TestCount}次U盘升级后拉流测试结束，下一次测试即将开始……】");
                                                    break;
                                                }
                                                else
                                                {
                                                    item.TestResult = "FAIL";
                                                    return;
                                                }

                                            }
                                            else
                                            {
                                                LogSaveOutput($"设备【{_currentIp}】U盘升级失败，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");
                                                item.TestResult = "FAIL";
                                                upgradeResult = false;
                                                return;
                                            }
                                        }
                                        if (update_checkVersionCount >= 300)
                                        {
                                            item.TestResult = "FAIL";
                                            upgradeResult = false;
                                            LogSaveOutput($"长时间没有起来，当前设备 【{_currentIp}】 U盘升级失败，期望版本：【{nextUpgradePath}】");
                                            return;
                                        }
                                    }
                                }
                                else
                                {
                                    upgradeResult = false;
                                    item.TestResult = "FAIL";
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                            }

                        }
                    });
                }


            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }


        private async void TestCase21(TestCases item)
        {
            LogSaveOutput($"{_currentIp} - 测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"{_currentIp} - 测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置dhcp 为true，自动获取ip
            getNetWorkConfigBtn_Click(null, null);
            await Task.Delay(100);
            setUdhcpcBtn_Click(null, null);
            await Task.Delay(100);

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 双版本循环分区OTA升级流程
                        string curSysVersion = await getSysVersion();
                        string ota1Path = input_otaPacketPath1.Text;
                        string ota2Path = input_otaPacketPath2.Text;
                        string nextUpgradePath = "";
                        bool upgradeResult = false;

                        LogSaveOutput($"{_currentIp} - 【OTA1：{ota1Path}】");
                        LogSaveOutput($"{_currentIp} - 【OTA2：{ota2Path}】");
                        if (ota1Path.Contains(curSysVersion))
                        {
                            LogSaveOutput($"{_currentIp} - Ready to : OTA2PATH : {ota2Path} -- {curSysVersion}");
                            nextUpgradePath = ota2Path;
                        }
                        else if (ota2Path.Contains(curSysVersion))
                        {
                            LogSaveOutput($"{_currentIp} - Ready to : OTA1PATH : {ota1Path} -- {curSysVersion}");
                            nextUpgradePath = ota1Path;
                        }
                        else
                        {
                            LogSaveOutput($"{_currentIp} - 停止测试 - 没有找到能够升级的版本，当前版本：{curSysVersion} 不在您所选择的2种版本中");
                            item.TestResult = "FAIL";
                            break;
                        }

                        if (nextUpgradePath != "")
                        {
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            // 上传ota包
                            if (await _api.UploadFirmwareAsync_SKDL_new(nextUpgradePath) == "success")
                            {
                                // 触发升级
                                if (await _api.StartUpdate() == "success")
                                {
                                    int update_checkCount = 0;
                                    // 检测升级版本和设备升级状态
                                    while (true)
                                    {
                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);
                                        update_checkCount += 1;
                                        string progress = await _api.CheckUpgradeStaus("progress");
                                        string status = await _api.CheckUpgradeStaus("status");
                                        LogSaveOutput($"{_currentIp} - 当前升级进度【{progress}】 -- 升级状态 【{status}】");
                                        if ((progress == "100" && status == "completed"))
                                        {
                                            LogSaveOutput($"{_currentIp} - 升级流程结束，等待设备启动完成！");
                                            upgradeResult = true;
                                            break;
                                        }
                                        if (progress == "99" && status == "update" || progress == "99" && status == "fail" || progress == "0" && status == "not start")
                                        {
                                            LogSaveOutput($"{_currentIp} - 升级流程结束，等待60秒设备启动完成！异端流程执行");
                                            await Task.Delay(60000);
                                            upgradeResult = true;
                                            break;
                                        }
                                        if (update_checkCount >= 60)
                                        {
                                            upgradeResult = false;
                                            item.TestResult = "FAIL";
                                            LogSaveOutput($"{_currentIp} - 升级流程超时！");
                                            break;
                                        }
                                        await Task.Delay(3000);
                                    }
                                }
                                else
                                {
                                    LogSaveOutput($"{_currentIp} - 触发升级失败，请检查设备状态！");
                                    item.TestResult = "FAIL";
                                    upgradeResult = false;
                                    break;
                                }
                            }
                            else
                            {
                                LogSaveOutput($"{_currentIp} - ota包上传失败，请检查！\n{nextUpgradePath}");
                                item.TestResult = "FAIL";
                                upgradeResult = false;
                                break;
                            }
                        }

                        int update_checkVersionCount = 0;
                        // ota升级测试结果更新
                        if (upgradeResult)
                        {
                            // ota升级进度完成，等待重启完成
                            LogSaveOutput($"{_currentIp} - ota升级进度完成，等待重启后进行拉流检测……");
                            while (true)
                            {
                                update_checkVersionCount += 1;
                                await Task.Delay(3000);
                                // 获取token
                                buttonGetToken_Click(null, null);
                                await Task.Delay(1000);
                                string upgradeDoneVersion = await _api.GetSysVerison();
                                string diskStatus = await _api.GetDiskStatus();
                                if (upgradeDoneVersion != null)
                                {
                                    if (nextUpgradePath.Contains(upgradeDoneVersion) && diskStatus.Contains("SUCCESS"))
                                    {
                                        item.TestResult = "PASS";
                                        upgradeResult = true;
                                        LogSaveOutput($"设备【{_currentIp}】升级完成，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");

                                        // 升级成功，即将开始拉流压测
                                        // 每一路拉流，并比对结果
                                        panoramicMainStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        panoramicSubStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpMainStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpSubStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai1StreanOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai2StreanOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai3StreanOnBtn_Click(null, null);
                                        await Task.Delay(100);

                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);

                                        // 全景辅流拉流测试出结果
                                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                                        LogSaveOutput(panoramicSub_pic);
                                        await Task.Delay(100);

                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);

                                        // 特写辅流拉流测试出结果
                                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                                        LogSaveOutput(closeUpSub_pic);
                                        await Task.Delay(100);

                                        // AI1前排流拉流测试出结果
                                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                                        LogSaveOutput(ai1_pic);
                                        await Task.Delay(100);

                                        // AI左后排流拉流测试出结果
                                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                                        LogSaveOutput(ai2_pic);
                                        await Task.Delay(100);

                                        // AI右后排流拉流测试出结果
                                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                                        LogSaveOutput(ai3_pic);
                                        await Task.Delay(100);

                                        if (item.TestCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                                        LogSaveOutput($"{_currentIp} - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                                        // 结果呈现，次数增加
                                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                                        // 所有流关流
                                        panoramicMainStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        panoramicSubStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpMainStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpSubStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai1StreanOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai2StreanOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        ai3StreanOffBtn_Click(null, null);
                                        await Task.Delay(100);

                                        LogSaveOutput($"{_currentIp} - {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                                        if (stopTest)
                                        {
                                            LogSaveOutput($"{_currentIp} - 手动停止测试！");
                                            return;
                                        }

                                        if (isSuccess)
                                        {
                                            item.TestCount++;
                                            item.TestResult = "PASS";
                                            LogSaveOutput($"{_currentIp} - 【第{item.TestCount}次测试结束，下一次测试即将开始……】");
                                            break;
                                        }
                                        else
                                        {
                                            item.TestResult = "FAIL";
                                            return;
                                        }

                                    }
                                    else
                                    {
                                        LogSaveOutput($"{_currentIp} - 设备【{_currentIp}】升级失败，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");
                                        item.TestResult = "FAIL";
                                        upgradeResult = false;
                                        return;
                                    }
                                }
                                if (update_checkVersionCount >= 30)
                                {
                                    item.TestResult = "FAIL";
                                    upgradeResult = false;
                                    LogSaveOutput($"{_currentIp} - 长时间没有起来，当前设备 【{_currentIp}】 OTA升级失败，期望版本：【{nextUpgradePath}】");
                                    return;
                                }
                            }
                        }
                        else
                        {
                            upgradeResult = false;
                            item.TestResult = "FAIL";
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"{_currentIp} - case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }
            });
        }





        private async void TestCase20(TestCases item)
        {

            AntdUI.Modal.open(new AntdUI.Modal.Config(this, "case未实现提醒", "当前case还未实现！", AntdUI.TType.Info));

            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);


        }

        private async void TestCase19(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置dhcp 为true，自动获取ip
            getNetWorkConfigBtn_Click(null, null);
            await Task.Delay(100);
            setUdhcpcBtn_Click(null, null);
            await Task.Delay(100);

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 重启设备
                        rebootDevBtn_Click(null, null);
                        LogSaveOutput("设备重启中请稍等150秒……");
                        await Task.Delay(150000);

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }




        private async void TestCase18(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "全功能老化测试开始提醒！",
                $"点击后开始全功能老化测试计时，勾选上面需要测试的内容，即可自动开始老化测试，取消勾选该测试即自动停止！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            var start = DateTime.Now; // 1. 记录开始时间

            // 2. 创建并启动定时器 (1秒刷新一次)
            new System.Windows.Forms.Timer { Interval = 1000, Enabled = true }.Tick += (s, e) =>
            {
                // 3. 核心代码：计算差值并转为 String
                string timeStr = (DateTime.Now - start).ToString(@"hh\:mm\:ss");

                // 显示出来 (例如赋值给 Label 或 窗体标题)
                item.TestResult = timeStr;
            };

            checkbox_7streamRTSPOn.Checked = true;
            await Task.Delay(10000);
            checkbox_zoomCircleTest.Checked = true;
            await Task.Delay(1000);
            checkbox_eptzCircleTest.Checked = true;
            await Task.Delay(1000);
            checkbox_ptzCircleTest.Checked = true;

            LogSaveOutput("全功能测试开始！");

        }


        private async void TestCase17(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_case", "case1_gbs_Channel1andChannel2LogicOnOffStream.py");
            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "Python脚本测试开始提醒！",
                $"点击开始后，尽量请勿操作电脑！详细测试结果和Log请前往文件夹{AppDomain.CurrentDomain.BaseDirectory}python_case查看结果即可！\n" +
                $"点击关闭该弹窗后测试将开始！", AntdUI.TType.Info));
            await WindowsFunc.executeCMDCommand_RealTime($"python -u {scriptPath}", (line) =>
            {
                // 因为这是在后台线程回调的，如果是更新UI，需要 Invoke
                this.Invoke((Action)(() =>
                {
                    LogSaveOutput(line);
                    if (line.Contains("通道逻辑测试结果为：True"))
                    {
                        item.TestCount++;
                        item.TestResult = "PASS";
                    }
                    else if (line.Contains("通道逻辑测试结果为：False") || line.Contains("Exception"))
                    {
                        WindowsFunc.executeCMDCommand("taskkill /F /IM python.exe");
                        item.TestResult = "FAIL";
                    }
                }));
            });
        }




        private async void TestCase16(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_uvc_pic, next_uvc_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置到uvc出特写模式
            setUvcCloseUpBtn_Click(null, null);
            await Task.Delay(100);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前uvc支持的分辨率
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");
            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;

                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 10 秒
                                await Task.Delay(10000);

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师特写[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }




        private async void TestCase15(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_uvc_pic, next_uvc_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置到uvc出特写模式
            setUvcCloseUpBtn_Click(null, null);
            await Task.Delay(100);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前uvc支持的分辨率
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");

            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;

                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 10 秒
                                await Task.Delay(10000);

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师特写[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }



        private async void TestCase14(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_uvc_pic, next_uvc_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置到uvc出全景模式
            setUvcPanoramicBtn_Click(null, null);
            await Task.Delay(100);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前uvc支持的分辨率
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");

            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;

                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    LogFailType(uvcResolution, format); // 记录失败信息
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 10 秒
                                await Task.Delay(10000);
                                LogSaveOutput($"预览教师全景[{uvc_x}x{uvc_y} {format}]  10秒");

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师全景[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }
        // 测试循环时间间隔
        int circleTestDelayTime = 5;
        private async void TestCase13(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取 token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置 UVC 输出为全景流
            setUvcPanoramicBtn_Click(null, null);
            await Task.Delay(100);

            // 切换到高分辨率模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前 UVC 支持的分辨率列表
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");
            if (uvcSupportResolutionList == null || uvcSupportResolutionList.Count == 0)
            {
                LogSaveOutput("未获取到支持的分辨率，测试终止");
                return;
            }

            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;

                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    LogFailType(uvcResolution, format); // 记录失败信息
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 15 秒
                                await Task.Delay(10000);

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师全景[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }

        



        private async void TestCase12(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";
            string ori_panoramic_RTMP_Main_pic, next_panoramic_RTMP_Main_pic = "";
            string ori_panoramic_RTMP_Sub_pic, next_panoramic_RTMP_Sub_pic = "";
            string ori_closeUp_RTMP_Main_pic, next_closeUp_RTMP_Main_pic = "";
            string ori_closeUp_RTMP_Sub_pic, next_closeUp_RTMP_Sub_pic = "";

            // 设置clumsy限速5%
            input1_clumsyLimit.Text = "5";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 打开clumsy限速5%
                        clumsyLimitSpeedBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{_currentIp} - 等待5秒后，进行测试……");

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicMainRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);


                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        // 全景主流RTMP拉流测试出结果
                        string panoramicRTMPMain_pic = await SafeSnapshotAsync(player_rtmp_panoramicMain, testFolder, "全景RTMP主流");
                        LogSaveOutput(panoramicRTMPMain_pic);
                        await Task.Delay(100);

                        // 全景辅流RTMP拉流测试出结果
                        string panoramicRTMPSub_pic = await SafeSnapshotAsync(player_rtmp_panoramicSub, testFolder, "全景RTMP辅流");
                        LogSaveOutput(panoramicRTMPSub_pic);
                        await Task.Delay(100);

                        // 特写主流RTMP拉流测试出结果
                        string closeUpRTMPMain_pic = await SafeSnapshotAsync(player_rtmp_closeUpMain, testFolder, "特写RTMP主流");
                        LogSaveOutput(closeUpRTMPMain_pic);
                        await Task.Delay(100);

                        // 特写辅流RTMP拉流测试出结果
                        string closeUpRTMPSub_pic = await SafeSnapshotAsync(player_rtmp_closeUpSub, testFolder, "特写RTMP辅流");
                        LogSaveOutput(closeUpRTMPSub_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                            ori_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic; next_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic;
                            ori_panoramic_RTMP_Sub_pic = panoramicRTMPSub_pic; next_panoramicSub_pic = panoramicRTMPSub_pic;
                            ori_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic; next_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic;
                            ori_closeUp_RTMP_Sub_pic = closeUpRTMPSub_pic; next_closeUp_RTMP_Sub_pic = closeUpRTMPSub_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                            ori_panoramic_RTMP_Main_pic = next_panoramic_RTMP_Main_pic; next_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic;
                            ori_panoramic_RTMP_Sub_pic = next_panoramic_RTMP_Sub_pic; next_panoramic_RTMP_Sub_pic = panoramicRTMPSub_pic;
                            ori_closeUp_RTMP_Main_pic = next_closeUp_RTMP_Main_pic; next_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic;
                            ori_closeUp_RTMP_Sub_pic = next_closeUp_RTMP_Sub_pic; next_closeUp_RTMP_Sub_pic = closeUpRTMPSub_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI1RTMP流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI2RTMP左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI3RTMP右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");
                        bool panoramicRTMPMainResult = checkPICValid(ori_panoramic_RTMP_Main_pic, next_panoramic_RTMP_Main_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP主流测试结果：{panoramicRTMPMainResult} -- {ori_panoramic_RTMP_Main_pic} : {next_panoramic_RTMP_Main_pic}");
                        bool panoramicRTMPSubResult = checkPICValid(ori_panoramic_RTMP_Sub_pic, next_panoramic_RTMP_Sub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP辅流测试结果：{panoramicRTMPSubResult} -- {ori_panoramic_RTMP_Sub_pic} : {next_panoramic_RTMP_Sub_pic}");
                        bool closeUpRTMPMainResult = checkPICValid(ori_closeUp_RTMP_Main_pic, next_closeUp_RTMP_Main_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP主流测试结果：{closeUpRTMPMainResult} -- {ori_closeUp_RTMP_Main_pic} : {next_closeUp_RTMP_Main_pic}");
                        bool closeUpRTMPSubResult = checkPICValid(ori_closeUp_RTMP_Sub_pic, next_closeUp_RTMP_Sub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP辅流测试结果：{closeUpRTMPSubResult} -- {ori_closeUp_RTMP_Sub_pic} : {next_closeUp_RTMP_Sub_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        clumsyStopLimitSpeedBtn_Click(null, null);
                        await Task.Delay(3000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }



        private async void TestCase11(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);


            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfigBtn_Click(null, null);
            await Task.Delay(3000);

            // 设置主流到1080P - 25fps， 辅流到576P - 25fps
            LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
            LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1024x576\""));
            LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
            LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1024x576\""));

            LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
            LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config));
            LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));
            LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }



        private async void TestCase10(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfigBtn_Click(null, null);
            await Task.Delay(3000);

            // 设置主流到4K - 30fps， 辅流到720P - 30fps
            LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
            LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1280x720\""));
            LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
            LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1280x720\""));

            LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
            LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config));
            LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));
            LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase9(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfigBtn_Click(null, null);
            await Task.Delay(3000);

            // 设置辅流的fps到60
            LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));
            LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高帧率模式设置 -- 切辅流均设置60FPS后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 切辅流均设置60FPS后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 切辅流均设置60FPS后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 切辅流均设置60FPS后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");



                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }



        private async void TestCase8(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase7(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_extreme_pic, next_extreme_pic = "";
            string ori_extreme2_pic, next_extreme2_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            extremeModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        extreme1StreamOnBtn_Click(null, null);
                        await Task.Delay(5000);
                        extreme2StreamOnBtn_Click(null, null);
                        await Task.Delay(5000);

                        // 性能模式流1测试出结果
                        string extreme_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "性能模式流1");
                        LogSaveOutput(extreme_pic);
                        await Task.Delay(100);

                        // 性能模式流2测试出结果
                        string extreme2_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "性能模式流2");
                        LogSaveOutput(extreme2_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_extreme_pic = extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }
                        else
                        {
                            ori_extreme_pic = next_extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = next_extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }


                        bool extremeResult = checkPICValid(ori_extreme_pic, next_extreme_pic);
                        LogSaveOutput($"当前性能模式后 -- 性能模式流 - 1测试结果：{extremeResult} -- {ori_extreme_pic} : {next_extreme_pic}");
                        bool extreme2Result = checkPICValid(ori_extreme2_pic, next_extreme2_pic);
                        LogSaveOutput($"当前性能模式后 -- 性能模式流 - 2测试结果：{extreme2Result} -- {ori_extreme2_pic} : {next_extreme2_pic}");


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool extremeStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前性能模式流1状态测试结果：{extremeStatusResult}");
                        bool extreme2StatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前性能模式流2状态测试结果：{extreme2StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = extremeResult && extreme2Result
                        && extremeStatusResult && extreme2StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        extreme1StreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        extreme2StreamOffBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }




        private async void TestCase6(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为1MB -- 1024
                        input1_allStreamBitrate.Text = "1024";
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(3000);
                        changeAllStreamBitrateBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase5(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为16MB -- 16384
                        input1_allStreamBitrate.Text = "16384";
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(3000);
                        changeAllStreamBitrateBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase4(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为64MB -- 65536
                        input1_allStreamBitrate.Text = "65536";
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(3000);
                        changeAllStreamBitrateBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase3_1(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";

            input1_clumsyLimit.Text = "8";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 打开clumsy限速8%
                        clumsyLimitSpeedBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{_currentIp} - 等待5秒后，进行测试……");

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                        }

                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        clumsyStopLimitSpeedBtn_Click(null, null);
                        await Task.Delay(3000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase3(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            //string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            //string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            //string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            //string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";

            input1_clumsyLimit.Text = "1";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 打开clumsy限速8%
                        clumsyLimitSpeedBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{_currentIp} - 等待5秒后，进行测试……");

                        // 每一路拉流，并比对结果
                        //panoramicMainStreamOnBtn_Click(null, null);
                        //await Task.Delay(100);
                        //panoramicSubStreamOnBtn_Click(null, null);
                        //await Task.Delay(100);
                        //closeUpMainStreamOnBtn_Click(null, null);
                        //await Task.Delay(100);
                        //closeUpSubStreamOnBtn_Click(null, null);
                        //await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        //// 全景主流拉流测试出结果
                        //string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        //LogSaveOutput(panoramicMain_pic);
                        //await Task.Delay(100);

                        //// 全景辅流拉流测试出结果
                        //string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        //LogSaveOutput(panoramicSub_pic);
                        //await Task.Delay(100);

                        //// 特写主流拉流测试出结果
                        //string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        //LogSaveOutput(closeUpMain_pic);
                        //await Task.Delay(100);

                        //// 特写辅流拉流测试出结果
                        //string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        //LogSaveOutput(closeUpSub_pic);
                        //await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            //ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            //ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            //ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            //ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            //ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            //ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            //ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            //ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }

                        //bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        //LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        //bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        //LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        //bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        //LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        //bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        //LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        //bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        //LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        //bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        //LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        //bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        //LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        //bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        //LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        //bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        //LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        //bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        //LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        //bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        //LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        //bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        //&& panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;
                        //bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result;
                        bool isSuccess = ai1Result && ai2Result && ai3Result;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        //panoramicMainStreamOffBtn_Click(null, null);
                        //await Task.Delay(100);
                        //panoramicSubStreamOffBtn_Click(null, null);
                        //await Task.Delay(100);
                        //closeUpMainStreamOffBtn_Click(null, null);
                        //await Task.Delay(100);
                        //closeUpSubStreamOffBtn_Click(null, null);
                        //await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        clumsyStopLimitSpeedBtn_Click(null, null);
                        await Task.Delay(3000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("全景主流", player_panoramicMain);
            openCvRtspPlayersDict.Add("全景辅流", player_panoramicSub);
            openCvRtspPlayersDict.Add("特写主流", player_CloseUpMain);
            openCvRtspPlayersDict.Add("特写辅流", player_CloseUpSub);
            openCvRtspPlayersDict.Add("AI前排流", player_ai1);
            openCvRtspPlayersDict.Add("AI左后排流", player_ai2);
            openCvRtspPlayersDict.Add("AI右后排流", player_ai3);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;


            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url}");
                        await Task.Delay(10000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }


        private bool getStreamStatusResult(OpenCvRtspPlayer player)
        {
            bool curStreamStatusResult = false;

            var status = player.GetPlayerStatus();
            float cur_fps = status.Fps;
            float cur_BitRate = status.TotalBitrateKbps / 1024;
            //float cur_CpuUsage = status.CpuUsage;

            bool fpsCheckResult = cur_fps > 0 ? true : false;
            bool bitRateCheckResult = cur_BitRate > 0 ? true : false;
            //bool cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

            LogSaveOutput($"当前全景主流帧率、码率、cpu占用情况:fps:{cur_fps:F1} -- bitrate: {cur_BitRate:F2}Mbps,结果为:{fpsCheckResult}");
            //LogSaveOutput($"当前全景主流帧率、码率、cpu占用情况:fps:{cur_fps:F1} -- cpu:{cur_CpuUsage:F1} -- bitrate: {cur_BitRate / 1024:F2}Mbps,结果为:{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");

            //curStreamStatusResult = fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult;
            curStreamStatusResult = fpsCheckResult && bitRateCheckResult;

            return curStreamStatusResult;
        }



        private async void TestCase1(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");


            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_panoramicSub_pic, next_panoramicSub_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_closeUpSub_pic, next_closeUpSub_pic = "";
            string ori_ai1_pic, next_ai1_pic = "";
            string ori_ai2_pic, next_ai2_pic = "";
            string ori_ai3_pic, next_ai3_pic = "";
            this.BeginInvoke(async () =>
            {
                while (true)
                {

                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolutionBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeResolutionOrderBtn_Click(null, null);
                        await Task.Delay(1000);
                        changeAllStreamCurConfigBtn_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 全景辅流拉流测试出结果
                        string panoramicSub_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "全景辅流");
                        LogSaveOutput(panoramicSub_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        // 特写辅流拉流测试出结果
                        string closeUpSub_pic = await SafeSnapshotAsync(player_CloseUpSub, testFolder, "特写辅流");
                        LogSaveOutput(closeUpSub_pic);
                        await Task.Delay(100);

                        // AI1前排流拉流测试出结果
                        string ai1_pic = await SafeSnapshotAsync(player_ai1, testFolder, "AI1前排流");
                        LogSaveOutput(ai1_pic);
                        await Task.Delay(100);

                        // AI左后排流拉流测试出结果
                        string ai2_pic = await SafeSnapshotAsync(player_ai2, testFolder, "AI左后排流");
                        LogSaveOutput(ai2_pic);
                        await Task.Delay(100);

                        // AI右后排流拉流测试出结果
                        string ai3_pic = await SafeSnapshotAsync(player_ai3, testFolder, "AI右后排流");
                        LogSaveOutput(ai3_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = ai3_pic; next_ai3_pic = ai3_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_panoramicSub_pic = next_panoramicSub_pic; next_panoramicSub_pic = panoramicSub_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_closeUpSub_pic = next_closeUpSub_pic; next_closeUpSub_pic = closeUpSub_pic;
                            ori_ai1_pic = next_ai1_pic; next_ai1_pic = ai1_pic;
                            ori_ai2_pic = next_ai2_pic; next_ai2_pic = ai2_pic;
                            ori_ai3_pic = next_ai3_pic; next_ai3_pic = ai3_pic;
                        }


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool panoramicSubResult = checkPICValid(ori_panoramicSub_pic, next_panoramicSub_pic);
                        LogSaveOutput($"当前全景辅流测试结果：{panoramicSubResult} -- {ori_panoramicSub_pic} : {next_panoramicSub_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool closeUpSubResult = checkPICValid(ori_closeUpSub_pic, next_closeUpSub_pic);
                        LogSaveOutput($"当前特写辅流测试结果：{closeUpSubResult} -- {ori_closeUpSub_pic} : {next_closeUpSub_pic}");
                        bool ai1Result = checkPICValid(ori_ai1_pic, next_ai1_pic);
                        LogSaveOutput($"当前AI1流测试结果：{ai1Result} -- {ori_ai1_pic} : {next_ai1_pic}");
                        bool ai2Result = checkPICValid(ori_ai2_pic, next_ai2_pic);
                        LogSaveOutput($"当前AI2左后排流测试结果：{ai2Result} -- {ori_ai2_pic} : {next_ai2_pic}");
                        bool ai3Result = checkPICValid(ori_ai3_pic, next_ai3_pic);
                        LogSaveOutput($"当前AI3右后排流测试结果：{ai3Result} -- {ori_ai3_pic} : {next_ai3_pic}");

                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool panoramicSubStatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前全景辅流状态测试结果：{panoramicSubStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");
                        bool closeUpSubStatusResult = getStreamStatusResult(player_CloseUpSub);
                        LogSaveOutput($"当前特写辅流状态测试结果：{closeUpSubStatusResult}");
                        bool ai1StatusResult = getStreamStatusResult(player_ai1);
                        LogSaveOutput($"当前AI1流状态测试结果：{ai1StatusResult}");
                        bool ai2StatusResult = getStreamStatusResult(player_ai2);
                        LogSaveOutput($"当前AI2左后排流状态测试结果：{ai2StatusResult}");
                        bool ai3StatusResult = getStreamStatusResult(player_ai3);
                        LogSaveOutput($"当前AI3右后排流状态测试结果：{ai3StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && panoramicSubResult && closeUpMainResult && closeUpSubResult && ai1Result && ai2Result && ai3Result
                        && panoramicMainStatusResult && panoramicSubStatusResult && closeUpMainStatusResult && closeUpSubStatusResult && ai1StatusResult && ai2StatusResult && ai3StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpSubStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai1StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai2StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        ai3StreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


















        string cur_panoramicMain_stream_config;
        string cur_panoramicSub_stream_config;
        string cur_closeUpMain_stream_config;
        string cur_closeUpSub_stream_config;

        private async void readAllStreamCurConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(cur_panoramicMain_stream_config = await _api.GetSpecVideoStreamDefaultConfig("panoramicMain"));
                LogSaveOutput(cur_panoramicSub_stream_config = await _api.GetSpecVideoStreamDefaultConfig("panoramicSub"));
                LogSaveOutput(cur_closeUpMain_stream_config = await _api.GetSpecVideoStreamDefaultConfig("closeUpMain"));
                LogSaveOutput(cur_closeUpSub_stream_config = await _api.GetSpecVideoStreamDefaultConfig("closeUpSub"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取所有视频配置异常！\n{ex.ToString()}");
            }

        }

        private async void readAllStreamCurConfig2()
        {
            try
            {
                LogSaveOutput(cur_panoramicMain_stream_config = await _api.GetSpecVideoStreamDefaultConfig("sub"));
                LogSaveOutput(cur_panoramicSub_stream_config = await _api.GetSpecVideoStreamDefaultConfig("sub"));
                LogSaveOutput(cur_closeUpMain_stream_config = await _api.GetSpecVideoStreamDefaultConfig("main"));
                LogSaveOutput(cur_closeUpSub_stream_config = await _api.GetSpecVideoStreamDefaultConfig("main"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取所有视频配置异常！\n{ex.ToString()}");
            }
        }

        string set_panoramicMain_stream_config_result;
        string set_panoramicSub_stream_config_result;
        string set_closeUpMain_stream_config_result;
        string set_closeUpSub_stream_config_result;


        private List<AntdUI.Radio> GetOrderedRadios(AntdUI.FlowPanel panel)
        {
            // 1. 筛选出 AntdUI.Radio
            // 2. 按 TabIndex 排序 (通常 TabIndex 顺序就是视觉顺序)
            //    如果 TabIndex 没设置好，也可以用 .OrderBy(r => r.Top) 按 Y 坐标排序
            return panel.Controls
                .OfType<AntdUI.Radio>()
                .OrderBy(r => r.TabIndex) // 或者 .OrderBy(r => r.Top)
                .ToList();
        }

        private int _currentResIndex_panoramicMain = -1; // 记录当前索引
        private int _currentResIndex_panoramicSub = -1; // 记录当前索引
        private int _currentResIndex_closeUpMain = -1; // 记录当前索引
        private int _currentResIndex_closeUpSub = -1; // 记录当前索引

        private string changeOrder(string streamType, int curIndex, AntdUI.FlowPanel panel)
        {
            var radios = GetOrderedRadios(panel);

            // 如果没有 Radio，直接退出
            if (radios.Count == 0) return "";

            // 索引 + 1
            curIndex++;

            // 如果超出了数量，重置回 0 (循环) 或者停止
            if (curIndex >= radios.Count)
            {
                curIndex = 0; // 循环回到第一个
                              // return; // 如果想停止，就解开这行
            }

            // 选中
            radios[curIndex].Checked = true;

            if (streamType == "panoramicMain")
            {
                _currentResIndex_panoramicMain = curIndex;
            }
            if (streamType == "panoramicSub")
            {
                _currentResIndex_panoramicSub = curIndex;
            }
            if (streamType == "closeUpMain")
            {
                _currentResIndex_closeUpMain = curIndex;
            }
            if (streamType == "closeUpSub")
            {
                _currentResIndex_closeUpSub = curIndex;
            }

            return radios[curIndex].Text;
        }

        private void changeResolutionOrderBtn_Click(object sender, EventArgs e)
        {
            try
            {
                string panoramicMain_resol_change = changeOrder("panoramicMain", _currentResIndex_panoramicMain, panel_panoramicMain_resolution);
                LogSaveOutput($"修改分辨率：【panoramicMain】 -- {panoramicMain_resol_change}");
                string panoramicSub_resol_change = changeOrder("panoramicSub", _currentResIndex_panoramicSub, panel_panoramicSub_resolution);
                LogSaveOutput($"修改分辨率：【panoramicSub】 -- {panoramicSub_resol_change}");
                string closeUpMain_resol_change = changeOrder("closeUpMain", _currentResIndex_closeUpMain, panel_closeUpMain_resolution);
                LogSaveOutput($"修改分辨率：【closeUpMain】 -- {closeUpMain_resol_change}");
                string closeUpSub_resol_change = changeOrder("closeUpSub", _currentResIndex_closeUpSub, panel_closeUpSub_resolution);
                LogSaveOutput($"修改分辨率：【closeUpSub】 -- {closeUpSub_resol_change}");

                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config.Replace(JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString(), panoramicMain_resol_change));
                LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config.Replace(JObject.Parse(cur_panoramicSub_stream_config)["resolution"].ToString(), panoramicSub_resol_change));
                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config.Replace(JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString(), closeUpMain_resol_change));
                LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config.Replace(JObject.Parse(cur_closeUpSub_stream_config)["resolution"].ToString(), closeUpSub_resol_change));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改分辨率顺序配置异常！\n{ex.ToString()}");
            }
        }

        private async void changeResolutionOrder2()
        {
            try
            {
                string panoramicMain_resol_change = changeOrder("panoramicMain", _currentResIndex_panoramicMain, panel_panoramicMain_resolution);
                LogSaveOutput($"修改分辨率：【panoramicMain】 -- {panoramicMain_resol_change}");
                string panoramicSub_resol_change = changeOrder("panoramicSub", _currentResIndex_panoramicSub, panel_panoramicSub_resolution);
                LogSaveOutput($"修改分辨率：【panoramicSub】 -- {panoramicSub_resol_change}");
                string closeUpMain_resol_change = changeOrder("closeUpMain", _currentResIndex_closeUpMain, panel_closeUpMain_resolution);
                LogSaveOutput($"修改分辨率：【closeUpMain】 -- {closeUpMain_resol_change}");
                string closeUpSub_resol_change = changeOrder("closeUpSub", _currentResIndex_closeUpSub, panel_closeUpSub_resolution);
                LogSaveOutput($"修改分辨率：【closeUpSub】 -- {closeUpSub_resol_change}");

                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config.Replace(JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString(), panoramicMain_resol_change));
                LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config.Replace(JObject.Parse(cur_panoramicSub_stream_config)["resolution"].ToString(), panoramicSub_resol_change));
                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config.Replace(JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString(), closeUpMain_resol_change));
                LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config.Replace(JObject.Parse(cur_closeUpSub_stream_config)["resolution"].ToString(), closeUpSub_resol_change));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改分辨率顺序配置异常！\n{ex.ToString()}");
            }

        }

        private async void changeAllStreamCurConfigBtn_Click(object sender, EventArgs e)
        {

            try
            {
                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config));
                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));
                LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改全部视频流配置异常！\n{ex.ToString()}");
            }


        }

        private async void changeAllStreamCurConfig2()
        {

            try
            {
                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicSub_stream_config));
                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));
                LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpSub_stream_config));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改全部视频流配置异常！\n{ex.ToString()}");
            }


        }

        private async void resetAllStreamDefaultConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("panoramicMain"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("panoramicSub"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("closeUpMain"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("closeUpSub"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"读取全部视频流默认配置异常！\n{ex.ToString()}");
            }

        }

        string clumsy_processId;
        private async void clumsyLimitSpeedBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput($"{_currentIp} - Clumsy限速");
                await Task.Run(() =>
                {
                    string argument1 = " --filter";
                    string argument2 = "\"" + $"inbound and ip.SrcAddr == {_currentIp}" + "\"";
                    string argument3 = $"--drop on --drop-chance {input1_clumsyLimit.Text}";

                    Console.WriteLine(argument1);
                    Console.WriteLine(argument2);
                    Console.WriteLine(argument3);
                    Process myPro = new Process();

                    myPro.StartInfo.FileName = ".\\clumsy\\clumsy.exe";
                    myPro.StartInfo.Arguments = argument1 + " " + argument2 + " " + argument3;
                    Console.WriteLine($"{myPro.StartInfo.FileName} " + argument1 + " " + argument2 + " " + argument3);
                    myPro.StartInfo.RedirectStandardInput = true;
                    myPro.StartInfo.RedirectStandardOutput = true;
                    myPro.StartInfo.CreateNoWindow = false;
                    myPro.StartInfo.UseShellExecute = false;
                    myPro.Start();
                    clumsy_processId = myPro.Id.ToString();
                    Console.WriteLine(clumsy_processId);
                    myPro.WaitForExit();
                    myPro.Close();
                });
            }
            catch (Exception ex)
            {
                LogSaveOutput($"clumsy限速异常！\n{ex.ToString()}");
            }

        }

        private async void clumsyStopLimitSpeedBtn_Click(object sender, EventArgs e)
        {

            try
            {
                LogSaveOutput($"{_currentIp} - Clumsy解除限速 -- PID: {clumsy_processId}");
                if (clumsy_processId != "")
                {
                    WindowsFunc.executeCMDCommand($"taskkill /F /T /PID {clumsy_processId}");
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"停止clumsy限速异常！\n{ex.ToString()}");
            }


        }

        private async void openCurFolderBtn_Click(object sender, EventArgs e)
        {
            string curToolDir = Environment.CurrentDirectory;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    Arguments = curToolDir,
                    FileName = "explorer.exe"
                };
                Process.Start(startInfo);
                AntdUI.Message.success(this, $"打开文件夹成功：{curToolDir}");
            }
            catch (Exception ex)
            {
                AntdUI.Message.error(this, "无法打开文件夹: " + ex.Message);
            }
        }

        private async void changeAllStreamBitrateBtn_Click(object sender, EventArgs e)
        {
            try
            {
                string cur_bitRate = input1_allStreamBitrate.Text;
                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_panoramicMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_panoramicSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_closeUpMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_closeUpSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));


                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config));
                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));
                LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改所有视频流码率配置异常！\n{ex.ToString()}");
            }


        }

        private async void changeAllStreamBitrate2()
        {

            try
            {
                string cur_bitRate = input1_allStreamBitrate.Text;
                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_panoramicMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_panoramicSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_closeUpMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));
                LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config.Replace($"\"bitRate\": {JObject.Parse(cur_closeUpSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {cur_bitRate},"));


                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicSub_stream_config));
                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));
                LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpSub_stream_config));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改所有视频流码率配置异常！\n{ex.ToString()}");
            }
        }




        private async void hiResModeBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetVideoStreamMode("high_res"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"设置高分辨率模式异常！\n{ex.ToString()}");
            }

        }

        private async void hiFpsModeBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetVideoStreamMode("high_fps"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"设置高帧率模式异常！\n{ex.ToString()}");
            }

        }

        private async void extremeModeBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetVideoStreamMode("extreme"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"设置性能模式异常！\n{ex.ToString()}");
            }

        }

        private void extreme1StreamOnBtn_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
                string url = $"rtsp://{_currentIp}/extreme";
                player_panoramicMain.Start(url, checkBoxDecodeTest.Checked);
                LogSaveOutput($"【Extreme性能模式流1】开始拉流：{url}");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流1拉流异常！\n{ex.ToString()}");
            }
        }

        private async void extreme1StreamOn2()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
                string url = $"rtsp://{_currentIp}/1";
                player_panoramicMain.Start(url, checkBoxDecodeTest.Checked);
                LogSaveOutput($"【Extreme性能模式流1】开始拉流：{url}");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流1拉流异常！\n{ex.ToString()}");
            }
        }

        private void extreme1StreamOffBtn_Click(object sender, EventArgs e)
        {
            try
            {
                player_panoramicMain.Stop();
                LogSaveOutput("【Extreme性能模式流1】已停止");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流停止异常！\n{ex.ToString()}");
            }
        }

        private async void extreme1StreamOff2()
        {
            try
            {
                player_panoramicMain.Stop();
                LogSaveOutput("【Extreme性能模式流1】已停止");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流停止异常！\n{ex.ToString()}");
            }
        }

        private void extreme2StreamOnBtn_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
                string url = $"rtsp://{_currentIp}/extreme_2";
                player_panoramicSub.Start(url, checkBoxDecodeTest.Checked);
                LogSaveOutput($"【Extreme性能模式流2】开始拉流：{url}");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流2拉流异常！\n{ex.ToString()}");
            }

        }

        private async void extreme2StreamOn2()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
                string url = $"rtsp://{_currentIp}/2";
                player_panoramicSub.Start(url, checkBoxDecodeTest.Checked);
                LogSaveOutput($"【Extreme性能模式流2】开始拉流：{url}");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流2拉流异常！\n{ex.ToString()}");
            }
        }

        private void extreme2StreamOffBtn_Click(object sender, EventArgs e)
        {
            try
            {
                player_panoramicSub.Stop();
                LogSaveOutput("【Extreme性能模式流2】已停止");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流2停止异常！\n{ex.ToString()}");
            }
        }

        private async void extreme2StreamOff2()
        {
            try
            {
                player_panoramicSub.Stop();
                LogSaveOutput("【Extreme性能模式流2】已停止");
            }
            catch (Exception ex)
            {
                LogSaveOutput($"性能模式流停止异常！\n{ex.ToString()}");
            }
        }

        private void oneKeyStopTestBtn_Click(object sender, EventArgs e)
        {
            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否停止当前测试项？",
                "点击确认后，当前一轮跑完后会停止测试！望悉知！",
                AntdUI.TType.Warn));

            if (result != DialogResult.Yes && result != DialogResult.OK)
            {
                return;
            }
            else
            {
                stopTest = true;
                WindowsFunc.executeCMDCommand("taskkill /F /IM msedge.exe");
                WindowsFunc.executeCMDCommand("taskkill /F /IM python.exe");
            }
        }

        private void panoramicMainRtmpStreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_rtmp_panoramicMain.Start(input_rtmp_panoramicMain.Text);
            LogSaveOutput("【全景主流RTMP】开始拉流");
        }

        private void panoramicSubRtmpStreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_rtmp_panoramicSub.Start(input_rtmp_panoramicSub.Text);
            LogSaveOutput("【全景辅流RTMP】开始拉流");
        }

        private void closeUpMainRtmpStreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_rtmp_closeUpMain.Start(input_rtmp_closeUpMain.Text);
            LogSaveOutput("【特写主流RTMP】开始拉流");
        }

        private void closeUpSubRtmpStreanOnBtn_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentIp)) _currentIp = textBox_ip.Text;
            player_rtmp_closeUpSub.Start(input_rtmp_closeUpSub.Text);
            LogSaveOutput("【特写辅流RTMP】开始拉流");
        }

        private void panoramicRtmpStreanOffBtn_Click(object sender, EventArgs e)
        {
            player_rtmp_panoramicMain.Stop();
            LogSaveOutput("【全景主流RTMP】已停止");
        }

        private void panoramicSubRtmpStreanOffBtn_Click(object sender, EventArgs e)
        {
            player_rtmp_panoramicSub.Stop();
            LogSaveOutput("【全景辅流RTMP】已停止");
        }

        private void closeUpMainRtmpStreanOffBtn_Click(object sender, EventArgs e)
        {
            player_rtmp_closeUpMain.Stop();
            LogSaveOutput("【特写主流RTMP】已停止");
        }

        private void closeUpSubRtmpStreanOffBtn_Click(object sender, EventArgs e)
        {
            player_rtmp_closeUpSub.Stop();
            LogSaveOutput("【特写辅流RTMP】已停止");
        }

        private async void paranomicMainRTMPStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_rtmp_panoramicMain, "全景主流RTMP");
        }

        private async void paranomicSubRTMPStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_rtmp_panoramicSub, "全景辅流RTMP");
        }

        private async void closeUpMainRTMPStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_rtmp_closeUpMain, "特写主流RTMP");
        }

        private async void closeUpSubRTMPStreamSnapShotBtn_Click(object sender, EventArgs e)
        {
            await SafeSnapshotAsync(player_rtmp_closeUpSub, "特写辅流RTMP");
        }

        private List<CameraInfo> GetCameras(string cameraNameNeed)
        {
            List<CameraInfo> cameras = new List<CameraInfo>();
            List<CameraInfo> cameraInfos = VideoCapturer.GetCameraInfos();
            for (int i = 0; i < cameraInfos.Count; i++)
            {
                if (cameraInfos[i].Name == cameraNameNeed)
                {
                    Console.WriteLine($"Found Camera: {cameraInfos[i].Name} -- {cameraInfos[i].DevicePath}");
                    cameras.Add(cameraInfos[i]);
                }
            }
            return cameras;
        }
        //重新改动,暂时不需要,指定拉流默认从StartUVC
        //private async void uvcStreamON_byDevicePath(string cameraDevicePath)
        //{
        //    pictureBox_uvcStream.Invalidate();
        //    pictureBox_uvcStream.Refresh();
        //    await Task.Delay(100);

        //    int uvc_x = int.Parse(input1_uvc_x.Text);
        //    int uvc_y = int.Parse(input2_uvc_y.Text);

        //    PreviewSize previewSize = new PreviewSize(uvc_x, uvc_y);

        //    camera1 = new VideoCapturer();
        //    camera1.SetPreviewSize(previewSize.Width, previewSize.Height);
        //    camera1.SetDisplayWindow(this.pictureBox_uvcStream.Handle);
        //    camera1.SetDisplaySize(this.pictureBox_uvcStream.Width, this.pictureBox_uvcStream.Height);

        //    List<CameraInfo> cameras = GetCameras("Seewo Lubo");
        //    int uvcCameraIndex = 0;
        //    for (int i = 0; i < cameras.Count; i++)
        //    {
        //        Console.WriteLine($"GET: -- {cameraDevicePath} -- {cameras[i].DevicePath}");
        //        if (cameraDevicePath == cameras[i].DevicePath)
        //        {
        //            Console.WriteLine($"OK: -- {cameraDevicePath} -- {cameras[i].DevicePath}");
        //            uvcCameraIndex = i; break;
        //        }

        //    }
        //    if(uvc_x == 3840 && uvc_y ==2160)
        //    {
        //        await camera1.StartupCapture(cameras[uvcCameraIndex], uvcCameraIndex, "H264", checkBoxDecodeTest.Checked);
        //        LogSaveOutput($"正在拉取H264{uvc_x}x{uvc_y}");
        //        await Task.Delay(10000);
        //    }
        //    else
        //    {
        //        //先关闭拉流
        //        uvc_streamOffBtn_Click(null, null);
        //        await Task.Delay(1000);

        //        LogSaveOutput($"正在拉取MJPG {uvc_x}x{uvc_y},预览10s");
        //        input_Uvctype.Text = "MJPG";
        //        await camera1.StartupCapture(cameras[uvcCameraIndex], uvcCameraIndex, "MJPG", checkBoxDecodeTest.Checked);
        //        await Task.Delay(10000);
        //        LogSaveOutput($"拉流完成,等待切换编码协议!");
        //        uvc_streamOffBtn_Click(null, null);
        //        await Task.Delay(1000);

        //        LogSaveOutput($"正在拉取H264 {uvc_x}x{uvc_y},预览10s");
        //        await camera1.StartupCapture(cameras[uvcCameraIndex], uvcCameraIndex, "H264", checkBoxDecodeTest.Checked);
        //        await Task.Delay(10000);
        //        LogSaveOutput($"拉流完成,等待切换编码协议!");
        //        uvc_streamOffBtn_Click(null, null);
        //        await Task.Delay(1000);

        //        LogSaveOutput($"正在拉取NV12 {uvc_x}x{uvc_y},预览10s");
        //        await camera1.StartupCapture(cameras[uvcCameraIndex], uvcCameraIndex, "NV12", checkBoxDecodeTest.Checked);
        //        await Task.Delay(10000);
        //        LogSaveOutput($"拉流完成,等待切换编码协议!");
        //        uvc_streamOffBtn_Click(null, null);
        //        await Task.Delay(1000);
        //    }
        //}

        private async void uvc_streamOnBtn_Click(object sender, EventArgs e)
        {
            try
            {
                int w = int.Parse(input1_uvc_x.Text);
                int h = int.Parse(input2_uvc_y.Text);
                string format = input_Uvctype.Text; // 从界面获取格式
                await StartUVC(w, h, format);
            }
            catch(Exception ex)
            {

            }
            

        }
      
        private void uvc_streamOffBtn_Click(object sender, EventArgs e)
        {
            int width = pictureBox_uvcStream.Width;
            int height = pictureBox_uvcStream.Height;
            if (camera1 != null)
            {
                camera1.Dispose();
            }
            Bitmap bmp = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                pictureBox_uvcStream.Image = bmp;
            }
            pictureBox_uvcStream.Invalidate();
            pictureBox_uvcStream.Refresh();

        }

        private async void uvcStreamTakePicBtn_Click(object sender, EventArgs e)
        {
            uvcStreamTakePicBtn.Loading = true;

            LogSaveOutput("拍摄路径：\n" + await uvcTaskSnapShot("Seewo Lubo", "case13_uvc全景高分辨率模式切换分辨率压测", "教师全景"));

            uvcStreamTakePicBtn.Loading = false;
        }

        private async Task<string> uvcTaskSnapShot(string cameraName, string folder, string name)
        {
            string ipSafe = _currentIp.Replace(".", "_");
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", ipSafe, folder);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string fileName = $"[{DateTime.Now:yyyyMMddHHmmss}]{name}.png";
                string fullPath = Path.Combine(dir, fileName);

                // === 核心修改：直接调用 VMR9 截图 ===
                bool success = camera1.Snapshot(fullPath);

                if (!success)
                {
                    LogSaveOutput($"截图失败：请检查视频是否正在播放");
                    return "";
                }

                LogSaveOutput($"截图成功: {fileName}");
                await Task.Delay(100); // 稍微防抖

                return fullPath;
            }
            catch (Exception ex)
            {
                LogSaveOutput($"Snapshot Err: {ex.Message}");
                return null;
            }
        }

        private List<string> getUVCCameraSupportResolution(string cameraName)
        {
            List<string> uvcSupportResolutions = new List<string>();

            uvcSupportResolutions.Clear();

            // 1. 获取所有视频设备
            var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

            // 2. 找到指定名称的相机
            var target = devices.Cast<FilterInfo>().FirstOrDefault(d => d.Name == cameraName);
            if (target != null)
            {
                // 3. 创建设备实例并读取分辨率
                var videoDevice = new VideoCaptureDevice(target.MonikerString);

                foreach (var cap in videoDevice.VideoCapabilities)
                {
                    LogSaveOutput($"分辨率: {cap.FrameSize.Width} x {cap.FrameSize.Height}");
                    uvcSupportResolutions.Add($"{cap.FrameSize.Width}x{cap.FrameSize.Height}");
                }
                return uvcSupportResolutions;
            }
            else
            {
                LogSaveOutput("未找到指定相机");
                return null;
            }
        }

        private async void setUvcPanoramicBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetVideoStreamUSBUVCType("panoramicMain"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"设置全景流UVC异常！\n{ex.ToString()}");
            }

        }

        private async void setUvcCloseUpBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetVideoStreamUSBUVCType("closeUpMain"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"设置特写流UVC异常！\n{ex.ToString()}");
            }

        }



        private async void checkbox_7streamRTSPOn_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    panoramicMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    panoramicSubStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpSubStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai1StreanOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai2StreanOnBtn_Click(null, null);
                    await Task.Delay(100);
                    ai3StreanOnBtn_Click(null, null);
                    await Task.Delay(100);
                    LogSaveOutput("RTSP流启动成功");
                }
                else
                {
                    panoramicMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    panoramicSubStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpSubStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai1StreanOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai2StreanOffBtn_Click(null, null);
                    await Task.Delay(100);
                    ai3StreanOffBtn_Click(null, null);
                    await Task.Delay(100);
                    LogSaveOutput("RTSP流停止成功");
                }
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        bool stopEptzCircleCurTest = false;
        private async void checkbox_eptzCircleTest_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    stopEptzCircleCurTest = false;
                    LogSaveOutput("启动电子云台移动成功");
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            eptzControlBtn_Click(null, null);
                            await Task.Delay(21000);
                            if (stopEptzCircleCurTest)
                            {
                                LogSaveOutput("停止电子云台移动测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopEptzCircleCurTest = true;
                }
                LogSaveOutput($"电子云台移动Action：stopTest - {stopEptzCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        bool stopPtzCircleCurTest = false;
        private async void checkbox_ptzCircleTest_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    stopPtzCircleCurTest = false;
                    LogSaveOutput("启动物理云台移动成功");
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            ptzMachineControlBtn_Click(null, null);
                            await Task.Delay(21000);
                            if (stopPtzCircleCurTest)
                            {
                                LogSaveOutput("停止物理云台移动测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopPtzCircleCurTest = true;
                }
                LogSaveOutput($"物理云台移动Action：stopTest - {stopPtzCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        bool stopZoomCircleCurTest = false;
        private async void checkbox_zoomCircleTest_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    stopZoomCircleCurTest = false;
                    LogSaveOutput("启动循环光变成功");
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            paranomicZoomBtn_Click(null, null);
                            closeUpZoomBtn_Click(null, null);
                            await Task.Delay(5000);
                            if (stopZoomCircleCurTest)
                            {
                                LogSaveOutput("停止循环光变测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopZoomCircleCurTest = true;
                }
                LogSaveOutput($"光变Action：stopTest - {stopZoomCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void paranomicZoomBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetZoomAction(0, "zoomIn"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetZoomAction(0, "zoomOut"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"全景变倍异常！\n{ex.ToString()}");
            }

        }

        private async void closeUpZoomBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetZoomAction(1, "zoomIn"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetZoomAction(1, "zoomOut"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"特写变倍异常！\n{ex.ToString()}");
            }

        }

        private async void ptzMachineControlBtn_Click(object sender, EventArgs e)
        {
            try
            {
                ptzGoHomeBtn_Click(null, null);
                await Task.Delay(3000);

                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台控制异常！\n{ex.ToString()}");
            }


        }

        private async void eptzControlBtn_Click(object sender, EventArgs e)
        {
            try
            {
                ptzGoHomeBtn_Click(null, null);
                await Task.Delay(3000);

                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "top"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                await Task.Delay(3000);
                LogSaveOutput(await _api.SetPtzControlAction(0, 5, "down"));
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                LogSaveOutput($"电子云台控制异常！\n{ex.ToString()}");
            }

        }

        private async void ptzGoHomeBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetPtzGoHomeAction(1));
                LogSaveOutput(await _api.SetPtzGoHomeAction(0));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台复位异常：\n {ex.ToString()}");
            }

        }

        private async void rebootDevBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.RebootCurDevice());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"重启设备触发异常！\n{ex.ToString()}");
            }

        }

        string curNetWorkConfig = "";
        private async void getNetWorkConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(curNetWorkConfig = await _api.GetCurNetWorkConfig());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取网络配置异常！\n{ex.ToString()}");
            }

        }

        private async void setUdhcpcBtn_Click(object sender, EventArgs e)
        {
            try
            {
                JArray array = JArray.Parse(curNetWorkConfig);
                foreach (JObject item in array)
                {
                    if (item["ipv4"] != null)
                    {
                        item["ipv4"]["dhcp"] = true;
                    }
                    if (item["ipv6"] != null)
                    {
                        item["ipv6"]["dhcp"] = true;
                    }
                }
                curNetWorkConfig = array.ToString(Formatting.Indented);
                LogSaveOutput(curNetWorkConfig);
                LogSaveOutput(await _api.SetCurNetWorkConfig(curNetWorkConfig));
            }
            catch (Exception ex)
            {
                LogSaveOutput("设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
            }
        }

        private void chooseOta1PacketBtn_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            if (ofd.ShowDialog().ToString() == "OK")
            {
                string otaPacketFilePath = ofd.FileName;
                LogSaveOutput($"当前选择的升级包为：{otaPacketFilePath}");
                input_otaPacketPath1.Text = otaPacketFilePath;
                //LogSaveOutput(await _api.UploadFirmwareAsync_SKDL_new(otaPacketFilePath));
            }
            else
            {
                LogSaveOutput("取消升级！");
            }
        }

        private void chooseOta2PacketBtn_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            if (ofd.ShowDialog().ToString() == "OK")
            {
                string otaPacketFilePath = ofd.FileName;
                LogSaveOutput($"当前选择的升级包为：{otaPacketFilePath}");
                input_otaPacketPath2.Text = otaPacketFilePath;
            }
            else
            {
                LogSaveOutput("取消升级！");
            }
        }

        private async void otaStartBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.StartUpdate());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"触发升级异常！\n{ex.ToString()}");
            }

        }

        private async void checkUpgradeStatusBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.CheckUpgradeStaus("progress"));
                LogSaveOutput(await _api.CheckUpgradeStaus("status"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"查询升级状态异常！\n{ex.ToString()}");
            }

        }

        // 上传ota包到板端
        private async Task<string> uploadOtaPacketToDev(string otaPacketPath)
        {
            return await _api.UploadFirmwareAsync_SKDL_new(otaPacketPath);
        }

        private async void uploadOtaPacketToDevBtn_Click(object sender, EventArgs e)
        {
            try
            {
                DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
               "手动上传功能（临时视使用！",
               $"该按钮只作为临时升级时使用，弹框确认后，默认会按ota1包进行上传，请注意！", AntdUI.TType.Info));
                if (result == DialogResult.OK)
                {
                    LogSaveOutput(await _api.UploadFirmwareAsync_SKDL_new(input_otaPacketPath1.Text));
                }
                else
                {
                    LogSaveOutput("取消上传！");
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"上传固件包异常！\n{ex.ToString()}");
            }


        }

        private async Task<string> getSysVersion()
        {
            return await _api.GetSysVerison();
        }

        private async void getSysVersionBtn_Click(object sender, EventArgs e)
        {
            try
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this, "版本提示", $"当前版本是：{await _api.GetSysVerison()}", AntdUI.TType.Info));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"当前设备版本获取异常！\n{ex.ToString()}");
            }

        }

        private async void ptzStressTestBtn_Click(object sender, EventArgs e)
        {
            await Task.Run(async () =>
            {
                buttonGetToken_Click(null, null);
                int i = 1;
                while (true)
                {
                    try
                    {
                        if (i % 10 == 0)
                        {
                            buttonGetToken_Click(null, null);
                        }

                        LogSaveOutput($"第{i}次云台开始转动");
                        ptzGoHomeBtn_Click(null, null);
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "top"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "down"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "left"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "top"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "right"));
                        await Task.Delay(2000);
                        LogSaveOutput(await _api.SetPtzControlAction(0, 5, "down"));
                        await Task.Delay(2000);
                        LogSaveOutput($"第{i}次云台本次转动完成！开始静置30秒");
                        await Task.Delay(30000);
                        i++;
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"云台重载测试异常！\n{ex.ToString()}");
                    }

                }
            });
        }


        string relayPort = "";
        string relayBaudRate = "9600";

        private async void connectRelayBtn_Click(object sender, EventArgs e)
        {
            try
            {
                await Task.Run(() =>
                {
                    LogSaveOutput("刷新串口指令");
                    string[] ports = SerialPort.GetPortNames();
                    if (ports.Length.ToString() == "0")
                    {
                        ports = new string[] { "未检索到串口" };
                        LogSaveOutput("未检索到串口");
                    }
                    Array.Sort(ports);
                    foreach (KeyValuePair<string, string> kvp in WindowsFunc.GetPortDeviceName())
                    {
                        if (kvp.Key.Contains("Silabser") || kvp.Key.Contains("USBSER"))
                        {
                            relayPort = kvp.Value;
                            WindowsFunc.port_id = relayPort;
                            WindowsFunc.baudrate = relayBaudRate;
                            //根据当前串口对象，来判断操作
                            if (WindowsFunc.comm.IsOpen)
                            {
                                //打开时点击，则关闭串口
                                WindowsFunc.comm.Close();
                            }
                            else
                                WindowsFunc.OpenSerialPort();

                            if (WindowsFunc.comm.IsOpen)
                            {
                                LogSaveOutput("关闭串口");
                                connectRelayBtn.Text = "关闭串口";
                            }
                            else
                            {
                                LogSaveOutput("打开串口");
                                connectRelayBtn.Text = "打开串口";
                            }

                            LogSaveOutput($"找到继电器控制串口{relayPort}，连接继电器成功");
                            break;
                        }
                        else
                        {
                            LogSaveOutput("未找到继电器控制串口，请检查接线是否正确！");
                        }
                    }

                });
            }
            catch (Exception ex)
            {
                LogSaveOutput($"连接继电器异常，请检查！\n{ex.ToString()}");
            }
        }


        // 类级别声明锁对象
        private static readonly object _serialPortLock = new object();

        public async Task<bool> controlRelay(int doNum, bool switchStatus, int type)
        {
            bool result = false;
            byte[] info;
            lock (_serialPortLock)
            {
                if (WindowsFunc.comm.IsOpen)
                {
                    try
                    {
                        if (type == 0)
                        {
                            info = CModbusDll.WriteDO(Convert.ToInt16("254"), doNum, switchStatus);
                        }
                        else
                        {
                            info = CModbusDll.WriteAllDO(Convert.ToInt16("254"), doNum, switchStatus);
                        }

                        byte[] rst = WindowsFunc.sendinfo(info);
                        Thread.Sleep(100);
                        result = true;
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"锁内代码报错 - 发送信息失败：{ex.Message}");
                        result = false;
                    }
                }
                else
                {
                    LogSaveOutput("串口未连接，无法操控！");
                }
                LogSaveOutput("继电器执行操作");
            }

            return result;
        }

        /// <summary>
        /// 读取指定继电器的状态
        /// </summary>
        /// <param name="doNum">
        /// // 用户输入从1开始递增，此处传入-1从0开始索引
        /// 继电器索引 (从0开始，例如0代表开关1，8代表开关9)</param>
        /// <returns>true: 打开(ON), false: 关闭(OFF) 或 读取失败</returns>
        public async Task<bool> readRelayStatus(int doNum)
        {
            // 用户输入从1开始递增，此处传入-1从0开始索引
            doNum = doNum - 1;
            // 基础检查
            if (WindowsFunc.comm == null || !WindowsFunc.comm.IsOpen)
            {
                LogSaveOutput("串口未连接，无法读取！");
                return false;
            }

            try
            {
                // 发送读取指令
                // 注意：此处假设 CModbusDll.ReadDO 返回包含所有状态的完整数据包
                byte[] info = CModbusDll.ReadDO(Convert.ToInt16("254"), Convert.ToInt16(16)); // 建议显式读取足够长度，例如 16位

                // 打印发送指令日志
                string hexLog = BitConverter.ToString(info).Replace("-", " ");
                //Console.WriteLine($"TX - {hexLog}");

                // 发送并获取响应
                byte[] rst = WindowsFunc.sendinfo(info);

                // 校验响应数据
                if (rst != null && rst.Length > 0)
                {
                    // Modbus解析逻辑：rst[0] 就是数据起始字节。

                    int offset = 0; // 如果 rst 包含包头，这里改为 3
                    int byteIndex = offset + (doNum / 8); // 第几个字节
                    int bitIndex = doNum % 8;             // 第几位

                    // 安全检查：防止数组越界
                    if (byteIndex >= rst.Length)
                    {
                        LogSaveOutput($"读取失败：返回数据长度不足 (Index: {byteIndex}, Len: {rst.Length})");
                        return false;
                    }

                    // 位运算获取状态
                    // 右移 bitIndex 位，然后与 1 进行与运算
                    bool isTurnOn = ((rst[byteIndex] >> bitIndex) & 0x01) == 1;

                    LogSaveOutput($"读取继电器 [{doNum}] 状态: {(isTurnOn ? "开启" : "关闭")}");

                    await Task.Delay(50); // 稍微延时，防止高频调用阻塞
                    return isTurnOn;
                }
                else
                {
                    LogSaveOutput("读取失败：未收到有效数据");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"读取异常: {ex.Message}");
                return false;
            }
        }


        // 继电器开关1 - 开 & 关
        private async void switch1RelayOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(0, true, 0) ? "success" : "failed");
        }

        private async void switch1RelayOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(0, false, 0) ? "success" : "failed");
        }

        private async void switch2RelayOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(1, true, 0) ? "success" : "failed");
        }

        private async void switch2RelayOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(1, false, 0) ? "success" : "failed");
        }

        private async void switch3RelayOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(2, true, 0) ? "success" : "failed");
        }

        private async void switch3RelayOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(2, false, 0) ? "success" : "failed");
        }

        private async void switch4RelayOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(3, true, 0) ? "success" : "failed");
        }

        private async void switch4RelayOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(3, false, 0) ? "success" : "failed");
        }

        private async void switch5RelayOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(4, true, 0) ? "success" : "failed");
        }

        private async void switch5RelayOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(4, false, 0) ? "success" : "failed");
        }

        private async void electricAllOnBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(16, true, 1) ? "success" : "failed");
        }

        private async void electricAllOffBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await controlRelay(16, false, 1) ? "success" : "failed");
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            // 在 Form_Load 或构造函数中执行一次
            foreach (var control in testSwitchGroup.Controls)
            {
                // 确保只处理 Checkbox，防止报错
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 把最初的 Text 存到 Tag 里
                    checkSwitchItem.Tag = checkSwitchItem.Text;
                }
            }
            // 初始化网络流播放器
            initNetWorkStreamPlayer();

            // 初始化VC51测试2路流的播放器
            initVC51TwoStreamPlayer();

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "测试提示",
                $"欢迎使用测试工具！是否先清空工具缓存日志？", AntdUI.TType.Info));
            if (result == DialogResult.OK)
            {
                File.Delete(_logFilePath);
                AntdUI.Message.open(new AntdUI.Message.Config(this, $"日志{_logFilePath}已清空！", AntdUI.TType.Success));
            }
        }

        // 推杆按压一次
        public async void pressPowerButtonOneTimes()
        {
            try
            {
                LogSaveOutput("触发推杆按压 - 前进");
                pushForwardBtn_Click(null, null);
                await Task.Delay(5000);
                LogSaveOutput("触发推杆按压 - 后退");
                pushBackwardBtn_Click(null, null);
                await Task.Delay(1000);
                LogSaveOutput("触发推杆按压 - 完成");
            }
            catch (Exception ex)
            {

            }
        }


        private async void TestCase40_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前上下电5000次压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text;
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            switch (switchIndex_now)
                            {
                                case 0:
                                    switchIndex_now = int.Parse(newSwitch1.Text);
                                    break;
                                case 1:
                                    switchIndex_now = int.Parse(newSwitch2.Text);
                                    break;
                                case 2:
                                    switchIndex_now = int.Parse(newSwitch3.Text);
                                    break;
                                case 3:
                                    switchIndex_now = int.Parse(newSwitch4.Text);
                                    break;
                                case 4:
                                    switchIndex_now = int.Parse(newSwitch5.Text);
                                    break;
                                default:
                                    break;
                            }

                            LogSaveOutput($"【当前设备{curTestIP_now}】 -- 对应测试开关控制：【{switchIndex_now}】");

                            Task.Run(async () =>
                            {
                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"{curTestIP_now} - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 登录异常: {ex.Message}");
                                }

                                int testCount = 1;
                                string testResult = "Fail";

                                // 结果呈现，次数增加
                                bool isSuccess = false;
                                while (true)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试开始……");
                                    // 先下电 - 松开开关
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(3000);
                                    // 再上电 - 长按6秒后松开开关
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(6000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(5000);

                                    int bootCountTimes = 0;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(1000);
                                        string token_now = await apiTestItem.LoginAsync();
                                        LogSaveOutput($"token now is : {token_now}");
                                        if (bootCountTimes >= 300)
                                        {
                                            LogSaveOutput($"{curTestIP_now} - 测试结束，当前第{testCount}次上电重启5分钟超时，无法获取到token，请检查，测试停止！");
                                            item.TestResult = "FAIL";
                                            isSuccess = false;
                                            return;
                                        }
                                        if (!string.IsNullOrEmpty(token_now))
                                        {
                                            isSuccess = true;
                                            LogSaveOutput($"{curTestIP_now} - 第{testCount}次上电重启完成，Token 获取成功{token_now},设备重启完成，即将开始测试！");
                                            break;
                                        }
                                        else
                                        {
                                            LogSaveOutput($"{curTestIP_now} - Token 获取中，重启中，请稍等……");
                                            continue;
                                        }
                                    }

                                    LogSaveOutput($"{curTestIP_now} - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestCount++;
                                        item.TestResult = "PASS";
                                        checkBoxDicts[switchIndex].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        // 先下电 - 松开开关
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(3000);
                                        // 再上电 - 长按6秒后松开开关
                                        await controlRelay(switchIndex_now, true, 0);
                                        await Task.Delay(6000);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(5000);
                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束FAIL");
                                        return;
                                    }
                                }
                            });

                            await Task.Delay(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase39_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置dhcp 为true，自动获取ip
            getNetWorkConfigBtn_Click(null, null);
            await Task.Delay(100);
            setUdhcpcBtn_Click(null, null);
            await Task.Delay(100);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        bool logic1 = false;
                        bool logic2 = false;
                        bool logic3 = false;
                        bool logic4 = false;
                        bool logic5 = false;
                        bool logic6 = false;
                        bool logic7 = false;
                        bool logic8 = false;

                        // 逻辑测试开始
                        //Logic 1 -  切到高分模式：高分模式-拉2路 [1、2]
                        hiResModeBtn_Click(null, null);
                        LogSaveOutput($"即将切到高分辨率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                        await Task.Delay(switchModeTime * 1000);

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        string stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                        string stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                        LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                        List<string> streamResults = new List<string>() { stream1Result, stream2Result };

                        logic1 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                        LogSaveOutput($"逻辑1测试结果为：{(logic1 ? "PASS" : "FAIL")}");

                        if (logic1)
                        {
                            //Logic 2 -  切到高帧率模式：高帧模式-拉4路 [1、2]
                            hiFpsModeBtn_Click(null, null);
                            LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                            await Task.Delay(switchModeTime * 1000);

                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                            stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                            LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                            streamResults = new List<string>() { stream1Result, stream2Result };

                            logic2 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                            LogSaveOutput($"逻辑2测试结果为：{(logic2 ? "PASS" : "FAIL")}");

                            if (logic2)
                            {
                                //Logic 3 -  切到高分模式：高分模式-拉2路 [1、2]
                                hiResModeBtn_Click(null, null);
                                LogSaveOutput($"即将切到高分辨率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                await Task.Delay(switchModeTime * 1000);

                                // 获取token
                                buttonGetToken_Click(null, null);
                                await Task.Delay(1000);

                                stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                                streamResults = new List<string>() { stream1Result, stream2Result };

                                logic3 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                LogSaveOutput($"逻辑3测试结果为：{(logic3 ? "PASS" : "FAIL")}");


                                if (logic3)
                                {
                                    //Logic 4 -  切到性能模式：性能模式-拉2路 [1、2]
                                    extremeModeBtn_Click(null, null);
                                    LogSaveOutput($"即将切到性能模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                    await Task.Delay(switchModeTime * 1000);

                                    // 获取token
                                    buttonGetToken_Click(null, null);
                                    await Task.Delay(1000);

                                    stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - extreme 1 测试结果为：{stream1Result}");
                                    stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                    LogSaveOutput($"视频流 - extreme 2 测试结果为：{stream2Result}");

                                    streamResults = new List<string>() { stream1Result, stream2Result };

                                    logic4 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                    LogSaveOutput($"逻辑4测试结果为：{(logic4 ? "PASS" : "FAIL")}");

                                    if (logic4)
                                    {
                                        //Logic 5 -  切到高分模式：高分模式-拉2路 [1、2]
                                        hiResModeBtn_Click(null, null);
                                        LogSaveOutput($"即将切到高分辨率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                        await Task.Delay(switchModeTime * 1000);

                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);

                                        stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                        stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                        LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                                        streamResults = new List<string>() { stream1Result, stream2Result };

                                        logic5 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                        LogSaveOutput($"逻辑5测试结果为：{(logic5 ? "PASS" : "FAIL")}");
                                        if (logic5)
                                        {
                                            //Logic 6 -  切到高帧率模式：高帧模式-拉4路 [1、2]
                                            hiFpsModeBtn_Click(null, null);
                                            LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                            await Task.Delay(switchModeTime * 1000);

                                            // 获取token
                                            buttonGetToken_Click(null, null);
                                            await Task.Delay(1000);

                                            stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                            LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                            stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                            LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                                            streamResults = new List<string>() { stream1Result, stream2Result };

                                            logic6 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                            LogSaveOutput($"逻辑6测试结果为：{(logic6 ? "PASS" : "FAIL")}");

                                            if (logic6)
                                            {
                                                //Logic 7 -  切到性能模式：性能模式-拉2路 [1、2]
                                                extremeModeBtn_Click(null, null);
                                                LogSaveOutput($"即将切到性能模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                                await Task.Delay(switchModeTime * 1000);

                                                // 获取token
                                                buttonGetToken_Click(null, null);
                                                await Task.Delay(1000);

                                                stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - extreme 1 测试结果为：{stream1Result}");
                                                stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                                LogSaveOutput($"视频流 - extreme 2 测试结果为：{stream2Result}");

                                                streamResults = new List<string>() { stream1Result, stream2Result };

                                                logic7 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                                LogSaveOutput($"逻辑7测试结果为：{(logic7 ? "PASS" : "FAIL")}");

                                                if (logic7)
                                                {
                                                    //Logic 8 -  切到高帧率模式：高帧模式-拉4路 [1、2]
                                                    hiFpsModeBtn_Click(null, null);
                                                    LogSaveOutput($"即将切到高帧率模式并等待{switchModeTime}秒完全切换完成，请稍等……");
                                                    await Task.Delay(switchModeTime * 1000);

                                                    // 获取token
                                                    buttonGetToken_Click(null, null);
                                                    await Task.Delay(1000);

                                                    stream1Result = await CheckStreamPlayedOK("1", _currentIp, player_CloseUpMain, testFolder) ? "PASS" : "FAIL";
                                                    LogSaveOutput($"视频流 - 1 测试结果为：{stream1Result}");
                                                    stream2Result = await CheckStreamPlayedOK("2", _currentIp, player_panoramicMain, testFolder) ? "PASS" : "FAIL";
                                                    LogSaveOutput($"视频流 - 2 测试结果为：{stream2Result}");

                                                    streamResults = new List<string>() { stream1Result, stream2Result };

                                                    logic8 = streamResults.Any(r => r.Contains("FAIL")) ? false : true;
                                                    LogSaveOutput($"逻辑8测试结果为：{(logic8 ? "PASS" : "FAIL")}");

                                                    if (logic8)
                                                    {
                                                        LogSaveOutput("本轮测试全部逻辑测试通过，继续下一轮测试！");
                                                    }
                                                    else
                                                    {
                                                        LogSaveOutput("逻辑8测试失败，测试结束！");
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    LogSaveOutput("逻辑7测试失败，测试结束！");
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                LogSaveOutput("逻辑6测试失败，测试结束！");
                                                break;
                                            }

                                        }
                                        else
                                        {
                                            LogSaveOutput("逻辑4测试失败，测试结束！");
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        LogSaveOutput("逻辑4测试失败，测试结束！");
                                        break;
                                    }
                                }
                                else
                                {
                                    LogSaveOutput("逻辑3测试失败，测试结束！");
                                    break;
                                }

                            }
                            else
                            {
                                LogSaveOutput("逻辑2测试失败，测试结束！");
                                break;
                            }
                        }
                        else
                        {
                            LogSaveOutput("逻辑1测试失败，测试结束！");
                            break;
                        }



                        // 结果呈现，次数增加
                        bool isSuccess = logic1 && logic2 && logic3 && logic4 && logic5 && logic6 && logic7;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }





        // 按一次，等150秒，拉三路流，然后测试，有fail的就停止，没有的就继续测试

        private async void TestCase38_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前休眠唤醒压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            int rebootWaitingTime = 150;
            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {

                // 控制继电器初始化
                LogSaveOutput("控制继电器初始化");
                connectRelayBtn_Click(null, null);
                await Task.Delay(500);

                // 全关一次
                electricAllOffBtn_Click(null, null);

                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text;
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {
                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"{curTestIP_now} - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"{curTestIP_now} - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"{curTestIP_now} - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";
                                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                                int onWaitTime = 5000, offWaitTime = 5000;
                                while (true)
                                {
                                    LogSaveOutput($"开关index {switchIndex_now} - {curTestIP_now} - 第{testCount}次测试开始……");

                                    if (switchIndex_now == 0)
                                    {
                                        // 按压一次power键 -- 如果是第一台设备，就触发按压，其他设备直接等待跟着响应即可
                                        LogSaveOutput($"{curTestIP_now} --  按压一次power键 1");
                                        pressPowerButtonOneTimes();
                                        await Task.Delay(10000);
                                        LogSaveOutput("按压一次power键 2");
                                        pressPowerButtonOneTimes();

                                        LogSaveOutput($"所有测试设备重启中请稍等{rebootWaitingTime}秒……");
                                        await Task.Delay(rebootWaitingTime * 1000 - 10000);
                                    }
                                    else
                                    {
                                        LogSaveOutput($"所有测试设备重启中请稍等{rebootWaitingTime}秒……");
                                        await Task.Delay(rebootWaitingTime * 1000);
                                    }

                                    int bootCountTimes = 0;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(1000);
                                        string token_now = await _api.LoginAsync();
                                        if (bootCountTimes >= 3000)
                                        {
                                            LogSaveOutput($"{curTestIP_now} - 测试结束，当前第{item.TestCount}次休眠唤醒超时，无法获取到token，请检查，测试停止！");
                                            item.TestResult = "FAIL";
                                            return;
                                        }
                                        if (!string.IsNullOrEmpty(token_now))
                                        {
                                            LogSaveOutput($"{curTestIP_now} - 第{item.TestCount}次休眠唤醒完成，Token 获取成功,设备重启完成，即将开始测试！");
                                            break;
                                        }
                                        else
                                        {
                                            LogSaveOutput($"{curTestIP_now} - Token 获取失败，重启中，请稍等……");
                                            continue;
                                        }
                                    }

                                    // 开始拉流测试 -- 更新item的testcount和result
                                    LogSaveOutput($"{curTestIP_now} - 拉流测试中……");
                                    await Task.Delay(onWaitTime);

                                    // 1. 先在外部定义变量，确保后面能访问到
                                    OpenCvRtspPlayer pm = null, ps = null, cm = null, cs = null, ai1 = null, ai2 = null, ai3 = null;
                                    PictureBox pb1 = null, pb2 = null, pb3 = null, pb4 = null, pb5 = null, pb6 = null, pb7 = null;

                                    // 2. 使用 Invoke 强制回到主 UI 线程执行控件创建（解决卡死/报错的关键）
                                    this.Invoke(new Action(() =>
                                    {
                                        // 定义一个临时的本地函数，避免重复写 7 遍相同的代码
                                        PictureBox CreateHiddenPb()
                                        {
                                            var pb = new PictureBox()
                                            {
                                                Size = new System.Drawing.Size(320, 180), // 保持合理大小以确保画质
                                                Location = new System.Drawing.Point(-10000, -10000), // 移出屏幕
                                                Visible = true, // 必须为 true，否则 OpenCvPlayer 逻辑会跳过解码
                                                Parent = this   // 明确指定父容器
                                            };
                                            this.Controls.Add(pb); // 必须加入窗体集合
                                            return pb;
                                        }

                                        // 批量初始化
                                        pb1 = CreateHiddenPb(); pm = new OpenCvRtspPlayer(pb1);
                                        pb2 = CreateHiddenPb(); ps = new OpenCvRtspPlayer(pb2);
                                        pb3 = CreateHiddenPb(); cm = new OpenCvRtspPlayer(pb3);
                                        pb4 = CreateHiddenPb(); cs = new OpenCvRtspPlayer(pb4);
                                        pb5 = CreateHiddenPb(); ai1 = new OpenCvRtspPlayer(pb5);
                                        pb6 = CreateHiddenPb(); ai2 = new OpenCvRtspPlayer(pb6);
                                        pb7 = CreateHiddenPb(); ai3 = new OpenCvRtspPlayer(pb7);
                                    }));

                                    // 每一路拉流，并比对结果
                                    await rtspStreamOn(curTestIP_now, pm, "2", "全景主流");
                                    await rtspStreamOn(curTestIP_now, cm, "1", "特写主流");


                                    // 全景主流拉流测试出结果
                                    string panoramicMain_pic = await SafeSnapshotAsync(pm, testFolder_item, "全景主流");
                                    LogSaveOutput(panoramicMain_pic);
                                    await Task.Delay(100);

                                    // 特写主流拉流测试出结果
                                    string closeUpMain_pic = await SafeSnapshotAsync(cm, testFolder_item, "特写主流");
                                    LogSaveOutput(closeUpMain_pic);
                                    await Task.Delay(100);

                                    if (testCount == 1)
                                    {
                                        ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                        ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                    }
                                    else
                                    {
                                        ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                        ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                    }


                                    bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                    LogSaveOutput($"{curTestIP_now} - 当前休眠唤醒设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                    bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                    LogSaveOutput($"{curTestIP_now} - 当前休眠唤醒设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                                    LogSaveOutput($"{curTestIP_now} - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                    await Task.Delay(checkStreamStatusWaitingTime);
                                    // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                    bool panoramicMainStatusResult = getStreamStatusResult(pm);
                                    LogSaveOutput($"{curTestIP_now} - 当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                    bool closeUpMainStatusResult = getStreamStatusResult(cm);
                                    LogSaveOutput($"{curTestIP_now} - 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                                    // 结果呈现，次数增加
                                    bool isSuccess = panoramicMainResult && closeUpMainResult
                                    && panoramicMainStatusResult && closeUpMainStatusResult;

                                    // 所有流关流

                                    rtspStreamOff(pm, "全景主流");
                                    rtspStreamOff(cm, "特写主流");

                                    LogSaveOutput($"{curTestIP_now} - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestResult = "PASS";
                                        item.TestCount++;
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(offWaitTime);

                                        // 修改间歇上下电逻辑
                                        if (testCount == 500)
                                        {
                                            onWaitTime = 25000;
                                            offWaitTime = 10000;
                                        }
                                        if (testCount == 1000)
                                        {
                                            onWaitTime = 30000;
                                            offWaitTime = 5000;
                                        }

                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"{curTestIP_now} - 第{testCount}次测试结束FAIL");
                                        return;
                                    }
                                }
                            });

                            await Task.Delay(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }
        private async void TestCase37_2(TestCases item)
        {
            /*
               1、先手动设置好预置位1
               2、手动使用工具触发到预置位1->存一张特写图
               3、云台从预置位1到->俯仰-35度转动 -- 持续500次
               4、500次后解除堵转，人工重新恢复预置位1->存一张特写图
               5、人工比对堵转前后前后每个预置位的特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case37_俯仰堵转反向压测！",
                $"请先设置好预置位1，然后点击后开始压测，测试达到500次后，自动停止，手动解除堵转后手动触发预置位1再取一张特写图！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            this.BeginInvoke((Delegate)(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    while (true)
                    {
                        // 触发俯仰-35度堵转到预置位1循环操控
                        checkbox_ptzCruiseReverse35Test.Checked = true;
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(3000);
                        LogSaveOutput("运行中，请稍等……");
                        item.TestCount++;
                        if (item.TestCount >= 500)
                        {
                            item.TestResult = "500次完成，请手动完成剩余测试！";
                            LogSaveOutput("测试次数达成，请停止堵转测试，并手动触发预置位1后采集特写图比对……");
                            break;
                        }
                    }
                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case37_俯仰堵转反向压测结束提醒！",
                $"请解除堵转后，手动触发预置位1，再存取特写图，比对预置位1前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }
            }));
        }

        private async void TestCase36_2(TestCases item)
        {
            /*
               1、先手动设置好预置位1
               2、手动使用工具触发到预置位1->存一张特写图
               3、云台从预置位1到->俯仰90度转动 -- 持续500次
               4、500次后解除堵转，人工重新恢复预置位1->存一张特写图
               5、人工比对堵转前后前后每个预置位的特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case36_俯仰堵转正向压测！",
                $"请先设置好预置位1，然后点击后开始压测，测试达到500次后，自动停止，手动解除堵转后手动触发预置位1再取一张特写图！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            this.BeginInvoke((Delegate)(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    while (true)
                    {
                        // 触发俯仰90度堵转到预置位1循环操控
                        checkbox_ptzCruise90Test.Checked = true;
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(3000);
                        LogSaveOutput("运行中，请稍等……");
                        item.TestCount++;
                        if (item.TestCount >= 500)
                        {
                            item.TestResult = "500次完成，请手动完成剩余测试！";
                            LogSaveOutput("测试次数达成，请停止堵转测试，并手动触发预置位1后采集特写图比对……");
                            break;
                        }
                    }
                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case36_俯仰堵转正向压测结束提醒！",
                $"请解除堵转后，手动触发预置位1，再存取特写图，比对预置位1前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }
            }));
        }

        private async void TestCase35_2(TestCases item)
        {
            /*
               1、先手动设置好预置位1
               2、手动使用工具触发到预置位1->存一张特写图
               3、云台从预置位1到->水平170度转动 -- 持续500次
               4、500次后解除堵转，人工重新恢复预置位1->存一张特写图
               5、人工比对堵转前后前后每个预置位的特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case35_水平堵转正向压测！",
                $"请先设置好预置位1，然后点击后开始压测，测试达到500次后，自动停止，手动解除堵转后手动触发预置位1再取一张特写图！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            this.BeginInvoke((Delegate)(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    while (true)
                    {
                        // 触发水平170度堵转到预置位1循环操控
                        checkbox_ptzCruise170Test.Checked = true;
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(3000);
                        LogSaveOutput("运行中，请稍等……");
                        item.TestCount++;
                        if (item.TestCount >= 500)
                        {
                            item.TestResult = "500次完成，请手动完成剩余测试！";
                            LogSaveOutput("测试次数达成，请停止堵转测试，并手动触发预置位1后采集特写图比对……");
                            break;
                        }
                    }

                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case35_水平堵转正向压测结束提醒！",
                $"请解除堵转后，手动触发预置位1，再存取特写图，比对预置位1前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }
            }));
        }


        private async void TestCase34_2(TestCases item)
        {
            /*
               1、先手动设置好预置位1
               2、手动使用工具触发到预置位1->存一张特写图
               3、云台从预置位1到->水平-170度转动 -- 持续500次
               4、500次后解除堵转，人工重新恢复预置位1->存一张特写图
               5、人工比对堵转前后前后每个预置位的特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case34_水平堵转反向压测！",
                $"请先设置好预置位1，然后点击后开始压测，测试达到500次后，自动停止，手动解除堵转后手动触发预置位1再取一张特写图！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            this.BeginInvoke((Delegate)(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    while (true)
                    {
                        // 触发水平-170度堵转到预置位1循环操控
                        checkbox_ptzCruiseReverse170Test.Checked = true;
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(3000);
                        LogSaveOutput("运行中，请稍等……");
                        item.TestCount++;
                        if (item.TestCount >= 500)
                        {
                            item.TestResult = "500次完成，请手动完成剩余测试！";
                            LogSaveOutput("测试次数达成，请停止堵转测试，并手动触发预置位1后采集特写图比对……");
                            break;
                        }
                    }

                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case34_水平堵转反向压测结束提醒！",
                $"请解除堵转后，手动触发预置位1，再存取特写图，比对预置位1前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }
            }));
        }


        private async void TestCase33_2(TestCases item)
        {
            /*
               1、先手动设置好预置位1\2\3\4\5
               2、工具触发到每个预置位->存一张特写图
               3、云台巡航1->3->5->2->4->1(每个点停留5秒)
               4、48小时后->触发回到每个预置位->存一张特写图
               5、人工比对48小时前后每个预置位的特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case33_云台负载巡航压测开始提醒！",
                $"请先设置好预置位1-2-3-4-5，然后点击后开始压测煲机测试计时，自动停止！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            var start = DateTime.Now; // 1. 记录开始时间

            // 2. 创建并启动定时器 (1秒刷新一次)
            new System.Windows.Forms.Timer { Interval = 1000, Enabled = true }.Tick += (s, e) =>
            {
                // 3. 核心代码：计算差值并转为 String
                string timeStr = (DateTime.Now - start).ToString(@"hh\:mm\:ss");

                // 显示出来 (例如赋值给 Label 或 窗体标题)
                item.TestResult = timeStr;
            };

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    // 预置位1存图
                    input_presetId.Text = "1";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前预置位1特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位2存图
                    input_presetId.Text = "2";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前预置位2特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位3存图
                    input_presetId.Text = "3";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前预置位3特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位4存图
                    input_presetId.Text = "4";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前预置位4特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位5存图
                    input_presetId.Text = "5";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前预置位5特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);


                    // 触发巡航压测48小时
                    checkbox_ptzCruiseTest.Checked = true;
                    while (true)
                    {
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(1000);
                        LogSaveOutput("运行中，请稍等……");
                        if (int.Parse(item.TestResult.Split(":")[0]) >= 48)
                        {
                            LogSaveOutput($"测试停止，当前运行时间：{item.TestResult} -- 超过48小时，等待60秒后即将开始回到预置位后存特写图！");
                            checkbox_ptzCruiseTest.Checked = false;
                            await Task.Delay(60000);
                            break;
                        }
                    }

                    // 48小时候触发预置位 -- 后
                    // 预置位1存图
                    input_presetId.Text = "1";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后预置位1特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位2存图 -- 后
                    input_presetId.Text = "2";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后预置位2特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位3存图 -- 后
                    input_presetId.Text = "3";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后预置位3特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位4存图 -- 后
                    input_presetId.Text = "4";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后预置位4特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    // 预置位5存图 -- 后
                    input_presetId.Text = "5";
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后预置位5特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);


                    // 所有流关流
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case32_云台预置位设置后煲机压测48小时候结束提醒！",
                $"请手动比对48小时前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }


            });
        }

        private async void TestCase32_2(TestCases item)
        {

            /*
               1、先手动设置好预置位
               2、工具触发到预置位->存一张特写图
               3、云台重载煲机48小时
               4、48小时候->触发回到预置位->存一张特写图
               5、人工比对48小时前后2张特写图
             */
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case32_云台预置位设置后煲机压测开始提醒！",
                $"请先设置好预置位1，然后点击后开始压测煲机测试计时，自动停止！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            var start = DateTime.Now; // 1. 记录开始时间

            // 2. 创建并启动定时器 (1秒刷新一次)
            new System.Windows.Forms.Timer { Interval = 1000, Enabled = true }.Tick += (s, e) =>
            {
                // 3. 核心代码：计算差值并转为 String
                string timeStr = (DateTime.Now - start).ToString(@"hh\:mm\:ss");

                // 显示出来 (例如赋值给 Label 或 窗体标题)
                item.TestResult = timeStr;
            };

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                try
                {
                    // 获取token
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    // 触发预置位
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(3000);

                    // 每一路拉流，并比对结果
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(10000);

                    // 特写主流拉流测试出结果
                    string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时前特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                    await Task.Delay(checkStreamStatusWaitingTime);

                    // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                    bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                    LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                    // 开始云台煲机48小时
                    checkbox_ptzCircleTest.Checked = true;
                    while (true)
                    {
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                        await Task.Delay(1000);
                        LogSaveOutput("运行中，请稍等……");
                        if (int.Parse(item.TestResult.Split(":")[0]) >= 48)
                        {
                            LogSaveOutput($"测试停止，当前运行时间：{item.TestResult} -- 超过48小时，等待60秒后即将开始回到预置位后存特写图！");
                            checkbox_ptzCircleTest.Checked = false;
                            await Task.Delay(60000);
                            break;
                        }
                    }

                    // 触发预置位
                    launchPresetIdBtn_Click(null, null);
                    await Task.Delay(10000);
                    // 特写主流拉流测试出结果
                    closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "48小时后特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                    await Task.Delay(checkStreamStatusWaitingTime);

                    // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                    closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                    LogSaveOutput($"当前48小时候特写主流状态测试结果：{closeUpMainStatusResult}");

                    // 所有流关流
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "case32_云台预置位设置后煲机压测48小时候结束提醒！",
                $"请手动比对48小时前后图片偏移量！\n", AntdUI.TType.Success));
                }
                catch (Exception ex)
                {
                    LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                }


            });
        }

        private async void TestCase30_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);


                        // Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K
                        // 设置到uvc出全景模式
                        setUvcPanoramicBtn_Click(null, null);
                        await Task.Delay(100);
                        // uvc 拉流4K 0 3840x2160
                        int width = 3840;
                        int height = 2160;
                        input1_uvc_x.Text = width.ToString();
                        input2_uvc_y.Text = height.ToString();
                        string format = "H264";

                        // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                        string devicePath = null;
                        if (GetCameras("Seewo Lubo").Count > 1)
                        {
                            devicePath = input_curUvcDevicePath.Text; // 使用当前选中的设备路径
                        }

                        bool uvcStarted = await StartUVC(width, height, format, devicePath);
                        if (!uvcStarted)
                        {
                            LogSaveOutput("UVC 启动失败，停止测试");
                            break;
                        }
                        //等待预览12s
                        await Task.Delay(12000);
                        LogSaveOutput($"预览教师全景[{width}x{height} {format}]  12秒");


                        string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC全景[{width}x{height}]");
                        LogSaveOutput(uvc_pic);
                        await Task.Delay(100);

                        bool highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                        LogSaveOutput($"Logic1 -- uvc 全景[{width}x{height}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");

                        if (!highResolutionTeacherResult)
                        {
                            LogSaveOutput($"UVC异常，停止测试");
                        }

                        // 先读取当前配置
                        readAllStreamCurConfig2();
                        await Task.Delay(1000);

                        // 设置主流到1080P - 30fps
                        LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                            .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                            .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));
                        LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
                        LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));

                        // 全景主流拉流
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        bool panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                        LogSaveOutput($"Logic1 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"Logic1 -- 当前全景主流状态测试结果：{panoramicMainStatusResult}");

                        if (highResolutionTeacherResult && panoramicMainResult && panoramicMainStatusResult)
                        {
                            // Logic 2 重新RTSP拉主流 + UCV 拉流MJPEG 1080P

                            // 1、先关流
                            // 所有流关流
                            uvc_streamOffBtn_Click(null, null);
                            await Task.Delay(100);
                            // 所有流关流
                            panoramicMainStreamOffBtn_Click(null, null);
                            await Task.Delay(100);
                            await Task.Delay(circleTestDelayTime * 1000);
                            // 2、再拉流
                            // logic 2 uvc 拉流1080P  1920x1080
                            input1_uvc_x.Text = "1920";
                            input2_uvc_y.Text = "1080";
                            input_Uvctype.Text = "H264";

                            // 每一路拉流，并比对结果,如果多台设备，就指定devicepath压测，单台就0
                            if (GetCameras("Seewo Lubo").Count > 1)
                            {
                                uvcStreamOnSpecificDevicePathBtn_Click(null, null);
                            }
                            else
                            {
                                uvc_streamOnBtn_Click(null, null);
                            }
                            await Task.Delay(5000);
                            LogSaveOutput("预览10秒，请稍等……");
                            await Task.Delay(10000);

                            uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"高分辨率模式教师UVC全景[{1920}x{1080}]");
                            LogSaveOutput(uvc_pic);
                            await Task.Delay(100);

                            highResolutionTeacherResult = checkPICValid(uvc_pic, uvc_pic);
                            LogSaveOutput($"Logic2 -- uvc 全景主流[{1920}x{1080}]测试结果：{highResolutionTeacherResult} -- {uvc_pic} ");
                            if (!highResolutionTeacherResult)
                            {
                                LogSaveOutput($"UVC异常，停止测试");
                                break;
                            }
                            // logic 2 rtsp 
                            // 全景主流拉流
                            panoramicMainStreamOnBtn_Click(null, null);
                            await Task.Delay(100);

                            // 全景主流拉流测试出结果
                            panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                            LogSaveOutput(panoramicMain_pic);
                            await Task.Delay(100);

                            panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                            LogSaveOutput($"Logic2 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                            LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                            await Task.Delay(checkStreamStatusWaitingTime);
                            // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                            panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                            LogSaveOutput($"Logic2 -- 当前全景主流状态测试结果：{panoramicMainStatusResult}");

                            if (highResolutionTeacherResult && panoramicMainResult && panoramicMainStatusResult)
                            {
                                // logic 3 重新RTSP拉流 + 网络流设置为主流 4KP30帧、辅流默认，RTSP 拉流主流
                                // 1、先关流
                                // 所有流关流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                // 所有流关流
                                panoramicMainStreamOffBtn_Click(null, null);
                                await Task.Delay(100);
                                await Task.Delay(circleTestDelayTime * 1000);
                                // rtsp 4k
                                // 先读取当前配置
                                readAllStreamCurConfig2();
                                await Task.Delay(1000);

                                // 设置主流到4k - 30fps
                                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                                    .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                                    .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));
                                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
                                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));

                                // 全景主流拉流
                                panoramicMainStreamOnBtn_Click(null, null);
                                await Task.Delay(100);

                                // 全景主流拉流测试出结果
                                panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                LogSaveOutput(panoramicMain_pic);
                                await Task.Delay(100);

                                panoramicMainResult = checkPICValid(panoramicMain_pic, panoramicMain_pic);
                                LogSaveOutput($"Logic3 -- rtsp 全景主流测试结果：{panoramicMainResult}");

                                LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                await Task.Delay(checkStreamStatusWaitingTime);
                                // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                LogSaveOutput($"Logic3 -- 当前全景主流4K30FPS状态测试结果：{panoramicMainStatusResult}");

                                if (panoramicMainResult && panoramicMainStatusResult)
                                {
                                    item.TestCount++;
                                    item.TestResult = "PASS";
                                    LogSaveOutput($"【Logic3 测试完成 -- 第{item.TestCount}次测试PASS，即将开始下一次测试……】");
                                    await Task.Delay(circleTestDelayTime * 1000);
                                    continue;
                                }
                                else
                                {
                                    LogSaveOutput("测试停止，当前Logic 3 测试失败：\n Logic 3 网络流设置为主流 4K 30帧、辅流默认，RTSP 拉流主流" +
                                    $"rtsp 结果：{panoramicMainResult} - {panoramicMainStatusResult}");
                                    item.TestResult = "FAIL";
                                    break;
                                }

                            }
                            else
                            {
                                LogSaveOutput("测试停止，当前Logic 1 测试失败：\n Logic 1 网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流 + UVC 拉流H264 4K" +
                                    $"uvc 结果：{highResolutionTeacherResult} + rtsp 结果：{panoramicMainResult} - {panoramicMainStatusResult}");
                                item.TestResult = "FAIL";
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }


                }
            });
        }
        private async void TestCase29_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("性能模式流1_2", player_panoramicMain);
            openCvRtspPlayersDict.Add("性能模式流2_2", player_panoramicSub);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;

            // 切换到性能模式 -- 等待150秒
            LogSaveOutput("即将切换到性能模式，请稍等150秒……");
            extremeModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url} -- 预览10分钟，请稍等……");
                        await Task.Delay(60000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"性能模式下 -- 当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"性能模式下 -- {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }
        private async void TestCase28_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_extreme_pic, next_extreme_pic = "";
            string ori_extreme2_pic, next_extreme2_pic = "";

            // 切换到性能模式 -- 等待150秒
            LogSaveOutput("即将切换到性能模式，请稍等150秒……");
            extremeModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolution2();
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfig2();
                        await Task.Delay(1000);
                        changeResolutionOrder2();
                        await Task.Delay(1000);
                        changeAllStreamCurConfig2();
                        await Task.Delay(1000);


                        // 每一路拉流，并比对结果
                        extreme1StreamOn2();
                        await Task.Delay(5000);
                        extreme2StreamOn2();
                        await Task.Delay(5000);

                        // 性能模式流1测试出结果
                        string extreme_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "性能模式流1");
                        LogSaveOutput(extreme_pic);
                        await Task.Delay(100);

                        // 性能模式流2测试出结果
                        string extreme2_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "性能模式流2");
                        LogSaveOutput(extreme2_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_extreme_pic = extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }
                        else
                        {
                            ori_extreme_pic = next_extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = next_extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }


                        bool extremeResult = checkPICValid(ori_extreme_pic, next_extreme_pic);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式后 -- 性能模式流 - 1测试结果：{extremeResult} -- {ori_extreme_pic} : {next_extreme_pic}");
                        bool extreme2Result = checkPICValid(ori_extreme2_pic, next_extreme2_pic);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式后 -- 性能模式流 - 2测试结果：{extreme2Result} -- {ori_extreme2_pic} : {next_extreme2_pic}");


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool extremeStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式流1状态测试结果：{extremeStatusResult}");
                        bool extreme2StatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"性能模式分辨率轮询压测 - 当前性能模式流2状态测试结果：{extreme2StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = extremeResult && extreme2Result
                        && extremeStatusResult && extreme2StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        extreme1StreamOff2();
                        await Task.Delay(100);
                        extreme2StreamOff2();
                        await Task.Delay(5000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }

                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }
        private async void TestCase27_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("全景主流", player_panoramicMain);
            openCvRtspPlayersDict.Add("特写主流", player_CloseUpMain);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;

            // 切换到高帧率模式 -- 等待150秒
            LogSaveOutput("即将切换到高帧率模式，请稍等150秒……");
            hiFpsModeBtn_Click(null, null);
            await Task.Delay(150000);


            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url}");
                        await Task.Delay(10000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"高帧率模式下 -- 当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"高帧率模式下 -- {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }

        private async void TestCase26_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");


            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            // 切换到高帧率模式 -- 等待150秒
            LogSaveOutput("即将切换到高帧率模式，请稍等150秒……");
            hiFpsModeBtn_Click(null, null);
            await Task.Delay(150000);

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolution2();
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfig2();
                        await Task.Delay(1000);
                        changeResolutionOrder2();
                        await Task.Delay(1000);
                        changeAllStreamCurConfig2();
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"高帧率模式下 -- 当前特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"高帧率模式下 -- 当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"高帧率模式下 -- 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase25_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前逻辑2上下电间歇时间短电压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text.Trim();
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {

                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"【{curTestIP_now}】 - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";
                                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                                int onWaitTime = 5000, offWaitTime = 5000;
                                while (true)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试开始……");
                                    // 先下电
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    // 再上电
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);

                                    int bootCountTimes = 0;
                                    bool pingSuccess = false;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(5000);
                                        WindowsFunc.IsHostReachableAsync(curTestIP_now).ContinueWith(reachabilityTask =>
                                        {
                                            if (reachabilityTask.Result)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备在线");
                                                pingSuccess = true;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备离线");
                                            }
                                        });
                                        if (pingSuccess)
                                        {
                                            apiTestItem = null; // 先释放之前的实例，确保资源清理
                                            apiTestItem = new HttpApi_stu(curTestIP_now);
                                            string token_now = await apiTestItem.LoginAsync();
                                            if (bootCountTimes >= 3000)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 测试结束，当前第{item.TestCount}次上电重启超时，无法获取到token，请检查，测试停止！");
                                                item.TestResult = "FAIL";
                                                return;
                                            }
                                            if (!string.IsNullOrEmpty(token_now))
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 第{item.TestCount}次上电重启完成，Token 获取成功,设备重启完成，即将开始测试！");
                                                break;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败，重启中，请稍等……");
                                                continue;
                                            }
                                        }
                                    }

                                    // 开始拉流测试 -- 更新item的testcount和result
                                    LogSaveOutput($"【{curTestIP_now}】 - 拉流测试中……");
                                    await Task.Delay(onWaitTime);

                                    // 1. 先在外部定义变量，确保后面能访问到
                                    OpenCvRtspPlayer pm = null, ps = null, cm = null, cs = null, ai1 = null, ai2 = null, ai3 = null;
                                    PictureBox pb1 = null, pb2 = null, pb3 = null, pb4 = null, pb5 = null, pb6 = null, pb7 = null;
                                    bool isSuccess = false;
                                    void SafeDisposePlayer(OpenCvRtspPlayer player)
                                    {
                                        try { player?.Dispose(); } catch { }
                                    }

                                    void SafeDisposePictureBox(PictureBox pb)
                                    {
                                        if (pb == null) return;
                                        if (IsDisposed || Disposing)
                                        {
                                            try { pb.Dispose(); } catch { }
                                            return;
                                        }
                                        BeginInvoke(new Action(() =>
                                        {
                                            try
                                            {
                                                if (pb.Parent != null)
                                                {
                                                    pb.Parent.Controls.Remove(pb);
                                                }
                                                pb.Dispose();
                                            }
                                            catch { }
                                        }));
                                    }

                                    try
                                    {
                                        // 2. 使用 Invoke 强制回到主 UI 线程执行控件创建（解决卡死/报错的关键）
                                        this.Invoke(new Action(() =>
                                        {
                                            // 定义一个临时的本地函数，避免重复写 7 遍相同的代码
                                            PictureBox CreateHiddenPb()
                                            {
                                                var pb = new PictureBox()
                                                {
                                                    Size = new System.Drawing.Size(320, 180), // 保持合理大小以确保画质
                                                    Location = new System.Drawing.Point(-10000, -10000), // 移出屏幕
                                                    Visible = true, // 必须为 true，否则 OpenCvPlayer 逻辑会跳过解码
                                                    Parent = this   // 明确指定父容器
                                                };
                                                this.Controls.Add(pb); // 必须加入窗体集合
                                                return pb;
                                            }

                                            // 批量初始化
                                            pb1 = CreateHiddenPb(); pm = new OpenCvRtspPlayer(pb1);
                                            pb2 = CreateHiddenPb(); ps = new OpenCvRtspPlayer(pb2);
                                            pb3 = CreateHiddenPb(); cm = new OpenCvRtspPlayer(pb3);
                                            pb4 = CreateHiddenPb(); cs = new OpenCvRtspPlayer(pb4);
                                            pb5 = CreateHiddenPb(); ai1 = new OpenCvRtspPlayer(pb5);
                                            pb6 = CreateHiddenPb(); ai2 = new OpenCvRtspPlayer(pb6);
                                            pb7 = CreateHiddenPb(); ai3 = new OpenCvRtspPlayer(pb7);
                                        }));

                                        // 每一路拉流，并比对结果
                                        await rtspStreamOn(curTestIP_now, pm, "2", "全景主流");
                                        await rtspStreamOn(curTestIP_now, cm, "1", "特写主流");


                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(pm, testFolder_item, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);

                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(cm, testFolder_item, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);

                                        if (testCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前间歇时间上下电重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                                        LogSaveOutput($"【{curTestIP_now}】 - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(pm);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(cm);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                                        // 结果呈现，次数增加
                                        isSuccess = panoramicMainResult && closeUpMainResult
                                        && panoramicMainStatusResult && closeUpMainStatusResult;
                                    }
                                    finally
                                    {
                                        SafeDisposePlayer(pm);
                                        SafeDisposePlayer(ps);
                                        SafeDisposePlayer(cm);
                                        SafeDisposePlayer(cs);
                                        SafeDisposePlayer(ai1);
                                        SafeDisposePlayer(ai2);
                                        SafeDisposePlayer(ai3);

                                        SafeDisposePictureBox(pb1);
                                        SafeDisposePictureBox(pb2);
                                        SafeDisposePictureBox(pb3);
                                        SafeDisposePictureBox(pb4);
                                        SafeDisposePictureBox(pb5);
                                        SafeDisposePictureBox(pb6);
                                        SafeDisposePictureBox(pb7);
                                    }

                                    // 所有流关流
                                    rtspStreamOff(pm, "全景主流");
                                    rtspStreamOff(cm, "特写主流");

                                    LogSaveOutput($"【{curTestIP_now}】 - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        testCount++;
                                        testResult = "PASS";
                                        item.TestResult = "PASS";
                                        item.TestCount++;
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(offWaitTime);

                                        // 修改间歇上下电逻辑
                                        if (testCount == 500)
                                        {
                                            onWaitTime = 25000;
                                            offWaitTime = 10000;
                                        }
                                        if (testCount == 1000)
                                        {
                                            onWaitTime = 30000;
                                            offWaitTime = 5000;
                                        }

                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束FAIL");
                                        return;
                                    }


                                }
                            });

                            await Task.Delay(3000);
                        }


                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase24_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前逻辑1上下电5000次压测测试项？",
                "点击确认后，当前测试会开始，望悉知！",
                AntdUI.TType.Warn));

            foreach (var control in testSwitchGroup.Controls)
            {
                if (control is AntdUI.Checkbox checkSwitchItem)
                {
                    // 检查 Tag 是否有值，有的话就还原给 Text
                    if (checkSwitchItem.Tag != null)
                    {
                        checkSwitchItem.Text = checkSwitchItem.Tag.ToString();
                    }
                }
            }

            item.TestCount++;
            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                connectRelayBtn_Click(null, null);
                await Task.Delay(1000);
                electricAllOffBtn_Click(null, null);
                await Task.Delay(1000);
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                List<int> testItemIndexs;
                Dictionary<int, AntdUI.Input> inputDicts = new Dictionary<int, AntdUI.Input>();
                inputDicts[0] = input_elecIP1;
                inputDicts[1] = input_elecIP2;
                inputDicts[2] = input_elecIP3;
                inputDicts[3] = input_elecIP4;
                inputDicts[4] = input_elecIP5;

                Dictionary<int, AntdUI.Checkbox> checkBoxDicts = new Dictionary<int, AntdUI.Checkbox>();
                checkBoxDicts[0] = checkbox_switch1;
                checkBoxDicts[1] = checkbox_switch2;
                checkBoxDicts[2] = checkbox_switch3;
                checkBoxDicts[3] = checkbox_switch4;
                checkBoxDicts[4] = checkbox_switch5;

                this.BeginInvoke(async () =>
                {
                    testItemIndexs = new List<int>();
                    try
                    {
                        // 遍历需要测试的IP checkbox
                        foreach (AntdUI.Checkbox checkSwitchItem in testSwitchGroup.Controls)
                        {
                            if (checkSwitchItem.Checked)
                            {
                                if (checkSwitchItem.Text == "开关1")
                                {
                                    testItemIndexs.Add(0);
                                }
                                if (checkSwitchItem.Text == "开关2")
                                {
                                    testItemIndexs.Add(1);
                                }
                                if (checkSwitchItem.Text == "开关3")
                                {
                                    testItemIndexs.Add(2);
                                }
                                if (checkSwitchItem.Text == "开关4")
                                {
                                    testItemIndexs.Add(3);
                                }
                                if (checkSwitchItem.Text == "开关5")
                                {
                                    testItemIndexs.Add(4);
                                }
                            }
                        }
                        await Task.Delay(100);
                        Dictionary<int, string> testIpsAndSwitchMappingDicts = new Dictionary<int, string>();
                        foreach (var item in testItemIndexs)
                        {
                            testIpsAndSwitchMappingDicts[item] = inputDicts[item].Text.Trim();
                        }
                        await Task.Delay(100);
                        foreach (var key in testIpsAndSwitchMappingDicts.Keys)
                        {
                            LogSaveOutput($"==========即将测试的IP有：{testIpsAndSwitchMappingDicts[key]}== 对应开关索引有：{key}========\n");
                        }
                        await Task.Delay(100);

                        LogSaveOutput($"即将开始测试，测试设备数量：{testIpsAndSwitchMappingDicts.Count}");
                        foreach (var switchIndex in testIpsAndSwitchMappingDicts.Keys)
                        {
                            string curTestIP_now = testIpsAndSwitchMappingDicts[switchIndex];
                            int switchIndex_now = switchIndex;

                            Task.Run(async () =>
                            {
                                string testFolder_item = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", item.Name, curTestIP_now.Replace(".", "_"));
                                LogSaveOutput($"测试文件夹：{testFolder_item}");

                                LogSaveOutput($"【{curTestIP_now}】 - 正在获取 Token...");
                                HttpApi_stu apiTestItem = new HttpApi_stu(curTestIP_now);
                                int curIndexSwitch;
                                try
                                {
                                    string token = await apiTestItem.LoginAsync(); // 假设 HttpApi_stu 已按之前建议优化
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取成功");
                                    }
                                    else
                                    {
                                        LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 登录异常: {ex.Message}");
                                }

                                string curNet = await apiTestItem.GetCurNetWorkConfig();
                                try
                                {
                                    JArray array = JArray.Parse(curNet);
                                    foreach (JObject item in array)
                                    {
                                        if (item["ipv4"] != null)
                                        {
                                            item["ipv4"]["dhcp"] = true;
                                        }
                                        if (item["ipv6"] != null)
                                        {
                                            item["ipv6"]["dhcp"] = true;
                                        }
                                    }
                                    curNet = array.ToString(Formatting.Indented);
                                    LogSaveOutput(curNet);
                                    LogSaveOutput(await apiTestItem.SetCurNetWorkConfig(curNet));
                                }
                                catch (Exception ex)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 设置自动获取IP异常 - JSON 处理出错: " + ex.Message);
                                }
                                int testCount = 1;
                                string testResult = "Fail";
                                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                                string ori_closeUpMain_pic, next_closeUpMain_pic = "";
                                while (true)
                                {
                                    LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试开始……");
                                    // 先下电
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, false, 0);
                                    await Task.Delay(2000);
                                    // 再上电
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);
                                    await controlRelay(switchIndex_now, true, 0);
                                    await Task.Delay(2000);

                                    int bootCountTimes = 0;
                                    bool pingSuccess = false;
                                    while (true)
                                    {
                                        bootCountTimes++;
                                        await Task.Delay(5000);
                                        WindowsFunc.IsHostReachableAsync(curTestIP_now).ContinueWith(reachabilityTask =>
                                        {
                                            if (reachabilityTask.Result)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备在线");
                                                pingSuccess = true;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - ping 进程检测 设备离线");
                                            }
                                        });
                                        if (pingSuccess)
                                        {
                                            apiTestItem = null; // 先释放之前的实例，确保资源清理
                                            apiTestItem = new HttpApi_stu(curTestIP_now);
                                            string token_now = await apiTestItem.LoginAsync();
                                            if (bootCountTimes >= 3000)
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 测试结束，当前第{item.TestCount}次上电重启超时，无法获取到token，请检查，测试停止！");
                                                item.TestResult = "FAIL";
                                                return;
                                            }
                                            if (!string.IsNullOrEmpty(token_now))
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - 第{item.TestCount}次上电重启完成，Token 获取成功,设备重启完成，即将开始测试！");
                                                break;
                                            }
                                            else
                                            {
                                                LogSaveOutput($"【{curTestIP_now}】 - Token 获取失败，重启中，请稍等……");
                                                continue;
                                            }
                                        }
                                    }

                                    // 开始拉流测试 -- 更新item的testcount和result
                                    LogSaveOutput($"【{curTestIP_now}】 - 拉流测试中……");
                                    await Task.Delay(5000);

                                    // 1. 先在外部定义变量，确保后面能访问到
                                    OpenCvRtspPlayer pm = null, ps = null, cm = null, cs = null, ai1 = null, ai2 = null, ai3 = null;
                                    PictureBox pb1 = null, pb2 = null, pb3 = null, pb4 = null, pb5 = null, pb6 = null, pb7 = null;
                                    bool isSuccess = false;
                                    void SafeDisposePlayer(OpenCvRtspPlayer player)
                                    {
                                        try { player?.Dispose(); } catch { }
                                    }

                                    void SafeDisposePictureBox(PictureBox pb)
                                    {
                                        if (pb == null) return;
                                        if (IsDisposed || Disposing)
                                        {
                                            try { pb.Dispose(); } catch { }
                                            return;
                                        }
                                        BeginInvoke(new Action(() =>
                                        {
                                            try
                                            {
                                                if (pb.Parent != null)
                                                {
                                                    pb.Parent.Controls.Remove(pb);
                                                }
                                                pb.Dispose();
                                            }
                                            catch { }
                                        }));
                                    }

                                    try
                                    {
                                        // 2. 使用 Invoke 强制回到主 UI 线程执行控件创建（解决卡死/报错的关键）
                                        this.Invoke(new Action(() =>
                                        {
                                            // 定义一个临时的本地函数，避免重复写 7 遍相同的代码
                                            PictureBox CreateHiddenPb()
                                            {
                                                var pb = new PictureBox()
                                                {
                                                    Size = new System.Drawing.Size(320, 180), // 保持合理大小以确保画质
                                                    Location = new System.Drawing.Point(-10000, -10000), // 移出屏幕
                                                    Visible = true, // 必须为 true，否则 OpenCvPlayer 逻辑会跳过解码
                                                    Parent = this   // 明确指定父容器
                                                };
                                                this.Controls.Add(pb); // 必须加入窗体集合
                                                return pb;
                                            }

                                            // 批量初始化
                                            pb1 = CreateHiddenPb(); pm = new OpenCvRtspPlayer(pb1);
                                            pb2 = CreateHiddenPb(); ps = new OpenCvRtspPlayer(pb2);
                                            pb3 = CreateHiddenPb(); cm = new OpenCvRtspPlayer(pb3);
                                            pb4 = CreateHiddenPb(); cs = new OpenCvRtspPlayer(pb4);
                                            pb5 = CreateHiddenPb(); ai1 = new OpenCvRtspPlayer(pb5);
                                            pb6 = CreateHiddenPb(); ai2 = new OpenCvRtspPlayer(pb6);
                                            pb7 = CreateHiddenPb(); ai3 = new OpenCvRtspPlayer(pb7);
                                        }));

                                        // 每一路拉流，并比对结果
                                        await rtspStreamOn(curTestIP_now, pm, "2", "全景主流");
                                        await rtspStreamOn(curTestIP_now, cm, "1", "特写主流");

                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(pm, testFolder_item, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);


                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(cm, testFolder_item, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);


                                        if (testCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前上下电重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                                        LogSaveOutput($"【{curTestIP_now}】 - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(pm);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(cm);
                                        LogSaveOutput($"【{curTestIP_now}】 - 当前特写主流状态测试结果：{closeUpMainStatusResult}");

                                        // 结果呈现，次数增加
                                        isSuccess = panoramicMainResult && closeUpMainResult
                                        && panoramicMainStatusResult && closeUpMainStatusResult;

                                        // 所有流关流

                                        rtspStreamOff(pm, "全景主流");
                                        rtspStreamOff(cm, "特写主流");
                                    }
                                    finally
                                    {
                                        SafeDisposePlayer(pm);
                                        SafeDisposePlayer(ps);
                                        SafeDisposePlayer(cm);
                                        SafeDisposePlayer(cs);
                                        SafeDisposePlayer(ai1);
                                        SafeDisposePlayer(ai2);
                                        SafeDisposePlayer(ai3);

                                        SafeDisposePictureBox(pb1);
                                        SafeDisposePictureBox(pb2);
                                        SafeDisposePictureBox(pb3);
                                        SafeDisposePictureBox(pb4);
                                        SafeDisposePictureBox(pb5);
                                        SafeDisposePictureBox(pb6);
                                        SafeDisposePictureBox(pb7);
                                    }



                                    LogSaveOutput($"【{curTestIP_now}】 - {item.Name} 第{testCount}次 结束，测试结果为：{item.TestResult}");
                                    if (stopTest)
                                    {
                                        LogSaveOutput("手动停止测试！");
                                        return;
                                    }

                                    if (isSuccess)
                                    {
                                        item.TestCount++;
                                        item.TestResult = "PASS";
                                        testCount++;
                                        testResult = "PASS";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束PASS");

                                        // 下电并等待10秒
                                        // 下电并等待10秒
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(500);
                                        await controlRelay(switchIndex_now, false, 0);
                                        await Task.Delay(10000);

                                    }
                                    else
                                    {
                                        item.TestResult = "FAIL";
                                        testResult = "FAIL";
                                        checkBoxDicts[switchIndex_now].Text = testResult + $"{testCount}次";
                                        LogSaveOutput($"【{curTestIP_now}】 - 第{testCount}次测试结束FAIL");
                                        return;
                                    }


                                }
                            });

                            await Task.Delay(3000);
                        }


                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase23_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前重启5000次压测测试项？",
                "点击确认后，当前测试会开始，设备将会先重启5000次，之后进行拉流测试，望悉知！",
                AntdUI.TType.Warn));

            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }
                // 获取token
                buttonGetToken_Click(null, null);
                await Task.Delay(1000);

                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                string ori_closeUpMain_pic, next_closeUpMain_pic = "";

                this.BeginInvoke(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            // 触发重启 5000次
                            // 3. 更新测试结果
                            item.TestCount++; // 次数+1

                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            // 重启流程
                            rebootDevBtn_Click(null, null);
                            await Task.Delay(5000);

                            bool rebootResult = false;
                            int rebootCount = 0;
                            LogSaveOutput($"第{item.TestCount}轮重启开始……");
                            while (true)
                            {
                                rebootCount++;
                                await Task.Delay(1000);
                                string token = await _api.LoginAsync();
                                if (rebootCount >= 3000)
                                {
                                    LogSaveOutput($"测试结束，当前第{item.TestCount}次重启超时，无法获取到token，请检查，测试停止！");
                                    item.TestResult = "FAIL";
                                    return;
                                }
                                if (stopTest)
                                {
                                    LogSaveOutput("手动停止测试！");
                                }
                                if (!string.IsNullOrEmpty(token))
                                {
                                    token_input.Text = token;
                                    LogSaveOutput($"第{item.TestCount}次重启完成，Token 获取成功,设备重启完成，即将开始下一次重启操作！");
                                    break;
                                }
                                else
                                {
                                    LogSaveOutput("Token 获取失败，重启中，请稍等……");
                                    continue;
                                }

                            }
                        }
                        catch (Exception ex)
                        {
                            LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                        }

                        if (item.TestCount >= 5000)
                        {
                            LogSaveOutput($"当前重启次数达到：{item.TestCount}, 即将开始拉流测试");
                            break;
                        }
                    }


                    await Task.Delay(5000);
                    // 进行拉流压测
                    // 每一路拉流，并比对结果
                    panoramicMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);

                    // 全景主流拉流测试出结果
                    string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                    LogSaveOutput(panoramicMain_pic);
                    await Task.Delay(100);

                    // 特写主流拉流测试出结果
                    string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                    LogSaveOutput(closeUpMain_pic);
                    await Task.Delay(100);

                    if (item.TestCount == 1)
                    {
                        ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                        ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                    }
                    else
                    {
                        ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                        ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                    }


                    bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                    bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                    LogSaveOutput($"重启5000次后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                    LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                    await Task.Delay(checkStreamStatusWaitingTime);
                    // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                    bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                    LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                    bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                    LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                    // 结果呈现，次数增加
                    bool isSuccess = panoramicMainResult && closeUpMainResult
                    && panoramicMainStatusResult && closeUpMainStatusResult;

                    // 所有流关流
                    panoramicMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);

                    LogSaveOutput($"{item.Name} 第{item.TestCount}次 重启后拉流结束，测试结果为：{item.TestResult}");
                    if (stopTest)
                    {
                        LogSaveOutput("手动停止测试！");
                    }

                    if (isSuccess)
                    {
                        item.TestCount++;
                        item.TestResult = "PASS";
                        LogSaveOutput($"【第{item.TestCount}次重启后拉流测试结束】");
                    }
                    else
                    {
                        item.TestResult = "FAIL";
                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase22_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "是否开始当前U盘升级拉流压测测试项？",
                "点击确认后，当前测试会开始，请务必保证已经把U盘插入，并且把固件放到U盘里面！望悉知！",
                AntdUI.TType.Warn));

            if (result == DialogResult.Yes || result == DialogResult.OK)
            {
                // 3. 更新测试结果
                item.TestCount++; // 次数+1

                string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
                LogSaveOutput($"测试文件夹：{testFolder}");
                if (Directory.Exists(testFolder))
                {
                    Directory.Delete(testFolder, true);
                }

                // 获取token
                buttonGetToken_Click(null, null);
                await Task.Delay(1000);


                string ori_panoramicMain_pic, next_panoramicMain_pic = "";
                string ori_closeUpMain_pic, next_closeUpMain_pic = "";

                this.BeginInvoke(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            // U盘升级拉流压测
                            string nextUpgradePath = await getSysVersion();
                            bool upgradeResult = false;

                            int update_checkVersionCount = 0;
                            // oU盘升级测试结果更新
                            if (await _api.RebootCurDevice() == "success")
                            {
                                LogSaveOutput("重启U盘升级中，请稍等！");
                                await Task.Delay(10000);
                                // U盘升级进度完成，等待重启完成
                                while (true)
                                {
                                    update_checkVersionCount += 1;
                                    // 获取token
                                    buttonGetToken_Click(null, null);
                                    await Task.Delay(1000);
                                    string upgradeDoneVersion = await _api.GetSysVerison();
                                    string diskStatus = await _api.GetDiskStatus();
                                    if (upgradeDoneVersion != null)
                                    {
                                        if (nextUpgradePath.Contains(upgradeDoneVersion) && diskStatus.Contains("SUCCESS"))
                                        {
                                            item.TestResult = "PASS";
                                            upgradeResult = true;
                                            LogSaveOutput($"设备【{_currentIp}】U盘升级完成，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");

                                            // 升级成功，即将开始拉流压测
                                            // 每一路拉流，并比对结果
                                            panoramicMainStreamOnBtn_Click(null, null);
                                            await Task.Delay(100);
                                            closeUpMainStreamOnBtn_Click(null, null);
                                            await Task.Delay(100);

                                            // 全景主流拉流测试出结果
                                            string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                            LogSaveOutput(panoramicMain_pic);
                                            await Task.Delay(1000);


                                            // 特写主流拉流测试出结果
                                            string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                            LogSaveOutput(closeUpMain_pic);
                                            await Task.Delay(1000);


                                            if (item.TestCount == 1)
                                            {
                                                ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            }
                                            else
                                            {
                                                ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                                ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                            }


                                            bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                            LogSaveOutput($"u盘升级后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                            bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                            LogSaveOutput($"u盘升级后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                                            LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                            await Task.Delay(checkStreamStatusWaitingTime);
                                            // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                            bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                            LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                            bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                            LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                                            // 结果呈现，次数增加
                                            bool isSuccess = panoramicMainResult && closeUpMainResult
                                            && panoramicMainStatusResult && closeUpMainStatusResult;

                                            // 所有流关流
                                            panoramicMainStreamOffBtn_Click(null, null);
                                            await Task.Delay(100);
                                            closeUpMainStreamOffBtn_Click(null, null);
                                            await Task.Delay(100);

                                            LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                                            if (stopTest)
                                            {
                                                LogSaveOutput("手动停止测试！");
                                                return;
                                            }

                                            if (isSuccess)
                                            {
                                                item.TestCount++;
                                                item.TestResult = "PASS";
                                                LogSaveOutput($"【第{item.TestCount}次U盘升级后拉流测试结束，下一次测试即将开始……】");
                                                break;
                                            }
                                            else
                                            {
                                                item.TestResult = "FAIL";
                                                return;
                                            }

                                        }
                                        else
                                        {
                                            LogSaveOutput($"设备【{_currentIp}】U盘升级失败，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");
                                            item.TestResult = "FAIL";
                                            upgradeResult = false;
                                            return;
                                        }
                                    }
                                    if (update_checkVersionCount >= 300)
                                    {
                                        item.TestResult = "FAIL";
                                        upgradeResult = false;
                                        LogSaveOutput($"长时间没有起来，当前设备 【{_currentIp}】 U盘升级失败，期望版本：【{nextUpgradePath}】");
                                        return;
                                    }
                                }
                            }
                            else
                            {
                                upgradeResult = false;
                                item.TestResult = "FAIL";
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                        }

                    }
                });
            }
            else
            {
                item.TestResult = "待测试";
                LogSaveOutput("未开始测试！");
            }
        }

        private async void TestCase21_2(TestCases item)
        {
            LogSaveOutput($"{_currentIp} - 测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"{_currentIp} - 测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            if (autoIPCheckBox.Checked)
            {
                // 设置dhcp 为true，自动获取ip
                getNetWorkConfigBtn_Click(null, null);
                await Task.Delay(100);
                setUdhcpcBtn_Click(null, null);
                await Task.Delay(100);
            }


            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 双版本循环分区OTA升级流程
                        string curSysVersion = await getSysVersion();
                        string ota1Path = input_otaPacketPath1.Text;
                        string ota2Path = input_otaPacketPath2.Text;
                        string nextUpgradePath = "";
                        bool upgradeResult = false;

                        LogSaveOutput($"{_currentIp} - 【OTA1：{ota1Path}】");
                        LogSaveOutput($"{_currentIp} - 【OTA2：{ota2Path}】");
                        if (ota1Path.Contains(curSysVersion))
                        {
                            LogSaveOutput($"{_currentIp} - Ready to : OTA2PATH : {ota2Path} -- {curSysVersion}");
                            nextUpgradePath = ota2Path;
                        }
                        else if (ota2Path.Contains(curSysVersion))
                        {
                            LogSaveOutput($"{_currentIp} - Ready to : OTA1PATH : {ota1Path} -- {curSysVersion}");
                            nextUpgradePath = ota1Path;
                        }
                        else
                        {
                            LogSaveOutput($"{_currentIp} - 停止测试 - 没有找到能够升级的版本，当前版本：{curSysVersion} 不在您所选择的2种版本中");
                            item.TestResult = "FAIL";
                            break;
                        }

                        if (nextUpgradePath != "")
                        {
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            LogSaveOutput($"{_currentIp} - 当前版本：{curSysVersion}，即将升级的版本：{nextUpgradePath}");
                            // 上传ota包
                            if (await _api.UploadFirmwareAsync_SKDL_new(nextUpgradePath) == "success")
                            {
                                // 触发升级
                                if (await _api.StartUpdate() == "success")
                                {
                                    int update_checkCount = 0;
                                    // 检测升级版本和设备升级状态
                                    while (true)
                                    {
                                        // 获取token
                                        buttonGetToken_Click(null, null);
                                        await Task.Delay(1000);
                                        update_checkCount += 1;
                                        string progress = await _api.CheckUpgradeStaus("progress");
                                        string status = await _api.CheckUpgradeStaus("status");
                                        LogSaveOutput($"{_currentIp} - 当前升级进度【{progress}】 -- 升级状态 【{status}】");
                                        if (progress == "99" && status == "update" || progress == "99" && status == "fail" || progress == "0" && status == "not start")
                                        {
                                            LogSaveOutput($"{_currentIp} - 升级流程结束，等待设备启动完成！");
                                            upgradeResult = true;
                                            break;
                                        }
                                        if (progress == "99" && status == "update")
                                        {
                                            LogSaveOutput($"{_currentIp} - 升级流程结束，等待60秒设备启动完成！");
                                            await Task.Delay(60000);
                                            upgradeResult = true;
                                            break;
                                        }
                                        if (update_checkCount >= 60)
                                        {
                                            upgradeResult = false;
                                            item.TestResult = "FAIL";
                                            LogSaveOutput($"{_currentIp} - 升级流程超时！");
                                            break;
                                        }
                                        await Task.Delay(3000);
                                    }
                                }
                                else
                                {
                                    LogSaveOutput($"{_currentIp} - 触发升级失败，请检查设备状态！");
                                    item.TestResult = "FAIL";
                                    upgradeResult = false;
                                    break;
                                }
                            }
                            else
                            {
                                LogSaveOutput($"{_currentIp} - ota包上传失败，请检查！\n{nextUpgradePath}");
                                item.TestResult = "FAIL";
                                upgradeResult = false;
                                break;
                            }
                        }

                        int update_checkVersionCount = 0;
                        // ota升级测试结果更新
                        if (upgradeResult)
                        {
                            // ota升级进度完成，等待重启完成
                            LogSaveOutput($"{_currentIp} - ota升级进度完成，等待重启后进行拉流检测……");
                            while (true)
                            {
                                update_checkVersionCount += 1;
                                await Task.Delay(3000);
                                // 获取token
                                buttonGetToken_Click(null, null);
                                await Task.Delay(1000);
                                string upgradeDoneVersion = await _api.GetSysVerison();
                                string diskStatus = await _api.GetDiskStatus();
                                if (upgradeDoneVersion != null)
                                {
                                    if (nextUpgradePath.Contains(upgradeDoneVersion) && diskStatus.Contains("SUCCESS"))
                                    {
                                        item.TestResult = "PASS";
                                        upgradeResult = true;
                                        LogSaveOutput($"设备【{_currentIp}】升级完成，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");

                                        // 升级成功，即将开始拉流压测
                                        // 每一路拉流，并比对结果
                                        panoramicMainStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpMainStreamOnBtn_Click(null, null);
                                        await Task.Delay(100);

                                        // 全景主流拉流测试出结果
                                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                                        LogSaveOutput(panoramicMain_pic);
                                        await Task.Delay(100);

                                        // 特写主流拉流测试出结果
                                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                                        LogSaveOutput(closeUpMain_pic);
                                        await Task.Delay(100);

                                        if (item.TestCount == 1)
                                        {
                                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }
                                        else
                                        {
                                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                                        }


                                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                                        LogSaveOutput($"{_currentIp} - ota双版本互刷升级后拉流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                                        LogSaveOutput($"{_currentIp} - 等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                                        await Task.Delay(checkStreamStatusWaitingTime);
                                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                                        // 结果呈现，次数增加
                                        bool isSuccess = panoramicMainResult && closeUpMainResult
                                        && panoramicMainStatusResult && closeUpMainStatusResult;

                                        // 所有流关流
                                        panoramicMainStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);
                                        closeUpMainStreamOffBtn_Click(null, null);
                                        await Task.Delay(100);

                                        LogSaveOutput($"{_currentIp} - {item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                                        if (stopTest)
                                        {
                                            LogSaveOutput($"{_currentIp} - 手动停止测试！");
                                            return;
                                        }

                                        if (isSuccess)
                                        {
                                            item.TestCount++;
                                            item.TestResult = "PASS";
                                            LogSaveOutput($"{_currentIp} - 【第{item.TestCount}次测试结束，下一次测试即将开始……】");
                                            break;
                                        }
                                        else
                                        {
                                            item.TestResult = "FAIL";
                                            return;
                                        }

                                    }
                                    else
                                    {
                                        LogSaveOutput($"设备【{_currentIp}】升级失败，当前版本：{upgradeDoneVersion}, 期望版本：{nextUpgradePath}");
                                        item.TestResult = "FAIL";
                                        upgradeResult = false;
                                        return;
                                    }
                                }
                                if (update_checkVersionCount >= 30)
                                {
                                    item.TestResult = "FAIL";
                                    upgradeResult = false;
                                    LogSaveOutput($"长时间没有起来，当前设备 【{_currentIp}】 OTA升级失败，期望版本：【{nextUpgradePath}】");
                                    return;
                                }
                            }
                        }
                        else
                        {
                            upgradeResult = false;
                            item.TestResult = "FAIL";
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"{_currentIp} - case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }
            });
        }

        private async void TestCase19_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            if (autoIPCheckBox.Checked)
            {
                // 设置dhcp 为true，自动获取ip
                getNetWorkConfigBtn_Click(null, null);
                await Task.Delay(100);
                setUdhcpcBtn_Click(null, null);
                await Task.Delay(100);
            }


            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 重启设备
                        rebootDevBtn_Click(null, null);
                        LogSaveOutput("设备重启中请稍等150秒……");
                        await Task.Delay(150000);

                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前重启设备，全视频流压测 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }
        private async void TestCase18_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "全功能老化测试开始提醒！",
                $"点击后开始全功能老化测试计时，勾选上面需要测试的内容，即可自动开始老化测试，取消勾选该测试即自动停止！\n" +
                $"点击关闭该弹窗后测试将开始计时！", AntdUI.TType.Info));

            var start = DateTime.Now; // 1. 记录开始时间

            // 2. 创建并启动定时器 (1秒刷新一次)
            new System.Windows.Forms.Timer { Interval = 1000, Enabled = true }.Tick += (s, e) =>
            {
                // 3. 核心代码：计算差值并转为 String
                string timeStr = (DateTime.Now - start).ToString(@"hh\:mm\:ss");

                // 显示出来 (例如赋值给 Label 或 窗体标题)
                item.TestResult = timeStr;
            };

            checkbox_2streamRTSPOn.Checked = true;
            await Task.Delay(10000);
            checkbox_zoomCircleTest.Checked = true;
            await Task.Delay(1000);
            checkbox_eptzCircleTest.Checked = true;
            await Task.Delay(1000);
            checkbox_ptzCircleTest.Checked = true;

            LogSaveOutput("全功能测试开始！");

        }


        private async void TestCase17_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_case", "case1_gbs_Channel1andChannel2LogicOnOffStream2.py");
            DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "Python脚本测试开始提醒！",
                $"点击开始后，尽量请勿操作电脑！详细测试结果和Log请前往文件夹{AppDomain.CurrentDomain.BaseDirectory}python_case查看结果即可！\n" +
                $"点击关闭该弹窗后测试将开始！", AntdUI.TType.Info));
            await WindowsFunc.executeCMDCommand_RealTime($"python -u {scriptPath}", (line) =>
            {
                // 因为这是在后台线程回调的，如果是更新UI，需要 Invoke
                this.Invoke((Action)(() =>
                {
                    LogSaveOutput(line);
                    if (line.Contains("通道逻辑测试结果为：True"))
                    {
                        item.TestCount++;
                        item.TestResult = "PASS";
                    }
                    else if (line.Contains("通道逻辑测试结果为：False"))
                    {
                        item.TestResult = "FAIL";
                    }
                }));
            });
        }


        private async void TestCase14_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_uvc_pic, next_uvc_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置到uvc出特写模式
            setUvcCloseUpBtn_Click(null, null);
            await Task.Delay(100);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前uvc支持的分辨率
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");

            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;
                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 10 秒
                                await Task.Delay(10000);
                                LogSaveOutput($"预览教师全景[{uvc_x}x{uvc_y} {format}] - 10秒");

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师全景[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }

        private async void TestCase13_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_uvc_pic, next_uvc_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 设置到uvc出特写模式
            setUvcCloseUpBtn_Click(null, null);
            await Task.Delay(100);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 获取当前uvc支持的分辨率
            List<string> uvcSupportResolutionList = getUVCCameraSupportResolution("Seewo Lubo");

            // 定义要测试的编码格式
            string[] formats = { "MJPG", "H264", "NV12" };

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var uvcResolution in uvcSupportResolutionList)
                        {
                            string uvc_x = uvcResolution.Split("x")[0];
                            string uvc_y = uvcResolution.Split("x")[1];

                            input1_uvc_x.Text = uvc_x;
                            input2_uvc_y.Text = uvc_y;

                            foreach (string format in formats)
                            {
                                // 更新 UI 显示的格式
                                input_Uvctype.Text = format;


                                // 启动 UVC 流
                                bool startOk;
                                if (GetCameras("Seewo Lubo").Count > 1)
                                {
                                    // 多设备情况下，使用当前选中的设备路径（如果有）
                                    string devicePath = input_curUvcDevicePath.Text;
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format, devicePath);
                                }
                                else
                                {
                                    startOk = await StartUVC(int.Parse(uvc_x), int.Parse(uvc_y), format);
                                }

                                if (!startOk)
                                {
                                    LogSaveOutput($"启动失败，跳过格式 {format} 分辨率 {uvcResolution}");
                                    LogFailType(uvcResolution, format); // 记录失败信息
                                    continue; // 跳过当前格式，继续下一个
                                }

                                // 预览 12 秒
                                await Task.Delay(10000);
                                LogSaveOutput($"预览教师全景[{uvc_x}x{uvc_y} {format}] - 10秒");

                                // 截图
                                string uvc_pic = await uvcTaskSnapShot("Seewo Lubo", item.Name, $"教师全景[{uvc_x}x{uvc_y} {format}]");
                                LogSaveOutput(uvc_pic);
                                await Task.Delay(100);

                                // 判断图片有效性（自对比，只要图片正常即可）
                                bool picValid = checkPICValid(uvc_pic, uvc_pic);
                                LogSaveOutput($"分辨率 {uvcResolution} 格式 {format} 测试结果：{(picValid ? "PASS" : "FAIL")}");

                                // 关闭流
                                uvc_streamOffBtn_Click(null, null);
                                await Task.Delay(2000); // 等待关闭

                                if (!picValid)
                                {
                                    item.TestResult = "FAIL";
                                    stopTest = true;
                                    break; // 跳出格式循环
                                }
                            }

                            if (stopTest) break;
                        }

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试或测试失败，结束测试");
                            break;
                        }
                        else
                        {
                            // 所有分辨率和格式都通过，继续下一轮
                            item.TestCount++;
                            item.TestResult = "PASS";
                            LogSaveOutput($"第{item.TestCount}轮测试完成，继续下一轮...");
                            await Task.Delay(circleTestDelayTime * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }
                }
            });
        }

        private async void TestCase12_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            string ori_panoramic_RTMP_Main_pic, next_panoramic_RTMP_Main_pic = "";
            string ori_closeUp_RTMP_Main_pic, next_closeUp_RTMP_Main_pic = "";

            // 设置clumsy限速5%
            input1_clumsyLimit.Text = "5";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 打开clumsy限速5%
                        clumsyLimitSpeedBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{_currentIp} - 等待5秒后，进行测试……");

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicMainRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainRtmpStreanOnBtn_Click(null, null);
                        await Task.Delay(100);


                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        // 全景主流RTMP拉流测试出结果
                        string panoramicRTMPMain_pic = await SafeSnapshotAsync(player_rtmp_panoramicMain, testFolder, "全景RTMP主流");
                        LogSaveOutput(panoramicRTMPMain_pic);
                        await Task.Delay(100);

                        // 特写主流RTMP拉流测试出结果
                        string closeUpRTMPMain_pic = await SafeSnapshotAsync(player_rtmp_closeUpMain, testFolder, "特写RTMP主流");
                        LogSaveOutput(closeUpRTMPMain_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic; next_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic;
                            ori_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic; next_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                            ori_panoramic_RTMP_Main_pic = next_panoramic_RTMP_Main_pic; next_panoramic_RTMP_Main_pic = panoramicRTMPMain_pic;
                            ori_closeUp_RTMP_Main_pic = next_closeUp_RTMP_Main_pic; next_closeUp_RTMP_Main_pic = closeUpRTMPMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");
                        bool panoramicRTMPMainResult = checkPICValid(ori_panoramic_RTMP_Main_pic, next_panoramic_RTMP_Main_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景RTMP主流测试结果：{panoramicRTMPMainResult} -- {ori_panoramic_RTMP_Main_pic} : {next_panoramic_RTMP_Main_pic}");
                        bool closeUpRTMPMainResult = checkPICValid(ori_closeUp_RTMP_Main_pic, next_closeUp_RTMP_Main_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写RTMP主流测试结果：{closeUpRTMPMainResult} -- {ori_closeUp_RTMP_Main_pic} : {next_closeUp_RTMP_Main_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        panoramicRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainRtmpStreanOffBtn_Click(null, null);
                        await Task.Delay(100);

                        clumsyStopLimitSpeedBtn_Click(null, null);
                        await Task.Delay(3000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }
        private async void TestCase11_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfig2();
            await Task.Delay(3000);

            // 设置主流到1080P - 25fps， 辅流到576P - 25fps
            LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1024x576\""));
            LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 25,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1920x1080\""));

            LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
            LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到1080P - 25fps， 辅流到576P - 25fps -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");

                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase10_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiResModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfig2();
            await Task.Delay(10000);

            // 设置主流到4K - 30fps， 辅流到720P - 30fps
            LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"1280x720\""));

            LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": 30,")
                .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"3840x2160\""));


            LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicMain_stream_config));
            LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("main", cur_closeUpMain_stream_config));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高分辨率模式设置 -- 设置主流到4K - 30fps， 辅流到720P - 30fps -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }
        private async void TestCase9_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfig2();
            await Task.Delay(3000);

            // 设置辅流的fps到60
            LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));
            LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_closeUpSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));



            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高帧率模式设置 -- 切辅流均设置60FPS后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 切辅流均设置60FPS后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");



                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }
        private async void TestCase8_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            hiFpsModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前高帧率模式设置后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase7_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_extreme_pic, next_extreme_pic = "";
            string ori_extreme2_pic, next_extreme2_pic = "";

            // 获取token
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            // 切到对应测试模式
            extremeModeBtn_Click(null, null);
            LogSaveOutput("请稍等，模式切换完成，大概50秒，等待50秒切换完成！");
            await Task.Delay(50000);

            // 先读取当前配置
            readAllStreamCurConfig2();
            await Task.Delay(3000);

            // 设置辅流的fps到60
            LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_panoramicSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));
            LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("sub", cur_closeUpSub_stream_config.Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": 60,")));



            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        extreme1StreamOn2();
                        await Task.Delay(5000);
                        extreme2StreamOn2();
                        await Task.Delay(5000);

                        // 性能模式流1测试出结果
                        string extreme_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "性能模式流1");
                        LogSaveOutput(extreme_pic);
                        await Task.Delay(100);

                        // 性能模式流2测试出结果
                        string extreme2_pic = await SafeSnapshotAsync(player_panoramicSub, testFolder, "性能模式流2");
                        LogSaveOutput(extreme2_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_extreme_pic = extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }
                        else
                        {
                            ori_extreme_pic = next_extreme_pic; next_extreme_pic = extreme_pic;
                            ori_extreme2_pic = next_extreme2_pic; next_extreme2_pic = extreme2_pic;
                        }


                        bool extremeResult = checkPICValid(ori_extreme_pic, next_extreme_pic);
                        LogSaveOutput($"当前性能模式后 -- 性能模式流 - 1测试结果：{extremeResult} -- {ori_extreme_pic} : {next_extreme_pic}");
                        bool extreme2Result = checkPICValid(ori_extreme2_pic, next_extreme2_pic);
                        LogSaveOutput($"当前性能模式后 -- 性能模式流 - 2测试结果：{extreme2Result} -- {ori_extreme2_pic} : {next_extreme2_pic}");


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool extremeStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前性能模式流1状态测试结果：{extremeStatusResult}");
                        bool extreme2StatusResult = getStreamStatusResult(player_panoramicSub);
                        LogSaveOutput($"当前性能模式流2状态测试结果：{extreme2StatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = extremeResult && extreme2Result
                        && extremeStatusResult && extreme2StatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        extreme1StreamOff2();
                        await Task.Delay(100);
                        extreme2StreamOff2();
                        await Task.Delay(5000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }
        private async void TestCase6_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为2MB -- 2048
                        input1_allStreamBitrate.Text = "2048";
                        readAllStreamCurConfig2();
                        await Task.Delay(3000);
                        changeAllStreamBitrate2();
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前1MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase5_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为12MB -- 12288
                        input1_allStreamBitrate.Text = "12288";
                        readAllStreamCurConfig2();
                        await Task.Delay(3000);
                        changeAllStreamBitrate2();
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前16MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase4_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 修改码率为64MB -- 65536
                        input1_allStreamBitrate.Text = "65536";
                        readAllStreamCurConfig2();
                        await Task.Delay(100);
                        changeAllStreamBitrate2();
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);


                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前64MB码率设置后 -- {input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        private async void TestCase3_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 打开clumsy限速8%
                        clumsyLimitSpeedBtn_Click(null, null);
                        await Task.Delay(5000);
                        LogSaveOutput($"{_currentIp} - 等待5秒后，进行测试……");

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前Clumsy限速{input1_clumsyLimit.Text}%后 -- 特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        //bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        //LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        //bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        //LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        //bool isSuccess = panoramicMainResult && closeUpMainResult
                        //&& panoramicMainStatusResult && closeUpMainStatusResult;

                        bool isSuccess = panoramicMainResult && closeUpMainResult;


                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);

                        clumsyStopLimitSpeedBtn_Click(null, null);
                        await Task.Delay(3000);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }

        private async void TestCase2_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");
            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Dictionary<string, OpenCvRtspPlayer> openCvRtspPlayersDict = new Dictionary<string, OpenCvRtspPlayer>();
            openCvRtspPlayersDict.Add("全景主流", player_panoramicMain);
            openCvRtspPlayersDict.Add("特写主流", player_CloseUpMain);

            string ori_pic, next_pic = "";
            float cur_fps, cur_allBitrate, cur_CpuUsage = 0;

            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 随机取出一路拉流
                        string curStreamName = chooseAStreamByQueue("random", openCvRtspPlayersDict);
                        OpenCvRtspPlayer curPlayer = openCvRtspPlayersDict[curStreamName];
                        string cur_url = StreamUrlBack(curStreamName);

                        // 随机取出一路流，拉流，等待10分钟
                        curPlayer.Start(cur_url, checkBoxDecodeTest.Checked);
                        LogSaveOutput($"{curStreamName} 开始拉流 - {cur_url}");
                        await Task.Delay(10000);

                        // 检查其对应帧率，码率，画面显示，cpu占用
                        bool picCheckResult = false, fpsCheckResult = false, bitRateCheckResult = false, cpuUsageCheckResult = false;
                        // 1、pic check
                        string cur_pic = await SafeSnapshotAsync(curPlayer, testFolder, curStreamName);
                        LogSaveOutput(cur_pic);
                        await Task.Delay(100);

                        if (item.TestCount == 1)
                        {
                            ori_pic = cur_pic; next_pic = cur_pic;
                        }
                        else
                        {
                            ori_pic = next_pic; next_pic = cur_pic;
                        }
                        picCheckResult = checkPICValid(ori_pic, next_pic);
                        LogSaveOutput($"当前{curStreamName}图像画面显示测试结果：{picCheckResult} -- {ori_pic} : {next_pic}");
                        await Task.Delay(100);

                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        // 2、 fps、bitrate、cpu check
                        var stats = curPlayer.GetPlayerStatus();
                        cur_fps = stats.Fps;
                        cur_allBitrate = stats.TotalBitrateKbps;
                        cur_CpuUsage = stats.CpuUsage;

                        fpsCheckResult = cur_fps > 0 ? true : false;
                        bitRateCheckResult = cur_allBitrate > 0 ? true : false;
                        cpuUsageCheckResult = cur_CpuUsage > 0 ? true : false;

                        LogSaveOutput($"当前{curStreamName}帧率、码率、cpu占用情况：fps: {stats.Fps:F1} -- cpu: {stats.CpuUsage:F1} -- bitrate: {stats.TotalBitrateKbps / 1024:F2} Mbps，结果为：{fpsCheckResult},{bitRateCheckResult}, {cpuUsageCheckResult}");
                        await Task.Delay(100);

                        // 没问题就关流
                        if (picCheckResult && fpsCheckResult && bitRateCheckResult && cpuUsageCheckResult)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }
                        // 循环下一次拉流
                        curPlayer.Stop();
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });

        }

        private async void TestCase1_2(TestCases item)
        {
            LogSaveOutput($"测试用例：【{item.Name}】运行中");

            // 3. 更新测试结果
            item.TestCount++; // 次数+1

            string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), item.Name);
            LogSaveOutput($"测试文件夹：{testFolder}");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            string ori_panoramicMain_pic, next_panoramicMain_pic = "";
            string ori_closeUpMain_pic, next_closeUpMain_pic = "";
            this.BeginInvoke(async () =>
            {
                while (true)
                {
                    try
                    {// 获取token
                        buttonGetToken_Click(null, null);
                        await Task.Delay(1000);

                        // 获取所有支持的分辨率情况
                        getAllResolution2();
                        await Task.Delay(1000);

                        // 切换每一路分辨率
                        readAllStreamCurConfig2();
                        await Task.Delay(1000);
                        changeResolutionOrder2();
                        await Task.Delay(1000);
                        changeAllStreamCurConfig2();
                        await Task.Delay(1000);

                        // 每一路拉流，并比对结果
                        panoramicMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOnBtn_Click(null, null);
                        await Task.Delay(100);

                        // 全景主流拉流测试出结果
                        string panoramicMain_pic = await SafeSnapshotAsync(player_panoramicMain, testFolder, "全景主流");
                        LogSaveOutput(panoramicMain_pic);
                        await Task.Delay(100);

                        // 特写主流拉流测试出结果
                        string closeUpMain_pic = await SafeSnapshotAsync(player_CloseUpMain, testFolder, "特写主流");
                        LogSaveOutput(closeUpMain_pic);
                        await Task.Delay(100);


                        if (item.TestCount == 1)
                        {
                            ori_panoramicMain_pic = panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }
                        else
                        {
                            ori_panoramicMain_pic = next_panoramicMain_pic; next_panoramicMain_pic = panoramicMain_pic;
                            ori_closeUpMain_pic = next_closeUpMain_pic; next_closeUpMain_pic = closeUpMain_pic;
                        }


                        LogSaveOutput($"等待{checkStreamStatusWaitingTime / 1000}秒，检查所有拉流状态……");
                        await Task.Delay(checkStreamStatusWaitingTime);
                        bool panoramicMainResult = checkPICValid(ori_panoramicMain_pic, next_panoramicMain_pic);
                        LogSaveOutput($"当前全景主流测试结果：{panoramicMainResult} -- {ori_panoramicMain_pic} : {next_panoramicMain_pic}");
                        bool closeUpMainResult = checkPICValid(ori_closeUpMain_pic, next_closeUpMain_pic);
                        LogSaveOutput($"当前特写主流测试结果：{closeUpMainResult} -- {ori_closeUpMain_pic} : {next_closeUpMain_pic}");

                        // 根据每个拉流的player获取对应fps、bitrate、cpuusage并判断结果
                        bool panoramicMainStatusResult = getStreamStatusResult(player_panoramicMain);
                        LogSaveOutput($"当前全景主流状态测试结果：{panoramicMainStatusResult}");
                        bool closeUpMainStatusResult = getStreamStatusResult(player_CloseUpMain);
                        LogSaveOutput($"当前特写主流状态测试结果：{closeUpMainStatusResult}");

                        // 结果呈现，次数增加
                        bool isSuccess = panoramicMainResult && closeUpMainResult
                        && panoramicMainStatusResult && closeUpMainStatusResult;

                        if (isSuccess)
                        {
                            item.TestCount++;
                            item.TestResult = "PASS";
                        }
                        else
                        {
                            item.TestResult = "FAIL";
                            break;
                        }

                        // 所有流关流
                        panoramicMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        closeUpMainStreamOffBtn_Click(null, null);
                        await Task.Delay(100);
                        LogSaveOutput($"{item.Name} 第{item.TestCount}次 结束，测试结果为：{item.TestResult}");
                        if (stopTest)
                        {
                            LogSaveOutput("手动停止测试！");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSaveOutput($"case本次测试存在部分异常，跳过并开始下一次测试！\n{ex.ToString()}");
                    }

                }

            });
        }


        /**
         * SKDL0104(VC35)&SKDL0105(VC51)项目case
         */
        private BindingList<TestCases> testCases2 = new BindingList<TestCases>();

        private void initTestCaseTable2()
        {
            table2_testCase.Columns.Clear();

            table2_testCase.Columns.Add(new AntdUI.Column("Name", "测试用例 -- 带自动IP"));
            table2_testCase.Columns.Add(new AntdUI.Column("Description", "用例描述"));
            table2_testCase.Columns.Add(new AntdUI.Column("TestCount", "测试次数"));

            var colResult = new AntdUI.Column("TestResult", "测试结果");
            table2_testCase.Columns.Add(colResult);

            var colAction = new AntdUI.Column("BtnText", "操作");
            colAction.Align = AntdUI.ColumnAlign.Center;
            table2_testCase.Columns.Add(colAction);

            TestCases t1 = new TestCases();
            t1.Name = "case1_分辨率轮询压测";
            t1.Description = "高分辨率模式：\r\n1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”主码流、辅码流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，切换完所有分辨率为1轮；\r\n主码流 rtsp://[ip]/1 \r\n辅码流 rtsp://[ip]/2 ";
            t1.TestCount = 0;
            t1.TestResult = "待测试";
            testCases2.Add(t1);

            TestCases t2 = new TestCases();
            t2.Name = "case2_随机拉流切换压测";
            t2.Description = "高分辨率模式：\r\n1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”主码流、辅码流“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t2.TestCount = 0;
            t2.TestResult = "待测试";
            testCases2.Add(t2);

            TestCases t3 = new TestCases();
            t3.Name = "case3_clumsy拉流限速压测";
            t3.Description = "1、控制clumsy.exe进行限速\r\n2、ping控制丢包率在5%~10%\r\n3、控制clumsy.exe解除限速\r\n4、RTSP 同时拉”主码流、辅码流“\r\n5、循环测试500次";
            t3.TestCount = 0;
            t3.TestResult = "待测试";
            testCases2.Add(t3);

            TestCases t4 = new TestCases();
            t4.Name = "case4_64M码率循环拉流压测";
            t4.Description = "1、所有码率设置64M -- 65536\r\n2、RTSP拉流：”主码流、辅码流“\r\n3、关流、开流循环500次";
            t4.TestCount = 0;
            t4.TestResult = "待测试";
            testCases2.Add(t4);

            TestCases t5 = new TestCases();
            t5.Name = "case5_12M码率循环拉流压测";
            t5.Description = "1、所有码率设置12M -- 12288\r\n2、RTSP拉流：”主码流、辅码流“\r\n3、关流、开流循环500次";
            t5.TestCount = 0;
            t5.TestResult = "待测试";
            testCases2.Add(t5);

            TestCases t6 = new TestCases();
            t6.Name = "case6_2M码率循环拉流压测";
            t6.Description = "1、所有码率设置2M -- 2048\r\n2、RTSP拉流：”主码流、辅码流“\r\n3、关流、开流循环500次";
            t6.TestCount = 0;
            t6.TestResult = "待测试";
            testCases2.Add(t6);

            TestCases t7 = new TestCases();
            t7.Name = "case7_extreme性能模式拉流压测";
            t7.Description = "1、设置为性能模式（ 分辨率会默认到4K，帧率60帧，辅码流 576P 60帧）\r\n2、RTSP拉流：主码流、辅码流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/1\r\nrtsp://[IP]/2";
            t7.TestCount = 0;
            t7.TestResult = "待测试";
            testCases2.Add(t7);

            TestCases t8 = new TestCases();
            t8.Name = "case8_highFPS高帧率模式拉流压测";
            t8.Description = "1、设置为高帧模式 （自动会默认分辨率主流到1080P60帧 + 辅流576P 25帧 ）\r\n2、RTSP拉流：主码流、辅码流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/1\r\nrtsp://[IP]/2";
            t8.TestCount = 0;
            t8.TestResult = "待测试";
            testCases2.Add(t8);

            TestCases t9 = new TestCases();
            t9.Name = "case9_highFPS高帧率模式拉流压测";
            t9.Description = "1、设置为高帧模式 （自动会默认分辨率主流到1080P60帧 + 切换 帧率辅流 720P 60帧(60fps需要手动切换) ）\r\n2、RTSP拉流：主码流、辅码流\r\n3、关流、开流循环500次\r\nrtsp://[IP]/1\r\nrtsp://[IP]/2";
            t9.TestCount = 0;
            t9.TestResult = "待测试";
            testCases2.Add(t9);

            TestCases t10 = new TestCases();
            t10.Name = "case10_all30FPS_main4K_sub720P拉流压测";
            t10.Description = "1、所有帧率设置30fps （设置为高分辨率模式，分辨率为4K（主流设））+ 720P（辅流设）））\r\n2、RTSP拉流：”主码流、辅码流“\r\n3、关流、开流循环500次";
            t10.TestCount = 0;
            t10.TestResult = "待测试";
            testCases2.Add(t10);

            TestCases t11 = new TestCases();
            t11.Name = "case11_all25FPS_main1080P_sub576P拉流压测";
            t11.Description = "1、所有帧率设置25fps（设置为高分辨率模式，分辨率为1080P（主流设） + 576P（辅流设））\r\n2、RTSP拉流：”主码流、辅码流“\r\n3、关流、开流循环500次";
            t11.TestCount = 0;
            t11.TestResult = "待测试";
            testCases2.Add(t11);

            TestCases t12 = new TestCases();
            t12.Name = "case12_clumsy拉流包括RTMP全流限速压测";
            t12.Description = "【在工具上部分rtmp1填入辅流rtmp地址，在rtmp3部分填入主流rtmp地址】\r\n1、控制clumsy.exe进行限速\r\n2、ping控制丢包率在5%\r\n3、控制clumsy.exe解除限速\r\n4、RTSP同时拉：”主码流、辅码流“\r\nRTMP同时拉：”主码流、辅码流“\r\n5、循环测试500次";
            t12.TestCount = 0;
            t12.TestResult = "待测试";
            testCases2.Add(t12);


            TestCases t13 = new TestCases();
            t13.Name = "case13_uvc全景高分辨率模式切换分辨率压测";
            t13.Description = "1、设置UVC特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t13.TestCount = 0;
            t13.TestResult = "待测试";
            testCases2.Add(t13);

            TestCases t14 = new TestCases();
            t14.Name = "case14_uvc全景高帧率模式切换分辨率压测";
            t14.Description = "1、设置UVC特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            t14.TestCount = 0;
            t14.TestResult = "待测试";
            testCases2.Add(t14);

            //TestCases t15 = new TestCases();
            //t15.Name = "case15_uvc特写高分辨率模式切换分辨率压测";
            //t15.Description = "1、设置UVC教师特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            //t15.TestCount = 0;
            //t15.TestResult = "待测试";
            //testCases2.Add(t15);

            //TestCases t16 = new TestCases();
            //t16.Name = "case16_uvc特写高帧率模式切换分辨率压测";
            //t16.Description = "1、设置UVC教师特写流\r\n2、UVC potplayer 切换分辨率 ，轮巡遍历切换分辨率测试 ，每次切换后预览10S\r\n3、测试1000次";
            //t16.TestCount = 0;
            //t16.TestResult = "待测试";
            //testCases2.Add(t16);

            TestCases t17 = new TestCases();
            t17.Name = "case17_gbs通道1和2拉流压测-python";
            t17.Description = "" +
                "【测试前修改对应文件夹内python脚本的 gbs_url1和 urlList后再开始测试】\n" +
                "注意事项如下：\n" +
                "1、使用浏览器打开GBS平台\r\n2、打开通道一拉流、打开通道二拉流\r\n3、关闭通道一拉流\r\n4、打开通道一拉流\r\n5、关闭通道二拉流\r\n6、打开通道二拉流\r\n\r\n备注：通道（channel0、channel1）";
            t17.TestCount = 0;
            t17.TestResult = "待测试";
            testCases2.Add(t17);

            TestCases t18 = new TestCases();
            t18.Name = "case18_全功能持续老化测试";
            t18.Description = "【点击后开始全功能老化测试计时，勾选上面需要测试的内容，即可自动开始老化测试，取消勾选该测试即自动停止！】" +
                "\n工具端实现：（勾选对应测试项，确认测试内容）\r\n1、全部2路流RTSP拉出来 -- 流全开\r\n2、云台不断转动 --- 云台不断来回转动\r\n3、光变变焦不断运行 -- 光变设置最大然后从近到远来回切\r\n4、HDMI OUT接出来 -- 连接HDMI OUT到其他显示器";
            t18.TestCount = 0;
            t18.TestResult = "待测试";
            testCases2.Add(t18);

            TestCases t19 = new TestCases();
            t19.Name = "case19_重启全视频流压测";
            t19.Description = "1、软件重启设备\r\n2、重启设备查看HDMI主动出流\r\n3、重启设备后RTSP 同时拉流：主码流、辅码流（默认视频配置）\r\n5、检查帧率、码率、画面显示正常\r\n6、重启压测1000次";
            t19.TestCount = 0;
            t19.TestResult = "待测试";
            testCases2.Add(t19);


            //TestCases t20 = new TestCases();
            //t20.Name = "case20_红外控制休眠唤醒拉流压测";
            //t20.Description = "case20_红外控制休眠唤醒拉流压测 -- 待OK调试脚本\r\n\r\n需要教授找电机模拟按压遥控器的开关机键,\r\n然后板端需要给红外模块供电，后压测\r\n\r\nKDL0105的休眠和唤醒刚刚和炜豪确认，\r\n休眠唤醒红外控制，要后面硬件改版后，\r\n给红外模块独立供电后，就可以正常通过遥控器控制开关机了";
            //t20.TestCount = 0;
            //t20.TestResult = "待测试";
            //testCases2.Add(t20);


            TestCases t21 = new TestCases();
            t21.Name = "case21_ota双版本互刷升级后拉流压测";
            t21.Description = "测试步骤：\r\n1、选择ota包1和ota包2（需要进行双固件升级压测的版本）\r\n2、点击开始测试即可\r\n\r\n1、使用自动化工具进行OTA升级1000次 \r\n2、每次刷机后工具HDMI出流（VC35P）、RTSP拉流正常\r\n3、查看工具每次刷机是否正常";
            t21.TestCount = 0;
            t21.TestResult = "待测试";
            testCases2.Add(t21);

            TestCases t22 = new TestCases();
            t22.Name = "case22_u盘救砖刷机后拉流压测";
            t22.Description = "（需要单独出一个默认自动以udhcpc true上电的固件用于压测，\n自己在jenkins编一个带这笔patch（40151）的固件用于测试即可）\r\n测试操作步骤：\r\n1、U盘里面放入带该patch的U盘升级固件\r\n2、点击开始测试即可\r\n\r\ncase描述：\r\n1、准备FAT32格式U盘，U盘内存在救砖文件；插入U盘；\r\n2、设备掉电情况下，保持针戳复位按键上电，进入救砖模式（我可以通过重启来进救砖模式）\r\n3、刷机后工具HDMI出流（VC35P）、RTSP拉流\r\n4、查看工具每次刷机是否正常\r\n5、循环500次";
            t22.TestCount = 0;
            t22.TestResult = "待测试";
            testCases2.Add(t22);

            TestCases t23 = new TestCases();
            t23.Name = "case23_重启5000次后拉流压测";
            t23.Description = "1、软件重启设备\r\n2、重启压测5000次\r\n3、压测5000次后进行 HDMI OUT出流和RTSP拉流测试";
            t23.TestCount = 0;
            t23.TestResult = "待测试";
            testCases2.Add(t23);

            TestCases t24 = new TestCases();
            t24.Name = "case24_继电器上下电压测逻辑1";
            t24.Description = "测试准备前提：\r\n1、测试工具一个工具可以带5路压测，填入对应IP\r\n2、提前将继电器接好对应测试设备，\r\n3、分别勾选需要测试的开关连接设备口即可测试对应设备，\r\n\r\n1、接Sensor ，使用 DC 12V 供电，测试系统起来后断电\r\n2、使用继电器设置上电45S，下电10S\r\n3、压测5000+次，9H";
            t24.TestCount = 0;
            t24.TestResult = "待测试";
            testCases2.Add(t24);

            TestCases t25 = new TestCases();
            t25.Name = "case25_继电器上下电压测逻辑2";
            t25.Description = "测试准备前提：\r\n1、测试工具一个工具可以带5路压测，填入对应IP\r\n2、提前将继电器接好对应测试设备，\r\n3、分别勾选需要测试的开关连接设备口即可测试对应设备，\r\n\r\n1、接Sensor ，使用 DC 12V 供电，测试系统起来期间断电\r\n2、使用继电器设置设置上电5S，下电5S   500次\r\n3、使用继电器设置设置上电25S，下电10S   500次\r\n4、使用继电器设置设置上电30S，下电5S   500次\r\n5、测试完系统正常上电开机起来，HDMI出流，RTSP拉流";
            t25.TestCount = 0;
            t25.TestResult = "待测试";
            testCases2.Add(t25);

            TestCases t26 = new TestCases();
            t26.Name = "case26_高帧模式分辨率轮询压测";
            t26.Description = "高帧模式：\r\n1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”主码流、辅码流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，\r\n切换完所有分辨率为1轮；\r\n主码流 rtsp://[ip]/1  \r\n辅码流 rtsp://[ip]/2";
            t26.TestCount = 0;
            t26.TestResult = "待测试";
            testCases2.Add(t26);

            TestCases t27 = new TestCases();
            t27.Name = "case27_高帧模式随机拉流切换压测";
            t27.Description = "高分辨率模式：\r\n1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”主码流、辅码流“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t27.TestCount = 0;
            t27.TestResult = "待测试";
            testCases2.Add(t27);

            TestCases t28 = new TestCases();
            t28.Name = "case28_性能模式分辨率轮询压测";
            t28.Description = "性能模式：\r\n1、RTSP 分别拉一路流，拉每一路流时 轮巡切换所有分辨率（web端切换分辨率）；\r\n”主码流、辅码流“\r\n脚本要求：测试完一路流拉另外一路流，循环测试不同流的切换分辨率测试；\r\n每次切换分辨率后预览10S，每路流切换分辨率循环遍历100轮，\r\n切换完所有分辨率为1轮；\r\n主码流  rtsp://[ip]/1 \r\n辅码流 rtsp://[ip]/2 ";
            t28.TestCount = 0;
            t28.TestResult = "待测试";
            testCases2.Add(t28);


            TestCases t29 = new TestCases();
            t29.Name = "case29_性能模式随机拉流切换压测";
            t29.Description = "高分辨率模式：\r\n1、RTSP 随机拉取一路流，预览10分钟，切换到另外一路流，轮巡2000次\r\n拉流覆盖轮巡：”主码流、辅码流“\r\n2、每切换一次检查帧率、码率、画面显示、CPU占用";
            t29.TestCount = 0;
            t29.TestResult = "待测试";
            testCases2.Add(t29);

            TestCases t30 = new TestCases();
            t30.Name = "case30_uvc和RTSP高分辨率模式编码复用压测";
            t30.Description = "UVC设置全景流\r\n1、网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流\r\n2、UVC 拉流H264 4K\r\n3、重新RTSP拉主流\r\n4、UCV 拉流MJPEG 1080P\r\n5、重新RTSP拉流\r\n6、网络流设置为主流 4K 30帧、辅流默认，RTSP 拉流主流\r\n7、重复1-6 500次";
            t30.TestCount = 0;
            t30.TestResult = "待测试";
            testCases2.Add(t30);

            //TestCases t31 = new TestCases();
            //t31.Name = "case31_uvc和RTSP高分辨率模式编码复用压测";
            //t31.Description = "UVC设置特写流\r\n1、网络流设置为主流 1080P30帧、辅流默认，RTSP 拉流主流\r\n2、UVC 拉流H264 4K\r\n3、重新RTSP拉主流\r\n4、UCV 拉流MJPEG 1080P\r\n5、重新RTSP拉流\r\n6、网络流设置为主流 4KP30帧、辅流默认，RTSP 拉流主流\r\n7、重复1-6 500次";
            //t31.TestCount = 0;
            //t31.TestResult = "待测试";
            //testCases2.Add(t31);

            TestCases t32 = new TestCases();
            t32.Name = "case32_云台预置位设置后煲机压测";
            t32.Description = "1、设置预置位，云台先运行到预置位，拍一张特写图存下来\r\n2、云台重载煲机48小时\r\n3、触发回到预置位，拍一张特写图存下来\r\n4、人工比对两张图片对应预置位的特写图片的位置是否符合，是否有偏移";
            t32.TestCount = 0;
            t32.TestResult = "待测试";
            testCases2.Add(t32);

            TestCases t33 = new TestCases();
            t33.Name = "case33_云台负载巡航压测";
            t33.Description = "1.云台上安装比标准模组重量重一倍的模组\r\n2. 设置5个预置位（覆盖不同方位和倾斜角度），在每个预置位采集一张图片存下来\r\n①水平-170°，俯仰90°\r\n②水平170°，俯仰-35°\r\n③水平-170°，俯仰-35°\r\n④水平170°，俯仰90°\r\n⑤水平0°，俯仰33°\r\n3. 创建巡航路径：1→3→5→2→4→1\r\n4. 设置每个点停留时间为5秒\r\n5.巡航48小时\r\n6、停止测试，回到每个预置位，并采集一张图片\r\n7、人工检查每个预置位的偏移情况";
            t33.TestCount = 0;
            t33.TestResult = "待测试";
            testCases2.Add(t33);

            TestCases t34 = new TestCases();
            t34.Name = "case34_水平堵转反向压测";
            t34.Description = "步骤\r\n1、先手动设置好预置位1\r\n2、手动使用工具触发到预置位1->存一张特写图\r\n3、云台从预置位1到->水平-170度转动 -- 持续500次\r\n4、500次后解除堵转，重新恢复预置位1->存一张特写图\r\n5、人工比对堵转前后前后每个预置位的特写图\r\n\r\n1. 云台初始化为0时，手动控制云台旋转至水平-170°\r\n2. 在旋转前阻挡云台转动\r\n3.步骤1-2操作500次\r\n4.解除堵转，水平、俯仰旋转云台";
            t34.TestCount = 0;
            t34.TestResult = "待测试";
            testCases2.Add(t34);

            TestCases t35 = new TestCases();
            t35.Name = "case35_水平堵转正向压测";
            t35.Description = "步骤\r\n1、先手动设置好预置位1\r\n2、手动使用工具触发到预置位1->存一张特写图\r\n3、云台从预置位1到->水平170度转动 -- 持续500次\r\n4、500次后解除堵转，重新恢复预置位1->存一张特写图\r\n5、人工比对堵转前后前后每个预置位的特写图\r\n\r\n1. 云台初始化为0时，手动控制云台旋转至水平170°\r\n2. 在旋转前阻挡云台转动\r\n3.步骤1-2操作500次\r\n4.解除堵转，水平、俯仰旋转云台";
            t35.TestCount = 0;
            t35.TestResult = "待测试";
            testCases2.Add(t35);

            TestCases t36 = new TestCases();
            t36.Name = "case36_俯仰堵转正向压测";
            t36.Description = "步骤\r\n1、先手动设置好预置位1\r\n2、手动使用工具触发到预置位1->存一张特写图\r\n3、云台从预置位1到->俯仰90度转动 -- 持续500次\r\n4、500次后解除堵转，重新恢复预置位1->存一张特写图\r\n5、人工比对堵转前后前后每个预置位的特写图\r\n\r\n1. 云台初始化为0时，手动控制云台旋转至俯仰90度\r\n2. 在旋转前阻挡云台转动\r\n3.步骤1-2操作500次\r\n4.解除堵转，水平、俯仰旋转云台";
            t36.TestCount = 0;
            t36.TestResult = "待测试";
            testCases2.Add(t36);

            TestCases t37 = new TestCases();
            t37.Name = "case37_俯仰堵转反向压测";
            t37.Description = "步骤\r\n1、先手动设置好预置位1\r\n2、手动使用工具触发到预置位1->存一张特写图\r\n3、云台从预置位1到->俯仰-35度转动 -- 持续500次\r\n4、500次后解除堵转，重新恢复预置位1->存一张特写图\r\n5、人工比对堵转前后前后每个预置位的特写图\r\n\r\n1. 云台初始化为0时，手动控制云台旋转至俯仰-35度\r\n2. 在旋转前阻挡云台转动\r\n3.步骤1-2操作500次\r\n4.解除堵转，水平、俯仰旋转云台";
            t37.TestCount = 0;
            t37.TestResult = "待测试";
            testCases2.Add(t37);


            TestCases t38 = new TestCases();
            t38.Name = "case38_休眠唤醒压测";
            t38.Description = "（测试前接线已经确认环境一定要找广涛或者家敏）\r\n操作逻辑：\r\n1、确保遥控器手动控制power键可以开关待测的0105的设备整机\r\n2、将板子的电源控制板供电打开，连接VCC口串口板接到电脑上\r\n3、测试板围绕遥控器控制设备摆放保证能够接收到红外\r\n4、触发工具开始测试\r\n\r\n1、红外遥控器按待机\r\n2、开机后设备查看HDMI主动出流 \r\n3、开机后设备后RTSP 同时拉流：主码流、辅码流\r\n4、检查帧率、码率、画面显示正常\r\n5、压测1000次";
            t38.TestCount = 0;
            t38.TestResult = "待测试";
            testCases2.Add(t38);

            TestCases t39 = new TestCases();
            t39.Name = "case39_模式切换压测";
            t39.Description = "1、切换高分辨率模式拉流2路，切换到高帧模式拉流2路\r\n2、从高帧模式再切换到高分辨率模式拉流2路\r\n3、高分辨率模式切换到性能模式，拉流2路\r\n4、性能模式切换回到高分辨率模式拉流2路\r\n5、切换到高帧模式拉流2路，切换到性能模式拉流2路\r\n6、性能模式切换到高帧模式拉流2路\r\n7、以上循环测试1-6 1000次";
            t39.TestCount = 0;
            t39.TestResult = "待测试";
            testCases2.Add(t39);

            TestCases t40 = new TestCases();
            t40.Name = "case40_电源键控制SKDL0105项目上下电压测";
            t40.Description = "通过继电器控制电源键长按从而进行上下电操作模拟设备开关机压测";
            t40.TestCount = 0;
            t40.TestResult = "待测试";
            testCases2.Add(t40);

            table2_testCase.DataSource = testCases2;
        }

        private async void table2_testCase_CellClick(object sender, TableClickEventArgs e)
        {
            // e.Record 是当前行的数据对象
            if (e.Record is TestCases item)
            {
                DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "是否运行当前点击的测试用例",
                    "如果当前case正在运行，请勿重复触发！！！",
                    AntdUI.TType.Warn));

                if (result != DialogResult.Yes && result != DialogResult.OK)
                {
                    // 用户点了取消，取消关闭，恢复Loading状态
                    return;
                }
                else
                { // 判断是否点击了“操作”列 (Key 为 "BtnText")
                    if (e.Column.Key == "BtnText")
                    {
                        // 防止重复点击
                        if (item.BtnText == "测试中...") return;

                        // 1. 更新 UI 状态
                        item.BtnText = "测试中...";
                        item.TestResult = "运行中...";
                        stopTest = false;
                        try
                        {
                            item.TestCount = 0;
                            // 获取token
                            buttonGetToken_Click(null, null);
                            await Task.Delay(1000);

                            // 设置自动ip
                            if (autoIPCheckBox.Checked)
                            {
                                getNetWorkConfigBtn_Click(null, null);
                                await Task.Delay(1000);
                                setUdhcpcBtn_Click(null, null);
                                await Task.Delay(1000);
                            }



                            resetAllStreamDefaultConfigBtn_Click(null, null);
                            await Task.Delay(1000);


                            // 按照case运行，并实时修改结果
                            if (item.Name == "case1_分辨率轮询压测")
                            {
                                TestCase1_2(item);
                            }
                            if (item.Name == "case2_随机拉流切换压测")
                            {
                                TestCase2_2(item);
                            }
                            if (item.Name == "case3_clumsy拉流限速压测")
                            {
                                TestCase3_2(item);
                            }
                            if (item.Name == "case4_64M码率循环拉流压测")
                            {
                                TestCase4_2(item);
                            }
                            if (item.Name == "case5_12M码率循环拉流压测")
                            {
                                TestCase5_2(item);
                            }
                            if (item.Name == "case6_2M码率循环拉流压测")
                            {
                                TestCase6_2(item);
                            }
                            if (item.Name == "case7_extreme性能模式拉流压测")
                            {
                                TestCase7_2(item);
                            }
                            if (item.Name == "case8_highFPS高帧率模式拉流压测")
                            {
                                TestCase8_2(item);
                            }
                            if (item.Name == "case9_highFPS高帧率模式拉流压测")
                            {
                                TestCase9_2(item);
                            }
                            if (item.Name == "case10_all30FPS_main4K_sub720P拉流压测")
                            {
                                TestCase10_2(item);
                            }
                            if (item.Name == "case11_all25FPS_main1080P_sub576P拉流压测")
                            {
                                TestCase11_2(item);
                            }
                            if (item.Name == "case12_clumsy拉流包括RTMP全流限速压测")
                            {
                                TestCase12_2(item);
                            }
                            if (item.Name == "case13_uvc全景高分辨率模式切换分辨率压测")
                            {
                                TestCase13_2(item);
                            }
                            if (item.Name == "case14_uvc全景高帧率模式切换分辨率压测")
                            {
                                TestCase14_2(item);
                            }
                            //if (item.Name == "case15_uvc特写高分辨率模式切换分辨率压测")
                            //{
                            //    //TestCase15(item);
                            //}
                            //if (item.Name == "case16_uvc特写高帧率模式切换分辨率压测")
                            //{
                            //    //TestCase16(item);
                            //}
                            if (item.Name == "case17_gbs通道1和2拉流压测-python")
                            {
                                TestCase17_2(item);
                            }
                            if (item.Name == "case18_全功能持续老化测试")
                            {
                                TestCase18_2(item);
                            }
                            if (item.Name == "case19_重启全视频流压测")
                            {
                                TestCase19_2(item);

                            }
                            //if (item.Name == "case20_红外控制休眠唤醒拉流压测")
                            //{
                            //    //TestCase20(item);
                            //}
                            if (item.Name == "case21_ota双版本互刷升级后拉流压测")
                            {
                                TestCase21_2(item);
                            }
                            if (item.Name == "case22_u盘救砖刷机后拉流压测")
                            {
                                TestCase22_2(item);
                            }
                            if (item.Name == "case23_重启5000次后拉流压测")
                            {
                                TestCase23_2(item);
                            }
                            if (item.Name == "case24_继电器上下电压测逻辑1")
                            {
                                TestCase24_2(item);
                            }
                            if (item.Name == "case25_继电器上下电压测逻辑2")
                            {
                                TestCase25_2(item);
                            }
                            if (item.Name == "case26_高帧模式分辨率轮询压测")
                            {
                                TestCase26_2(item);
                            }
                            if (item.Name == "case27_高帧模式随机拉流切换压测")
                            {
                                TestCase27_2(item);
                            }
                            if (item.Name == "case28_性能模式分辨率轮询压测")
                            {
                                TestCase28_2(item);
                            }
                            if (item.Name == "case29_性能模式随机拉流切换压测")
                            {
                                TestCase29_2(item);
                            }
                            if (item.Name == "case30_uvc和RTSP高分辨率模式编码复用压测")
                            {
                                TestCase30_2(item);
                            }
                            //if (item.Name == "case31_uvc和RTSP高分辨率模式编码复用压测")
                            //{
                            //    //TestCase31(item);
                            //}
                            if (item.Name == "case32_云台预置位设置后煲机压测")
                            {
                                TestCase32_2(item);
                            }
                            if (item.Name == "case33_云台负载巡航压测")
                            {
                                TestCase33_2(item);
                            }
                            if (item.Name == "case34_水平堵转反向压测")
                            {
                                TestCase34_2(item);
                            }
                            if (item.Name == "case35_水平堵转正向压测")
                            {
                                TestCase35_2(item);
                            }
                            if (item.Name == "case36_俯仰堵转正向压测")
                            {
                                TestCase36_2(item);
                            }
                            if (item.Name == "case37_俯仰堵转反向压测")
                            {
                                TestCase37_2(item);
                            }
                            if (item.Name == "case38_休眠唤醒压测")
                            {
                                TestCase38_2(item);
                            }
                            if (item.Name == "case39_模式切换压测")
                            {
                                TestCase39_2(item);
                            }
                            if (item.Name == "case40_电源键控制SKDL0105项目上下电压测")
                            {
                                TestCase40_2(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            item.TestResult = "ERROR";
                            MessageBox.Show("测试异常: " + ex.Message);
                        }
                        finally
                        {
                            // 4. 恢复按钮状态
                            item.BtnText = "开始测试";
                        }
                    }

                }
            }
        }

        private async void checkbox_2streamRTSPOn_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    panoramicMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOnBtn_Click(null, null);
                    await Task.Delay(100);
                    LogSaveOutput("RTSP流启动成功");
                }
                else
                {
                    panoramicMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    closeUpMainStreamOffBtn_Click(null, null);
                    await Task.Delay(100);
                    LogSaveOutput("RTSP流停止成功");
                }
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void setPresetIdBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.SetPresetPTZId(int.Parse(input_presetId.Text)));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台预置位设置异常：\n {ex.ToString()}");
            }
        }

        private async void launchPresetIdBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.LaunchPresetPTZId(int.Parse(input_presetId.Text)));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台预置位调用异常：\n {ex.ToString()}");
            }
        }

        private async void deleteAllPresetIdBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.ClearPresetPTZId());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台预置位清除异常：\n {ex.ToString()}");
            }
        }

        // 启动五个预置位巡航压测
        private async void checkbox_ptzCruiseTest_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    stopPtzCircleCurTest = false;
                    LogSaveOutput("启动物理云台巡航测试移动成功");
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 1");
                            await _api.LaunchPresetPTZId(1);
                            await Task.Delay(5000);
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 3");
                            await _api.LaunchPresetPTZId(3);
                            await Task.Delay(5000);
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 5");
                            await _api.LaunchPresetPTZId(5);
                            await Task.Delay(5000);
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 2");
                            await _api.LaunchPresetPTZId(2);
                            await Task.Delay(5000);
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 4");
                            await _api.LaunchPresetPTZId(4);
                            await Task.Delay(5000);
                            LogSaveOutput($"Camera PTZ 云台到预置位 - 1");
                            await _api.LaunchPresetPTZId(1);
                            await Task.Delay(5000);
                            if (stopPtzCircleCurTest)
                            {
                                LogSaveOutput("停止物理云台巡航测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopPtzCircleCurTest = true;
                }
                LogSaveOutput($"物理云台巡航测试Action：stopTest - {stopPtzCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void checkbox_ptzCruiseReverse170Test_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    LogSaveOutput("启动-170度反向水平压测成功");
                    await Task.Run(async () =>
                    {
                        LogSaveOutput($"触发回到预置位1");
                        await _api.LaunchPresetPTZId(1);
                        await Task.Delay(1000);
                        LogSaveOutput($"水平旋转1");
                        await _api.SetPtzControlAction(1, 5, "left");
                        await Task.Delay(1000);
                        if (stopPtzCircleCurTest)
                        {
                            LogSaveOutput("停止-170度反向水平压测");
                        }
                    });
                }
                else
                {
                }
                LogSaveOutput($"-170度反向水平压测Action：stopTest");
                checkbox_ptzCruiseReverse170Test.Checked = false;
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void checkbox_ptzCruise170Test_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    LogSaveOutput("启动170度正向水平压测成功");
                    await Task.Run(async () =>
                    {
                        LogSaveOutput($"触发回到预置位1");
                        await _api.LaunchPresetPTZId(1);
                        await Task.Delay(1000);
                        LogSaveOutput($"水平旋转1");
                        await _api.SetPtzControlAction(1, 5, "right");
                        await Task.Delay(1000);
                        if (stopPtzCircleCurTest)
                        {
                            LogSaveOutput("停止170度正向水平压测");
                        }
                    });
                }
                else
                {
                }
                LogSaveOutput($"170度正向水平压测Action：stopTest");
                checkbox_ptzCruise170Test.Checked = false;
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void checkbox_ptzCruise90Test_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    LogSaveOutput("启动90度正向俯仰压测成功");
                    await Task.Run(async () =>
                    {
                        LogSaveOutput($"触发回到预置位1");
                        await _api.LaunchPresetPTZId(1);
                        await Task.Delay(1000);
                        LogSaveOutput($"90度正向俯仰旋转1");
                        await _api.SetPtzControlAction(1, 5, "top");
                        await Task.Delay(1000);
                        if (stopPtzCircleCurTest)
                        {
                            LogSaveOutput("停止90度正向俯仰压测");
                        }
                    });
                }
                else
                {
                }
                LogSaveOutput($"90度正向俯仰压测Action：stopTest");
                checkbox_ptzCruise90Test.Checked = false;
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void checkbox_ptzCruiseReverse35Test_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    LogSaveOutput("启动-35度反向俯仰压测成功");
                    await Task.Run(async () =>
                    {
                        LogSaveOutput($"触发回到预置位1");
                        await _api.LaunchPresetPTZId(1);
                        await Task.Delay(1000);
                        LogSaveOutput($"-35度反向俯仰旋转1");
                        await _api.SetPtzControlAction(1, 5, "down");
                        await Task.Delay(1000);
                        if (stopPtzCircleCurTest)
                        {
                            LogSaveOutput("停止-35度反向俯仰压测");
                        }
                    });
                }
                else
                {
                }
                LogSaveOutput($"-35度反向俯仰压测Action：stopTest");
                checkbox_ptzCruiseReverse35Test.Checked = false;
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }

        }

        private async void checkbox_upDownLeftRightPTZCircle_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);
                    stopPtzCircleCurTest = false;
                    LogSaveOutput("启动云台水平俯仰持续压测成功");
                    int i = 0;
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            i++;
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(1000);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(1000);
                            LogSaveOutput($"第{i}次云台水平俯仰运动已完成！");
                            if (stopPtzCircleCurTest)
                            {
                                LogSaveOutput("停止启动云台水平俯仰持续测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopPtzCircleCurTest = true;
                }
                LogSaveOutput($"启动云台水平俯仰持续压测Action：stopTest - {stopPtzCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private async void checkbox_mimimumAdjustupDownLeftRightPTZCircle_CheckedChanged(object sender, BoolEventArgs e)
        {
            try
            {
                if (e.Value)
                {
                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);
                    stopPtzCircleCurTest = false;
                    LogSaveOutput("启动最小精度云台水平俯仰持续压测成功");
                    int i = 0;
                    await Task.Run(async () =>
                    {
                        while (true)
                        {
                            i++;
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(100);
                            LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                            await Task.Delay(100);
                            LogSaveOutput($"第{i}次云台最小精度水平俯仰运动已完成！");
                            if (stopPtzCircleCurTest)
                            {
                                LogSaveOutput("停止启动云台最小精度水平俯仰持续测试");
                                break;
                            }
                        }
                    });
                }
                else
                {
                    stopPtzCircleCurTest = true;
                }
                LogSaveOutput($"启动云台最小精度水平俯仰持续压测Action：stopTest - {stopPtzCircleCurTest}");
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "操作提示",
                $"操作失败：{ex.Message}", AntdUI.TType.Error));
            }
        }

        private void getCurComputerUvcDevicePathBtn_Click(object sender, EventArgs e)
        {
            try
            {
                List<CameraInfo> cameras = GetCameras("Seewo Lubo");
                input_curUvcDevicePath.Text = cameras[0].DevicePath;
            }
            catch (Exception)
            {

            }

        }
        
        
        //添加UVC失败拉流的类型,保存成日志
        private void LogFailType(string resolution, string format)
        {
            try
            {
                string ipSafe = _currentIp?.Replace(".", "_") ?? "unknown";
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", ipSafe);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string failFilePath = Path.Combine(dir, "Failtype.txt");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - 分辨率: {resolution}, 格式: {format} 启动失败";
                lock (_failFileLock)
                {
                    File.AppendAllText(failFilePath, line + Environment.NewLine);
                }
            }
            catch { /* 忽略写入错误 */ }
        }
        // 改动 :添加的私有方法,启动 UVC 流,适用手动拉流和指定path拉流
        private async Task<bool> StartUVC(int width, int height, string format, string devicePath = null)
        {
            // 如果已有摄像头实例，先释放
            if (camera1 != null)
            {
                camera1.Dispose();
                camera1 = null;
            }

            // 刷新 PictureBox
            pictureBox_uvcStream.Invalidate();
            pictureBox_uvcStream.Refresh();
            await Task.Delay(100);

            // 创建新实例并设置参数
            camera1 = new VideoCapturer();
            camera1.SetPreviewSize(width, height);
            camera1.SetDisplayWindow(pictureBox_uvcStream.Handle);
            camera1.SetDisplaySize(pictureBox_uvcStream.Width, pictureBox_uvcStream.Height);

            // 获取设备列表
            List<CameraInfo> cameras = GetCameras("Seewo Lubo");
            if (cameras.Count == 0)
            {
                LogSaveOutput("未找到名称为 'Seewo Lubo' 的摄像头");
                return false;
            }

            int index = 0;
            if (!string.IsNullOrEmpty(devicePath))
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    if (cameras[i].DevicePath == devicePath)
                    {
                        index = i;
                        break;
                    }
                }
            }

            // 启动捕获
            bool success = await camera1.StartupCapture(cameras[index], index, format, checkBoxDecodeTest.Checked);
            if (success)
            {
                LogSaveOutput($"拉流成功: {width}x{height} 格式 {format}");
            }
            else
            {
                LogSaveOutput($"拉流失败: {width}x{height} 格式 {format}");
                camera1?.Dispose();
                camera1 = null;
            }
            return success;
        }

        private async void uvcStreamOnSpecificDevicePathBtn_Click(object sender, EventArgs e)
        {
            try
            {
                //改动:使用私有方法
                //uvcStreamON_byDevicePath(input_curUvcDevicePath.Text);
                int w = int.Parse(input1_uvc_x.Text);
                int h = int.Parse(input2_uvc_y.Text);
                string format = input_Uvctype.Text;
                string devicePath = input_curUvcDevicePath.Text;
                await StartUVC(w, h, format, devicePath);

            }
            catch (Exception ex)
            {

            }

        }

        private void pictureBox_uvcStream_SizeChanged(object sender, EventArgs e)
        {
            WindowsFunc.ResizeVideoCapture(camera1, pictureBox_uvcStream);
        }

        private async void readRelaySwitchSpecificStatusBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(await readRelayStatus(Int16.Parse(input_ToBeReadRelayStatus.Text)) ? "success" : "failed");
        }

        // 按下power
        private async void pushForwardBtn_Click(object sender, EventArgs e)
        {
            try
            {
                // 全关
                electricAllOffBtn_Click(null, null);
                await Task.Delay(500);
                // 开关2 off
                if (await controlRelay(1, false, 0))
                {
                    if (await controlRelay(1, true, 0))
                    {
                        // 如果6和7 off
                        if (!await readRelayStatus(6) && !await readRelayStatus(7))
                        {
                            await Task.Delay(500);
                            // 再去打开8和9
                            await controlRelay(7, true, 0);
                            await Task.Delay(500);
                            await controlRelay(8, true, 0);
                        }
                        else
                        {
                            // 先关闭6和7.再去打开8和9
                            if (await controlRelay(5, false, 0) && await controlRelay(6, false, 0))
                            {
                                await Task.Delay(1000);
                                // 如果6和7 off
                                if (!await readRelayStatus(6) && !await readRelayStatus(7))
                                {
                                    await Task.Delay(500);
                                    // 再去打开8和9
                                    await controlRelay(7, true, 0);
                                    await Task.Delay(500);
                                    await controlRelay(8, true, 0);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }

        }


        // 松开power
        private async void pushBackwardBtn_Click(object sender, EventArgs e)
        {
            try
            {
                // 全关
                electricAllOffBtn_Click(null, null);
                await Task.Delay(500);
                // 开关2 off
                if (await controlRelay(1, false, 0))
                {
                    if (await controlRelay(1, true, 0))
                    {
                        // 如果8和9 off
                        if (!await readRelayStatus(8) && !await readRelayStatus(9))
                        {
                            await Task.Delay(500);
                            // 再去打开6和7
                            await controlRelay(5, true, 0);
                            await Task.Delay(500);
                            await controlRelay(6, true, 0);
                        }
                        else
                        {
                            // 先关闭8和9.再去打开6和7
                            if (await controlRelay(7, false, 0) && await controlRelay(8, false, 0))
                            {
                                await Task.Delay(1000);
                                // 如果8和9 off
                                if (!await readRelayStatus(8) && !await readRelayStatus(9))
                                {
                                    await Task.Delay(500);
                                    // 再去打开6和7
                                    await controlRelay(5, true, 0);
                                    await Task.Delay(500);
                                    await controlRelay(6, true, 0);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }
        }

        private void initNetWorkStreamPlayer()
        {
            player1 = new OpenCvRtspPlayer(netWorkStreamPB);
        }
        private OpenCvRtspPlayer player1;

        private async void networkStreamOnBtn_Click(object sender, EventArgs e)
        {
            string url = networkUrlInput.Text.Trim();
            player1.Start(url, checkBoxDecodeTest.Checked);
            LogSaveOutput($"开始拉流: {url}");
        }

        private void getActualFPSAndBitRateBtn_Click(object sender, EventArgs e)
        {
            PlayerStatus status;

            this.BeginInvoke(async () =>
            {
                //while (true)
                //{
                //    status = player1.GetPlayerStatus();
                //    //var ffprobContent = player1.GetBitrateDebugInfo();
                //    //LogSaveOutput($"获取当前流状态：\n 码率：{status.TotalBitrateKbps}\n 帧率：{status.Fps}\n ffprob信息：{ffprobContent}\n");
                //    LogSaveOutput($"获取当前流状态：\n 码率：{status.TotalBitrateKbps}\n 帧率：{status.Fps}\n");
                //    await Task.Delay(1000);
                //}
                LogSaveOutput( $"当前码率：{await player1.GetBitrateAsync(networkUrlInput.Text)}");
                LogSaveOutput($"当前帧率：{player1.GetPlayerStatus().Fps}");
            });
        }

        private void networkStreamOffBtn_Click(object sender, EventArgs e)
        {
            player1.Stop();
            LogSaveOutput("停止拉流");
        }

        private void networkStreamSnapshotBtn_Click(object sender, EventArgs e)
        {
            player1.Snapshot("./ok.jpg");
            LogSaveOutput("网络流截图保存到 ok.jpg");
        }

        ExcelHelper excelHelper;
        private async void chooseExcelBtn_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Title = "选择要读写的Excel文件";
            openFileDialog.Filter = "Excel Files|*.xlsx;*.xls";
            openFileDialog.Multiselect = false;
            openFileDialog.InitialDirectory = Environment.CurrentDirectory;
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                excelInput.Text = openFileDialog.FileName;
                await Task.Delay(1000);
                excelHelper = new ExcelHelper(excelInput.Text);
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "操作提示",
                    $"Excel文件加载成功！请点击 - 锁定测试表名 - 按钮，输入对应待测试表单名称后，点击锁定测试表名即可点击 - 开始测试", AntdUI.TType.Success));
            }
        }

        private void readExcelBtn_Click(object sender, EventArgs e)
        {
            if (excelHelper != null)
            {
                string columnName = columnContentInput.Text;
                int rowNumber = int.Parse(rowNumberInput.Text);
                readWriteContentInput.Text = excelHelper.ReadCell(columnName, rowNumber);
                LogSaveOutput($"Excel 读取列名：{columnName} 行数：{rowNumber} - 内容：{readWriteContentInput.Text}");
            }
        }

        private void writeExcelBtn_Click(object sender, EventArgs e)
        {
            if (excelHelper != null)
            {
                string columnName = columnContentInput.Text;
                int rowNumber = int.Parse(rowNumberInput.Text);
                string content = readWriteContentInput.Text;
                excelHelper.WriteTestResult(columnName, rowNumber, content);
                excelHelper.Save();
                LogSaveOutput($"Excel {columnName} {rowNumber} 写入内容: {content} - 并且保存成功！");
            }
        }

        private void lockTestSheetNameBtn_Click(object sender, EventArgs e)
        {
            excelHelper.LoadSheet(excelSheetInput.Text);
            excelHelper.HeaderRow = 2;
            AntdUI.Message.success(this, $"已锁定测试用例表单: {excelSheetInput.Text} - 并且默认表头行数为2，从第二行开始遍历……");
        }

        private async void recoverDefaultConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("panoramicMain"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("panoramicSub"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("closeUpMain"));
                LogSaveOutput(await _api.ResetSpecVideoStreamConfig("closeUpSub"));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"读取全部视频流默认配置异常！\n{ex.ToString()}");
            }
        }

        private async void modifiedTestConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                string testBitRate = testBitRateInput.Text;
                string testProtocol = testProtocolInput.Text;
                string testFps = testFPSInput.Text;
                string testIGop = testiGopInput.Text;
                string testResolution = testResolutionXInput.Text + "x" + testResolutionYInput.Text;
                string testBRControl = testBRControlInput.Text;

                LogSaveOutput(cur_panoramicMain_stream_config = cur_panoramicMain_stream_config
                    .Replace($"\"bitRate\": {JObject.Parse(cur_panoramicMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {testBitRate},")
                    .Replace($"\"encFmt\": \"{JObject.Parse(cur_panoramicMain_stream_config)["encFmt"].ToString()}\",", $"\"encFmt\": \"{testProtocol}\",")
                    .Replace($"\"fps\": {JObject.Parse(cur_panoramicMain_stream_config)["fps"].ToString()},", $"\"fps\": {testFps},")
                    .Replace($"\"gop\": {JObject.Parse(cur_panoramicMain_stream_config)["gop"].ToString()},", $"\"gop\": {testIGop},")
                    .Replace($"\"rcMode\": \"{JObject.Parse(cur_panoramicMain_stream_config)["rcMode"].ToString()}\",", $"\"rcMode\": \"{testBRControl}\",")
                    .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"{testResolution}\""));


                LogSaveOutput(cur_panoramicSub_stream_config = cur_panoramicSub_stream_config
                    .Replace($"\"bitRate\": {JObject.Parse(cur_panoramicSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {testBitRate},")
                    .Replace($"\"encFmt\": \"{JObject.Parse(cur_panoramicSub_stream_config)["encFmt"].ToString()}\",", $"\"encFmt\": \"{testProtocol}\",")
                    .Replace($"\"fps\": {JObject.Parse(cur_panoramicSub_stream_config)["fps"].ToString()},", $"\"fps\": {testFps},")
                    .Replace($"\"gop\": {JObject.Parse(cur_panoramicSub_stream_config)["gop"].ToString()},", $"\"gop\": {testIGop},")
                    .Replace($"\"rcMode\": \"{JObject.Parse(cur_panoramicSub_stream_config)["rcMode"].ToString()}\",", $"\"rcMode\": \"{testBRControl}\",")
                    .Replace($"\"resolution\": \"{JObject.Parse(cur_panoramicSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"{testResolution}\""));


                LogSaveOutput(cur_closeUpMain_stream_config = cur_closeUpMain_stream_config
                    .Replace($"\"bitRate\": {JObject.Parse(cur_closeUpMain_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {testBitRate},")
                    .Replace($"\"encFmt\": \"{JObject.Parse(cur_closeUpMain_stream_config)["encFmt"].ToString()}\",", $"\"encFmt\": \"{testProtocol}\",")
                    .Replace($"\"fps\": {JObject.Parse(cur_closeUpMain_stream_config)["fps"].ToString()},", $"\"fps\": {testFps},")
                    .Replace($"\"gop\": {JObject.Parse(cur_closeUpMain_stream_config)["gop"].ToString()},", $"\"gop\": {testIGop},")
                    .Replace($"\"rcMode\": \"{JObject.Parse(cur_closeUpMain_stream_config)["rcMode"].ToString()}\",", $"\"rcMode\": \"{testBRControl}\",")
                    .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpMain_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"{testResolution}\""));


                LogSaveOutput(cur_closeUpSub_stream_config = cur_closeUpSub_stream_config
                    .Replace($"\"bitRate\": {JObject.Parse(cur_closeUpSub_stream_config)["bitRate"].ToString()},", $"\"bitRate\": {testBitRate},")
                    .Replace($"\"encFmt\": \"{JObject.Parse(cur_closeUpSub_stream_config)["encFmt"].ToString()}\",", $"\"encFmt\": \"{testProtocol}\",")
                    .Replace($"\"fps\": {JObject.Parse(cur_closeUpSub_stream_config)["fps"].ToString()},", $"\"fps\": {testFps},")
                    .Replace($"\"gop\": {JObject.Parse(cur_closeUpSub_stream_config)["gop"].ToString()},", $"\"gop\": {testIGop},")
                    .Replace($"\"rcMode\": \"{JObject.Parse(cur_closeUpSub_stream_config)["rcMode"].ToString()}\",", $"\"rcMode\": \"{testBRControl}\",")
                    .Replace($"\"resolution\": \"{JObject.Parse(cur_closeUpSub_stream_config)["resolution"].ToString()}\"", $"\"resolution\": \"{testResolution}\""));

                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicMain", cur_panoramicMain_stream_config));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig("panoramicSub", cur_panoramicSub_stream_config));
                LogSaveOutput(set_closeUpMain_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpMain", cur_closeUpMain_stream_config));
                LogSaveOutput(set_closeUpSub_stream_config_result = await _api.SetSpecVideoStreamConfig("closeUpSub", cur_closeUpSub_stream_config));

                LogSaveOutput($"修改测试配置：\n 码率：{testBitRate}\n 协议：{testProtocol}\n FPS：{testFps}\n iGop: {testIGop}\n 分辨率: {testResolution}");

            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改测试配置异常！\n{ex.ToString()}");
            }
        }

        private async void videoReachTestBtn_Click(object sender, EventArgs e)
        {
            stopTest = false;

            if (excelInput.Text == "" || excelSheetInput.Text == "" || input_Rtmp1.Text == "")
            {
                AntdUI.Message.error(this, "请确保已选择Excel文件，锁定测试表单，并且填写了四路RTMP地址！");
            }
            else
            {
                // ufo浏览器请务必将测试设备对着ufo浏览器进行测试，保证画面内容符合测试要求
                DialogResult result = AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "测试前提醒！",
                    "ufo浏览器请务必将测试设备对着ufo浏览器进行测试，保证画面内容符合测试要求！！！",
                    AntdUI.TType.Warn));

                if (result != DialogResult.Yes && result != DialogResult.OK)
                {
                    // 用户点了取消，取消关闭，恢复Loading状态
                    AntdUI.Message.error(this, "用户点了取消，不开始测试");
                    return;
                }
                else
                {
                    AntdUI.Message.success(this, "已开始视频可达性测试，请耐心等待测试完成，期间请勿关闭程序！");

                    buttonGetToken_Click(null, null);
                    await Task.Delay(1000);

                    resetAllStreamDefaultConfigBtn_Click(null, null);
                    await Task.Delay(1000);

                    int testRowCount = excelHelper.GetRowCount();
                    int currentTestRow = 0;

                    int passCount = 0;
                    int failCount = 0;

                    testCountLabel.Text = $"{testRowCount - 1}"; // 减去表头行

                    await Task.Delay(1000);

                    string testFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testData", _currentIp.Replace(".", "_").Replace(":", "_"), excelSheetInput.Text);
                    LogSaveOutput($"测试文件夹：{testFolder}");
                    if (Directory.Exists(testFolder))
                    {
                        Directory.Delete(testFolder, true);
                    }

                    for (int i = 1; i < testRowCount; i++)
                    {
                        try
                        {
                            string testColumnNameRTSP = "RTSP测试结果";
                            string testColumnNameRTMP = "RTMP测试结果";
                            string testColumnNameGB2818 = "GB2818测试结果";
                            string testColumnNameWeb = "web测试结果";

                            string testUrl = "";
                            string testProtocol = "";
                            string testResolution = "";
                            string testBitRate = "";
                            string testBRControl = "";
                            string testIGop = "";
                            string testFps = "";


                            if (stopTest)
                            {
                                AntdUI.Notification.success(this, "测试进度提醒", $"{excelSheetInput.Text} - 停止测试");
                                LogSaveOutput($"{excelSheetInput} - 停止测试");
                                break;
                            }

                            string testUrlName = excelHelper.ReadCell("码流", i);

                            LogSaveOutput($"当前测试码流为：{testUrlName}");

                            if (testUrlName.Contains("路主码流") || testUrlName.Contains("路辅码流") || testUrlName.Contains("路特写") || testUrlName.Contains("路全景"))
                            {
                                testStreamInput.Text = testUrlName;
                                if (testUrlName == "第一路主码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/1";
                                }
                                if (testUrlName == "第二路辅码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/2";
                                }
                                if (testUrlName == "第一路特写主码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/1";
                                }
                                if (testUrlName == "第二路全景主码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/2";
                                }
                                if (testUrlName == "第三路特写辅码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/3";
                                }
                                if (testUrlName == "第四路全景辅码流")
                                {
                                    testUrl = $"rtsp://{textBox_ip.Text}/4";
                                }
                                networkUrlInput.Text = testUrl;

                                LogSaveOutput($"测试URL已设置为：{testUrl} - 开始拉流测试");

                                testProtocol = excelHelper.ReadCell("编码协议", i);
                                testProtocolInput.Text = testProtocol;
                                testResolution = excelHelper.ReadCell("分辨率", i);
                                testResolutionXInput.Text = testResolution.Split('*')[0];
                                testResolutionYInput.Text = testResolution.Split('*')[1];
                                testBitRate = excelHelper.ReadCell("码率", i);
                                testBitRateInput.Text = testBitRate;
                                testBRControl = excelHelper.ReadCell("码率控制", i);

                                if (testBRControl == "固定码率")
                                {
                                    testBRControlInput.Text = "CBR";
                                }
                                else
                                {
                                    testBRControlInput.Text = "VBR";
                                }
                                testIGop = excelHelper.ReadCell("I帧间隔", i);
                                testiGopInput.Text = testIGop;
                                testFps = excelHelper.ReadCell("帧率", i);
                                testFPSInput.Text = testFps;

                                buttonGetToken_Click(null, null);
                                await Task.Delay(1000);

                                recoverDefaultConfigBtn_Click(null, null);
                                await Task.Delay(3000);

                                readAllStreamCurConfigBtn_Click(null, null);
                                await Task.Delay(3000);

                                modifiedTestConfigBtn_Click(null, null);
                                await Task.Delay(3000);
                                LogSaveOutput($"测试配置：\n 编码协议：{testProtocol}\n 分辨率：{testResolution}\n 码率：{testBitRate}\n 码率控制：{testBRControl}\n I帧间隔：{testIGop}\n 帧率：{testFps}");



                                // 解码拉流
                                checkBoxDecodeTest.Checked = true;

                                // RTSP 测试
                                if (checkbox_RTSP.Checked)
                                {
                                    if (excelHelper.ReadCell("RTSP测试结果", i) != "" && skipTestContentCheckBox.Checked)
                                    {
                                        LogSaveOutput($"RTSP当前已存在测试结果，跳过该测试！");
                                    }
                                    else
                                    {
                                        networkStreamOnBtn_Click(null, null);
                                        // 等待10秒钟后拍图
                                        await Task.Delay(10000);
                                        string picPath = await SafeSnapshotAsync(player1, testFolder, $"{testProtocol}-{testResolution}-{testBitRate}-{testBRControl}-{testIGop}-{testFps}".Replace("*", "-"));
                                        LogSaveOutput($"RTSP测试截图已保存到：{picPath}");
                                        // 再等30秒后获取帧率码率
                                        await Task.Delay(checkVideoConfigTestStreamStatusWaitingTime);
                                        PlayerStatus status = player1.GetPlayerStatus();

                                        // 关流
                                        networkStreamOffBtn_Click(null, null);
                                        long curBitRate = (long)await player1.GetBitrateAsync(networkUrlInput.Text);
                                        string curFps = status.Fps.ToString();
                                        LogSaveOutput($"RTSP测试获取当前流状态：\n 码率：{curBitRate}\n 帧率：{curFps}\n");

                                        float expectedBitRate = float.Parse(testBitRate);
                                        float expectedFps = float.Parse(testFps);
                                        float nowBitRate = curBitRate;
                                        float nowFps = float.Parse(curFps);

                                        double expectedPassBitRate = expectedBitRate;
                                        if (testBRControl == "固定码率")
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.95;
                                        }
                                        else
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.8;
                                        }

                                        if (WindowsFunc.IsImageValid(picPath))
                                        {
                                            // 图片没问题，再判断码率和帧率是否符合预期 -- 分别固定码率和动态码率两种情况讨论 -
                                            // 码率判断标准：固定码率上下浮动5%，动态码率上下浮动20%

                                            LogSaveOutput($"预期码率：{expectedPassBitRate} kbps\n当前码率：{nowBitRate} kbps\n预期帧率：{expectedFps * 0.9} fps\n当前帧率：{nowFps} fps");

                                            if ((nowBitRate >= expectedPassBitRate) &&
                                                ((nowFps >= expectedFps * 0.9)))
                                            {
                                                testColumnNameRTSP = $"PASS:\n1、图片正常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                            else
                                            {
                                                testColumnNameRTSP = $"FAIL:\n1、图片正常，码率或帧率异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                        }
                                        else
                                        {
                                            testColumnNameRTSP = $"FAIL:\n1、图片异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n 3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                        }

                                        await Task.Delay(1000);

                                        // 写入到Excel
                                        excelHelper.WriteTestResult("RTSP测试结果", i, testColumnNameRTSP);
                                        excelHelper.Save();

                                        LogSaveOutput($"RTSP测试结果已写入Excel - {testColumnNameRTSP}");
                                        
                                    }
                                }
                                else
                                {
                                    LogSaveOutput($"{testUrlName} - 不进行RTSP测试！");
                                    // 写入到Excel
                                    testColumnNameRTSP = "跳过RTSP测试";
                                    excelHelper.WriteTestResult("RTSP测试结果", i, testColumnNameRTSP);
                                    excelHelper.Save();
                                }

                                // RTMP 测试
                                if (checkbox_RTMP.Checked && testProtocol != "H265" && testProtocol != "MJPEG")
                                {
                                    if (excelHelper.ReadCell("RTMP测试结果", i) != "" && skipTestContentCheckBox.Checked)
                                    {
                                        LogSaveOutput($"RTMP当前已存在测试结果，跳过该测试！");
                                    }
                                    else
                                    {
                                        // 先恢复默认RTMP配置，确保测试环境干净
                                        recoverDefaultRTMPConfigBtn_Click(null, null);
                                        await Task.Delay(3000);

                                        // 打开RTMP设置界面，修改RTMP配置为当前测试配置，并且地址设置为input_Rtmp1.Text
                                        changeRTMPConfig(testStreamInput.Text, true, input_Rtmp1.Text, true);
                                        await Task.Delay(3000);
                                        string curTestRTMPUrl = input_Rtmp1.Text;
                                        networkUrlInput.Text = curTestRTMPUrl;
                                        LogSaveOutput($"当前测试RTMP URL已设置为：{curTestRTMPUrl} - 开始拉流测试");
                                        networkStreamOnBtn_Click(null, null);
                                        // 等待10秒钟后拍图
                                        await Task.Delay(10000);
                                        string picPath = await SafeSnapshotAsync(player1, testFolder, $"{testProtocol}-{testResolution}-{testBitRate}-{testBRControl}-{testIGop}-{testFps}".Replace("*", "-"));
                                        LogSaveOutput($"RTMP测试截图已保存到：{picPath}，RTMP拉流和获取码率时间长，请等待{checkVideoConfigTestStreamStatusWaitingTime}");
                                        // 再等30秒后获取帧率码率
                                        await Task.Delay(checkVideoConfigTestStreamStatusWaitingTime);
                                        PlayerStatus status = player1.GetPlayerStatus();
                                        // 关流
                                        networkStreamOffBtn_Click(null, null);
                                        long curBitRate = (long)await player1.GetBitrateAsync(networkUrlInput.Text);
                                        string curFps = status.Fps.ToString();
                                        LogSaveOutput($"RTMP测试获取当前流状态：\n 码率：{curBitRate}\n 帧率：{curFps}\n");

                                        float expectedBitRate = float.Parse(testBitRate);
                                        float expectedFps = float.Parse(testFps);
                                        float nowBitRate = curBitRate;
                                        float nowFps = float.Parse(curFps);

                                        double expectedPassBitRate = expectedBitRate;
                                        if (testBRControl == "固定码率")
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.95;
                                        }
                                        else
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.8;
                                        }

                                        if (WindowsFunc.IsImageValid(picPath))
                                        {
                                            // 图片没问题，再判断码率和帧率是否符合预期 -- 分别固定码率和动态码率两种情况讨论 -
                                            // 码率判断标准：固定码率上下浮动5%，动态码率上下浮动20%

                                            LogSaveOutput($"预期码率：{expectedPassBitRate} kbps\n当前码率：{nowBitRate} kbps\n预期帧率：{expectedFps * 0.9} fps\n当前帧率：{nowFps} fps");

                                            if ((nowBitRate >= expectedPassBitRate) &&
                                                ((nowFps >= expectedFps * 0.9)))
                                            {
                                                testColumnNameRTMP = $"PASS:\n1、图片正常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                            else
                                            {
                                                testColumnNameRTMP = $"FAIL:\n1、图片正常，帧率或码率异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                        }
                                        else
                                        {
                                            testColumnNameRTMP = $"FAIL:\n1、图片异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                        }
                                        await Task.Delay(1000);

                                        // 写入到Excel
                                        excelHelper.WriteTestResult("RTMP测试结果", i, testColumnNameRTMP);
                                        excelHelper.Save();

                                        LogSaveOutput($"RTMP测试结果已写入Excel - {testColumnNameRTMP}");
                                    }


                                }
                                else
                                {
                                    LogSaveOutput($"{testUrlName} - 不进行RTMP测试！");
                                    // 写入到Excel
                                    testColumnNameRTMP = "跳过RTMP测试";
                                    excelHelper.WriteTestResult("RTMP测试结果", i, testColumnNameRTMP);
                                    excelHelper.Save();
                                }


                                // GB2818 测试
                                if (checkbox_GB2818.Checked)
                                {
                                    if (excelHelper.ReadCell("GB2818测试结果", i) != "" && skipTestContentCheckBox.Checked)
                                    {
                                        LogSaveOutput($"GB2818当前已存在测试结果，跳过该测试！");
                                    }
                                    else
                                    {
                                        if (testUrlName == "第一路主码流")
                                        {
                                            testUrl = input_gb2818_1.Text;
                                        }
                                        if (testUrlName == "第二路辅码流")
                                        {
                                            testUrl = input_gb2818_2.Text;
                                        }
                                        if (testUrlName == "第一路特写主码流")
                                        {
                                            testUrl = input_gb2818_1.Text;
                                        }
                                        if (testUrlName == "第二路全景主码流")
                                        {
                                            testUrl = input_gb2818_2.Text;
                                        }
                                        if (testUrlName == "第三路特写辅码流")
                                        {
                                            testUrl = input_gb2818_3.Text;
                                        }
                                        if (testUrlName == "第四路全景辅码流")
                                        {
                                            testUrl = input_gb2818_4.Text;
                                        }

                                        string curTestGB2818Url = testUrl;
                                        networkUrlInput.Text = curTestGB2818Url;
                                        LogSaveOutput($"当前测试GB2818 URL已设置为：{curTestGB2818Url} - 开始拉流测试");
                                        networkStreamOnBtn_Click(null, null);
                                        // 等待10秒钟后拍图
                                        await Task.Delay(10000);
                                        string picPath = await SafeSnapshotAsync(player1, testFolder, $"{testProtocol}-{testResolution}-{testBitRate}-{testBRControl}-{testIGop}-{testFps}".Replace("*", "-"));
                                        LogSaveOutput($"GB2818测试截图已保存到：{picPath}");
                                        // 再等30秒后获取帧率码率
                                        await Task.Delay(checkVideoConfigTestStreamStatusWaitingTime);
                                        PlayerStatus status = player1.GetPlayerStatus();
                                        // 关流
                                        networkStreamOffBtn_Click(null, null);
                                        long curBitRate = (long)await player1.GetBitrateAsync(networkUrlInput.Text);
                                        string curFps = status.Fps.ToString();
                                        LogSaveOutput($"GB2818测试获取当前流状态：\n 码率：{curBitRate}\n 帧率：{curFps}\n");

                                        float expectedBitRate = float.Parse(testBitRate);
                                        float expectedFps = float.Parse(testFps);
                                        float nowBitRate = curBitRate;
                                        float nowFps = float.Parse(curFps);

                                        double expectedPassBitRate = expectedBitRate;
                                        if (testBRControl == "固定码率")
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.95;
                                        }
                                        else
                                        {
                                            expectedPassBitRate = expectedPassBitRate * 0.8;
                                        }

                                        if (WindowsFunc.IsImageValid(picPath))
                                        {
                                            // 图片没问题，再判断码率和帧率是否符合预期 -- 分别固定码率和动态码率两种情况讨论 -
                                            // 码率判断标准：固定码率上下浮动5%，动态码率上下浮动20%

                                            LogSaveOutput($"预期码率：{expectedPassBitRate} kbps\n当前码率：{nowBitRate} kbps\n预期帧率：{expectedFps * 0.9} fps\n当前帧率：{nowFps} fps");

                                            if (((nowBitRate >= expectedPassBitRate)) &&
                                                ((nowFps >= expectedFps * 0.9)))
                                            {
                                                testColumnNameGB2818 = $"PASS:\n1、图片正常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                            else
                                            {
                                                testColumnNameGB2818 = $"FAIL:\n1、图片正常，帧率或码率异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                            }
                                        }
                                        else
                                        {
                                            testColumnNameGB2818 = $"FAIL:\n1、图片异常：{picPath}\n2、期待码率：{expectedPassBitRate} - 码率：{curBitRate} kbps\n3、期待帧率：{expectedFps * 0.9} - 帧率：{curFps} fps";
                                        }
                                        await Task.Delay(1000);

                                        // 写入到Excel
                                        excelHelper.WriteTestResult("GB2818测试结果", i, testColumnNameGB2818);
                                        excelHelper.Save();

                                        LogSaveOutput($"GB2818测试结果已写入Excel - {testColumnNameGB2818}");
                                    }

                                }
                                else
                                {
                                    LogSaveOutput($"{testUrlName} - 不进行GB2818测试！");
                                    // 写入到Excel
                                    testColumnNameGB2818 = "跳过GB2818测试";
                                    excelHelper.WriteTestResult("GB2818测试结果", i, testColumnNameGB2818);
                                    excelHelper.Save();
                                }

                                // Web 测试
                                if (checkbox_Web.Checked)
                                {
                                    if (excelHelper.ReadCell("Web测试结果", i) != "" && skipTestContentCheckBox.Checked)
                                    {
                                        LogSaveOutput($"Web 测试当前已存在测试结果，跳过该测试！");
                                    }
                                    else
                                    {
                                        // 关流 -- 释放网络资源，确保web预览测试环境干净
                                        networkStreamOffBtn_Click(null, null);
                                        await Task.Delay(3000);

                                        bool webIsPlaying = VideoChecker.Check($"http://{textBox_ip.Text}/#/media-preview", $"http://{textBox_ip.Text}/#/media-preview");
                                        AntdUI.Message.success(this, $"Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
                                        LogSaveOutput($"Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");

                                        testColumnNameWeb = webIsPlaying ? "PASS: Web预览成功" : "FAIL: Web预览失败";

                                        // 写入到Excel
                                        excelHelper.WriteTestResult("web测试结果", i, testColumnNameWeb);
                                        excelHelper.Save();

                                        LogSaveOutput($"Web测试结果已写入Excel - {testColumnNameWeb}");
                                    }
                                }
                                else
                                {
                                    LogSaveOutput($"{testUrlName} - 不进行Web测试！");
                                    // 写入到Excel
                                    testColumnNameWeb = "跳过Web测试";
                                    excelHelper.WriteTestResult("Web测试结果", i, testColumnNameWeb);
                                    excelHelper.Save();
                                }


                                if (testColumnNameRTSP.Contains("FAIL") || testColumnNameRTMP.Contains("FAIL") || testColumnNameGB2818.Contains("FAIL") || testColumnNameWeb.Contains("FAIL"))
                                {
                                    failCount++;
                                }
                                else
                                {
                                    passCount++;
                                }

                                failLabel.Text = failCount.ToString();
                                passLabel.Text = passCount.ToString();

                                await Task.Delay(3000);

                            }
                            else
                            {
                                AntdUI.Message.error(this, "跳过当前行，非测试行");
                            }
                            await Task.Delay(3000);
                        }
                        catch (Exception ex)
                        {
                            LogSaveOutput($"测试执行异常：\n {ex.ToString()} -- 跳过第{i}行，继续下一行测试！");
                        }
                    }
                }
            }
        }

        string cur_streamRTMP_config;

        private async void getDefaultRTMPConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(cur_streamRTMP_config = await _api.GetSpecVideoStreamDefaultRTMPConfig());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"获取所有视频RTMP配置异常！\n{ex.ToString()}");
            }
        }

        private async void changeRTMPConfig(string testName, bool enable, string address, bool videoEnabled)
        {
            string stream = "";
            switch (testName)
            {
                case "第一路特写主码流":
                    stream = $"closeUpMain";
                    break;
                case "第二路全景主码流":
                    stream = $"panoramicMain";
                    break;
                case "第三路特写辅码流":
                    stream = $"closeUpSub";
                    break;
                case "第四路全景辅码流":
                    stream = $"panoramicSub";
                    break;
                case "第一路主码流":
                    stream = $"main";
                    break;
                case "第二路辅码流":
                    stream = $"sub";
                    break;
                default:
                    break;
            }

            string rtmpJsonContent = "{\"rtmps\":[{\"index\":0,\"enable\":true,\"address\":\"rtmp://192.168.100.138/live/stream0\",\"stream\":\"sub\",\"videoEnabled\":true,\"audioEnabled\":true},{\"index\":1,\"enable\":true,\"address\":\"rtmp://192.168.100.138/live/stream1\",\"stream\":\"main\",\"videoEnabled\":true,\"audioEnabled\":true},{\"index\":2,\"enable\":true,\"address\":\"rtmp://192.168.100.138/live/stream2\",\"stream\":\"main\",\"videoEnabled\":true,\"audioEnabled\":true},{\"index\":3,\"enable\":true,\"address\":\"rtmp://192.168.100.138/live/stream3\",\"stream\":\"main\",\"videoEnabled\":true,\"audioEnabled\":true}]}";

            var jObj = JObject.Parse(rtmpJsonContent);
            var first = jObj["rtmps"][0];
            first["enable"] = enable;
            first["address"] = address;
            first["stream"] = stream;
            first["videoEnabled"] = videoEnabled;
            rtmpJsonContent = jObj.ToString();


            LogSaveOutput("触发修改RTMP配置……");
            LogSaveOutput(await _api.SetSpecVideoRTMPStreamConfig(rtmpJsonContent));
            LogSaveOutput($"修改RTMP配置：测试项：{testName} - enable: {enable} - address: {address} - videoEnabled: {videoEnabled}");
        }

        private void changeRTMPConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                changeRTMPConfig("第二路全景主码流", true, input_Rtmp1.Text, true);
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改RTMP配置异常！\n{ex.ToString()}");
            }
        }

        private async void recoverDefaultRTMPConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                LogSaveOutput(await _api.ResetSpecVideoStreamRTMPConfig());
            }
            catch (Exception ex)
            {
                LogSaveOutput($"读取全部视频流RTMP默认配置异常！\n{ex.ToString()}");
            }
        }

        private void debugBtn_Click(object sender, EventArgs e)
        {
            LogSaveOutput(player1.GetBitrateDebugInfo());
        }

        private async void checkWebPreviewSuccessBtn_Click(object sender, EventArgs e)
        {

            bool webIsPlaying = VideoChecker.Check($"http://{textBox_ip.Text}/#/media-preview", $"http://{textBox_ip.Text}/#/media-preview");
            Console.WriteLine($"Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            AntdUI.Message.success(this, $"Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            LogSaveOutput($"Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            //int i = 0;
            //while (true)
            //{
            //    i += 1;
            //    // http://10.66.30.241/#/media-preview
            //    bool webIsPlaying = VideoChecker.Check($"http://{textBox_ip.Text}/#/media-preview");
            //    Console.WriteLine($"第{i}次Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            //    AntdUI.Message.success(this, $"第{i}次Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            //    LogSaveOutput($"第{i}次Web预览测试结果：{(webIsPlaying ? "成功" : "失败")} ！");
            //    await Task.Delay(3000);
            //    Console.WriteLine($"再次进行Web预览测试……");
            //    AntdUI.Message.success(this, $"再次进行Web预览测试……");
            //}
        }

        private void initVC51TwoStreamPlayer()
        {
            vc51_player1 = new OpenCvRtspPlayer(pictureBox_VC51_1);
            vc51_player2 = new OpenCvRtspPlayer(pictureBox_VC51_2);
        }

        OpenCvRtspPlayer vc51_player1, vc51_player2;
        private void vc51_1StreamOnBtn_Click(object sender, EventArgs e)
        {
            string url = vc51_1InputStream.Text.Trim();
            vc51_player1.Start(url, checkBoxDecodeTest.Checked);
            LogSaveOutput($"开始拉流: {url}");
        }

        private void vc51_1StreamOffBtn_Click(object sender, EventArgs e)
        {
            vc51_player1.Stop();
            LogSaveOutput($"停止拉流");
        }

        private void vc51_2StreamOnBtn_Click(object sender, EventArgs e)
        {
            string url = vc51_2InputStream.Text.Trim();
            vc51_player2.Start(url, checkBoxDecodeTest.Checked);
            LogSaveOutput($"开始拉流: {url}");
        }

        private void vc51_2StreamOffBtn_Click(object sender, EventArgs e)
        {
            vc51_player2.Stop();
            LogSaveOutput($"停止拉流");
        }

        private async void vc51_changeConfigBtn_Click(object sender, EventArgs e)
        {
            try
            {
                int testFps1 = 30;
                string testResolution1 = "3840x2160";

                int testFps2 = 30;
                string testResolution2 = "1280x720";

                string mainConfig = "{\"stream\":\"main\",\"data\":{\"profile\":\"mainprofile\",\"bitRate\":8192,\"encFmt\":\"H264\",\"gop\":25,\"fps\":25,\"rcMode\":\"CBR\",\"resolution\":\"640x360\"}}";

                string subConfig = "{\"stream\":\"sub\",\"data\":{\"profile\":\"mainprofile\",\"bitRate\":2048,\"encFmt\":\"H264\",\"gop\":25,\"fps\":25,\"rcMode\":\"CBR\",\"resolution\":\"1024x576\"}}";

                var jObj = JObject.Parse(mainConfig);
                var first = jObj["data"];
                first["fps"] = testFps1;
                first["resolution"] = testResolution1;
                mainConfig = jObj.ToString();

                LogSaveOutput(mainConfig);

                var jObj2 = JObject.Parse(subConfig);
                var first2 = jObj2["data"];
                first2["fps"] = testFps2;
                first2["resolution"] = testResolution2;
                subConfig = jObj2.ToString();

                LogSaveOutput(subConfig);

                LogSaveOutput(set_panoramicMain_stream_config_result = await _api.SetSpecVideoStreamConfig_VC51(mainConfig));
                LogSaveOutput(set_panoramicSub_stream_config_result = await _api.SetSpecVideoStreamConfig_VC51(subConfig));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"修改测试配置异常！\n{ex.ToString()}");
            }
        }

        private async void vc51_1minutesOnlyRotate30sBtn_Click(object sender, EventArgs e)
        {

            vc51_1InputStream.Text = $"rtsp://{textBox_ip.Text}/1";
            vc51_2InputStream.Text = $"rtsp://{textBox_ip.Text}/2";

            checkBoxDecodeTest.Checked = true;
            buttonGetToken_Click(null, null);
            await Task.Delay(1000);

            recoverDefaultConfigBtn_Click(null, null);
            await Task.Delay(1000);

            readAllStreamCurConfigBtn_Click(null, null);
            await Task.Delay(1000);

            vc51_changeConfigBtn_Click(null, null);
            await Task.Delay(1000);

            vc51_1StreamOnBtn_Click(null, null);
            vc51_2StreamOnBtn_Click(null, null);
            int i = 0;
            try
            {
                while (true)
                {
                    i += 1;
                    VC51_CountLabel.Text = $"第{i}轮测试";
                    if (stopTest)
                    {
                        LogSaveOutput("停止测试");
                        break;
                    }
                    await Task.Run(async () =>
                    {
                        LogSaveOutput("开始旋转 - 旋转30秒");
                        ptzGoHomeBtn_Click(null, null);
                        await Task.Delay(3000);
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 旋转30秒";

                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 左转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 左转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 上转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 右转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 右转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 下转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 左转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 左转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 上转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 开始旋转 - 右转3秒";
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "right"));
                        LogSaveOutput($"第{i}轮测试 -- 开始旋转 - 右转3秒");
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "down"));
                        LogSaveOutput($"第{i}轮测试 -- 开始旋转 - 下转3秒");
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        LogSaveOutput($"第{i}轮测试 -- 开始旋转 - 左转3秒");
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "left"));
                        LogSaveOutput($"第{i}轮测试 -- 开始旋转 - 左转3秒");
                        await Task.Delay(3000);
                        LogSaveOutput(await _api.SetPtzControlAction(1, 5, "top"));
                        LogSaveOutput($"第{i}轮测试 -- 开始旋转 - 上转3秒");
                        await Task.Delay(3000);

                        LogSaveOutput("停止旋转 - 等待30秒");
                        VC51_CountLabel.Text = $"第{i}轮测试 -- 停止旋转 - 等待30秒";
                        await Task.Delay(30000);
                    });
                }
            }
            catch (Exception ex)
            {
                LogSaveOutput($"机械云台控制异常！\n{ex.ToString()}");
            }


        }

        private async void getDiskStatusBtn_Click(object sender, EventArgs e)
        {
            try
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this, "分区状态提示", $"当前分区状态：{await _api.GetDiskStatus()}", AntdUI.TType.Info));
            }
            catch (Exception ex)
            {
                LogSaveOutput($"当前设备分区状态获取异常！\n{ex.ToString()}");
            }
        }
    }
}
#endregion