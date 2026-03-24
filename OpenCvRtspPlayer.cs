/*
 * 基于OpenCV的RTSP/FLV视频流播放器类
 * 提供RTSP/HTTP-FLV流媒体播放、帧抓取、性能监控等功能
 */
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace skdl_new_2025_test_tool
{

    /// 准确码率要通过
    /***
     * 先停止拉流，再获取码率，确保获取到的码率是当前流的实际码率，而不是之前流的残留数据。
    1. 启动拉流: player.Start(rtspUrl)
    2. 停止拉流: player.Stop()  
    3. 获取码率: await player.GetBitrateAsync(rtspUrl)
     */




    /// <summary>
    /// 播放器状态信息结构体
    /// </summary>
    public struct PlayerStatus
    {
        public float Fps;                 // 当前帧率
        public float TotalBitrateKbps;   // 总比特率(Kbps)
        public float CpuUsage;           // CPU使用率(%)
        public bool IsPlaying;           // 是否正在播放
        public string BackendName;       // 后端名称
    }

    /// <summary>
    /// OpenCV RTSP/FLV播放器实现类
    /// </summary>
    public class OpenCvRtspPlayer : IDisposable
    {
        #region 私有字段
        private VideoCapture _capture;           // OpenCV视频捕获对象
        private VideoCapture _flvCapture;        // FLV流专用捕获对象
        private Process _ffmpegProcess;          // FFmpeg进程（用于HTTP FLV流）
        private CancellationTokenSource _cts;    // 取消令牌源，用于控制播放线程
        private readonly PictureBox _pictureBox; // 显示视频的PictureBox控件
        private Thread _playThread;              // 播放线程
        private bool _useFFmpegProcess = false;  // 是否使用FFmpeg进程模式

        // UI 刷新间隔 (毫秒)
        private const int UI_INTERVAL_MS = 66; // 约15fps

        private volatile bool _requestSnapshot = false; // 截图请求标志
        private Mat _snapshotBuffer = null;            // 截图缓冲区
        private readonly object _snapshotLock = new object(); // 截图缓冲区锁
        private readonly ManualResetEventSlim _snapshotEvent = new ManualResetEventSlim(false);
        private readonly object _lifecycleLock = new object(); // 生命周期锁
        private readonly object _captureLock = new object();   // Capture访问锁
        private volatile bool _isRendering = false;    // 是否正在渲染UI

        private volatile float _currentFps = 0;        // 当前帧率
        private string _backendInfo = "Init";          // 后端信息
        public bool IsPlaying { get; private set; }    // 播放状态属性

        // 实时码率监控相关字段
        private long _startTime = 0;
        private long _lastBitrateUpdateTime = 0;
        private volatile float _currentBitrateKbps = 0; // 当前流码率(Kbps)
        private volatile float _rawBitrateKbps = 0;     // 原始码率(无平滑)
        private float _lastValidBitrateKbps = 0;        // 上次有效码率(防止为0)
        private readonly object _bitrateLock = new object();
        private float _smoothedFps = 0;                 // 平滑后的帧率
        private float _lastValidFps = 0;                // 上次有效帧率(防止为0)
        private long _fpsSampleStartTime = 0;           // 帧率采样开始时间
        private int _fpsSampleFrameCount = 0;           // 采样周期内的帧数
        private double _streamFpsFromMeta = 0;          // 流元数据中的帧率
        private bool _bitrateFromProbe = false;         // 码率是否来自FFprobe探测

        // FFprobe实时码率监控
        private CancellationTokenSource _bitrateMonitorCts;
        private string _currentRtspUrl = "";
        private int _bitrateMonitorConsecutiveFailures = 0;
        private const int MAX_BITRATE_MONITOR_FAILURES = 20;
        private long _lastMemoryCheckTick = 0;
        private long _memoryLeakSampleCount = 0;

        // 诊断日志相关
        private static readonly object _diagLogLock = new object();
        private static string _diagLogFile = "";
        private static List<string> _diagLogBuffer = new List<string>();
        private const int DIAG_LOG_FLUSH_INTERVAL = 5000;
        private long _lastDiagLogFlush = 0;

        /// <summary>
        /// 手动设置码率（当FFprobe无法获取时使用，单位Kbps）
        /// </summary>
        public float ManualBitrateKbps { get; set; } = 0;

        private static string _ffprobePath = "ffprobe.exe";
        private static string _lastProbeOutput = "";
        private static string _lastProbeError = "";
        private string _lastFFmpegError = "";
        private string _lastFFmpegOutput = "";

        private static readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(50, 50);
        private static PerformanceCounter _cpuCounter;
        private static System.Windows.Forms.Timer _statTimer;
        private static volatile float _globalCpuUsage = 0;
        private static readonly object _staticInitLock = new object();

        // FFmpeg进程模式临时文件
        private static string _tempFlvFile = "";
        private long _lastFilePosition = 0;
        private bool _flvCaptureReady = false;
        #endregion

        #region 静态构造函数 (配置 FFMPEG)
        static OpenCvRtspPlayer()
        {
            try
            {
                // 默认RTSP选项，具体在连接时动态调整
                string options = "rtsp_transport;tcp|stimeout;5000000|buffer_size;2048000";
                Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", options);
            }
            catch { }
        }

        private static string GetCaptureOptions(string url)
        {
            bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool isFlv = url.Contains(".flv", StringComparison.OrdinalIgnoreCase) ||
                         url.Contains("flv", StringComparison.OrdinalIgnoreCase);

            if (isHttp || isFlv)
            {
                // HTTP/FLV 流需要更长的超时和缓冲区
                return "rtsp_transport;tcp|stimeout;15000000|buffer_size;8192000|reconnect;1|reconnect_streamed;1|reconnect_delay_max;5000|http_seekable;0|multiple_requests;1|flush_packets;1";
            }
            return "rtsp_transport;tcp|stimeout;10000000|buffer_size;4096000|fifo_size;1000000";
        }
        #endregion

        #region 构造函数
        public OpenCvRtspPlayer(PictureBox pictureBox)
        {
            _pictureBox = pictureBox ?? throw new ArgumentNullException(nameof(pictureBox));
            _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            InitializeGlobalCounters();
        }

        private static void InitializeGlobalCounters()
        {
            lock (_staticInitLock)
            {
                if (_cpuCounter == null)
                {
                    try
                    {
                        string processName = Process.GetCurrentProcess().ProcessName;
                        _cpuCounter = new PerformanceCounter("Process", "% Processor Time", processName);
                        _cpuCounter.NextValue();
                    }
                    catch { _cpuCounter = null; }
                }
                if (_statTimer == null)
                {
                    try
                    {
                        _statTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                        _statTimer.Tick += (s, e) =>
                        {
                            try
                            {
                                if (_cpuCounter != null)
                                    _globalCpuUsage = _cpuCounter.NextValue() / Environment.ProcessorCount;
                            }
                            catch { }
                        };
                        _statTimer.Start();
                    }
                    catch { }
                }
            }
        }
        #endregion

        #region 公共方法
        public PlayerStatus GetPlayerStatus()
        {
            float finalBitrate = 0;
            lock (_bitrateLock)
            {
                if (_currentBitrateKbps > 0)
                {
                    finalBitrate = _currentBitrateKbps;
                }
                else if (_lastValidBitrateKbps > 0)
                {
                    finalBitrate = _lastValidBitrateKbps;
                }
                else if (ManualBitrateKbps > 0)
                {
                    finalBitrate = ManualBitrateKbps;
                }
            }

            return new PlayerStatus
            {
                IsPlaying = this.IsPlaying,
                Fps = _lastValidFps > 0 ? (float)Math.Round(_currentFps, 1) : _lastValidFps,
                CpuUsage = (float)(_globalCpuUsage > 0 ? Math.Round(_globalCpuUsage, 1) : 1),
                TotalBitrateKbps = (float)Math.Round(finalBitrate, 2) > 0 ? (float)Math.Round(finalBitrate, 2) : 1,
                BackendName = _backendInfo
            };
        }

        public void SetManualBitrate(float bitrateKbps)
        {
            if (bitrateKbps > 0)
            {
                lock (_bitrateLock)
                {
                    _rawBitrateKbps = bitrateKbps;
                    _currentBitrateKbps = bitrateKbps;
                    _lastValidBitrateKbps = bitrateKbps;
                    _bitrateFromProbe = true;
                }
            }
        }

        public async Task<long?> GetBitrateAsync(string url)
        {
            return await Task.Run(() =>
            {
                try
                {
                    DiagLog($"GetBitrateAsync START: {url}");
                    KillOtherFfmpegProcesses();
                    Thread.Sleep(200);

                    string ffmpegDir = Path.GetDirectoryName(_ffprobePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                    string ffmpegPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
                    if (!File.Exists(ffmpegPath)) ffmpegPath = "ffmpeg.exe";

                    int DURATION = 20;
                    bool isRtmp = url.Contains("rtmp", StringComparison.OrdinalIgnoreCase);
                    bool isFlv = url.Contains(".flv", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://") || url.StartsWith("https://");
                    double OVERHEAD_RATIO = isRtmp ? 1.05 : (isFlv ? 1.02 : 1.15);

                    string arguments;
                    if (isRtmp)
                        arguments = $"-i \"{url}\" -t {DURATION} -c:v copy -f null -";
                    else if (isFlv)
                        arguments = $"-fflags discardcorrupt -i \"{url}\" -t {DURATION} -c:v copy -f null -";
                    else
                        arguments = $"-rtsp_transport tcp -i \"{url}\" -t {DURATION} -c:v copy -f null -";

                    var outputBuilder = new System.Text.StringBuilder();
                    var errorBuilder = new System.Text.StringBuilder();

                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();
                    DiagLog($"GetBitrateAsync: ffmpeg started, PID: {process.Id}");

                    process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool exited = process.WaitForExit((DURATION + 25) * 1000);
                    if (!exited)
                    {
                        DiagLog("GetBitrateAsync: ffmpeg timeout, killing");
                        process.Kill(entireProcessTree: true);
                    }

                    Thread.Sleep(500);

                    string errorOutput = errorBuilder.ToString();
                    if (string.IsNullOrEmpty(errorOutput))
                        errorOutput = outputBuilder.ToString();

                    _lastFFmpegOutput = errorOutput;

                    var bitrate = ExtractVideoSizeFromFfmpeg(errorOutput, DURATION, OVERHEAD_RATIO);
                    if (bitrate.HasValue && bitrate.Value > 0)
                    {
                        lock (_bitrateLock)
                        {
                            _rawBitrateKbps = bitrate.Value;
                            _currentBitrateKbps = bitrate.Value;
                            _lastValidBitrateKbps = bitrate.Value;
                        }
                        _lastFFmpegError = $"Bitrate: {bitrate.Value} kbps";
                        DiagLog($"GetBitrateAsync: SUCCESS {bitrate.Value}k");
                    }
                    else
                    {
                        _lastFFmpegError = $"Parse failed, output: {(errorOutput.Length > 200 ? errorOutput.Substring(0, 200) : errorOutput)}";
                        DiagLog($"GetBitrateAsync: PARSE FAILED - {errorOutput.Substring(0, Math.Min(100, errorOutput.Length))}");
                    }

                    return bitrate;
                }
                catch (Exception ex)
                {
                    _lastFFmpegError = $"GetBitrate error: {ex.Message}";
                    DiagLog($"GetBitrateAsync: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            });
        }

        public void RefreshBitrate(string rtspUrl)
        {
            _currentRtspUrl = rtspUrl;
            _ = GetBitrateAsync(rtspUrl);
        }

        public string GetBitrateDebugInfo()
        {
            return $"[FFprobe Output]\n{_lastProbeOutput}\n\n[FFmpeg Output]\n{_lastFFmpegOutput}\n\n[FFmpeg Process Error]\n{_lastFFmpegError}\n\n[Parsed Values]\n_rawBitrateKbps: {_rawBitrateKbps}\n_currentBitrateKbps: {_currentBitrateKbps}\n_bitrateFromProbe: {_bitrateFromProbe}";
        }

        public async Task<string> DiagnoseStreamAsync(string url)
        {
            string result = $"URL: {url}\n\n";
            try
            {
                string arguments = $"-v quiet -print_format json -show_streams -show_format \"{url}\"";

                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                result += $"[FFprobe Output]\n{output}\n\n[FFprobe Error]\n{error}";
            }
            catch (Exception ex)
            {
                result += $"Error: {ex.Message}";
            }
            return result;
        }

        public void Start(string rtspUrl, bool decodeFrames = true)
        {
            lock (_lifecycleLock)
            {
                if (IsPlaying) Stop();
                _cts = new CancellationTokenSource();
                IsPlaying = true;
                _currentFps = 0;
                _isRendering = false;
                _lastFFmpegError = $"Start called with URL: {rtspUrl}";

                // 规范化URL（保留端口）
                try
                {
                    Uri uri = new Uri(rtspUrl);
                    int port = uri.Port;
                    string portStr = (port > 0 && port != 80 && port != 443) ? $":{port}" : "";
                    rtspUrl = $"{uri.Scheme}://{uri.Host}{portStr}{uri.AbsolutePath}";
                }
                catch { /* 保留原URL */ }

                _lastFFmpegError = $"Normalized URL: {rtspUrl}";

                _bitrateMonitorConsecutiveFailures = 0;
                _playThread = new Thread(() => PlayLoop(rtspUrl, decodeFrames, _cts.Token));
                _playThread.IsBackground = true;
                _playThread.Priority = ThreadPriority.AboveNormal;
                _playThread.Start();
            }
        }

        public void Stop()
        {
            DiagLog($"Stop called, IsPlaying={IsPlaying}");
            LogMemoryStatus("Stop Start");
            
            try
            {
                lock (_lifecycleLock)
                {
                    if (!IsPlaying) 
                    {
                        DiagLog("Stop: Already stopped");
                        return;
                    }
                    IsPlaying = false;
                    
                    DiagLog("Stop: Cancelling _cts");
                    _cts?.Cancel();

                    DiagLog("Stop: Cancelling _bitrateMonitorCts");
                    _bitrateMonitorCts?.Cancel();
                    _bitrateMonitorCts?.Dispose();
                    _bitrateMonitorCts = null;

                    _cts?.Cancel();
                    Thread.Sleep(100);

                    if (_playThread != null && _playThread.IsAlive)
                    {
                        _playThread.Join(1500);
                        if (_playThread.IsAlive)
                        {
                            DiagLog("Stop: Thread not responding, aborting");
                            try { _playThread.Abort(); } catch { }
                        }
                    }

                    lock (_captureLock)
                    {
                        try { if (_capture != null && _capture.IsOpened()) _capture.Release(); } catch { }
                        try { _capture?.Dispose(); } catch { }
                        _capture = null;
                        try { if (_flvCapture != null && _flvCapture.IsOpened()) _flvCapture.Release(); } catch { }
                        try { _flvCapture?.Dispose(); } catch { }
                        _flvCapture = null;
                    }

                    if (!_pictureBox.IsDisposed)
                    {
                        try
                        {
                            if (_pictureBox.IsHandleCreated)
                            {
                                _pictureBox.Invoke(new Action(() =>
                                {
                                    _pictureBox.Image?.Dispose();
                                    _pictureBox.Image = null;
                                }));
                            }
                        }
                        catch { }
                    }

                    _currentFps = 0;
                    _smoothedFps = 0;
                    _lastValidFps = 0;
                    _bitrateMonitorConsecutiveFailures = 0;
                    _memoryLeakSampleCount = 0;
                    lock (_bitrateLock)
                    {
                        _currentBitrateKbps = 0;
                        _rawBitrateKbps = 0;
                        _lastValidBitrateKbps = 0;
                        _startTime = 0;
                        _bitrateFromProbe = false;
                    }

                    // 清理FFmpeg进程和临时文件
                    try { _ffmpegProcess?.Kill(entireProcessTree: true); } catch { }
                    _ffmpegProcess?.Dispose();
                    _ffmpegProcess = null;
                    if (!string.IsNullOrEmpty(_tempFlvFile) && File.Exists(_tempFlvFile))
                    {
                        try { File.Delete(_tempFlvFile); } catch { }
                        _tempFlvFile = "";
                    }
                    _snapshotBuffer?.Dispose();
                    _snapshotBuffer = null;
                    lock (_snapshotLock)
                    {
                        _requestSnapshot = false;
                    }
                    
                    DiagLog("Stop: Cleanup complete");
                    LogMemoryStatus("Stop End");
                    FlushDiagLog();
                }
            }
            catch (SocketException ex) when (ex.ErrorCode == 995)
            {
                DiagLog("Stop: SocketException 995 (expected during cleanup)");
            }
            catch (Exception ex)
            {
                DiagLog($"Stop: Exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        public bool Snapshot(string savePath)
        {
            if (!IsPlaying) return false;
            Mat localFrame = null;
            bool lockTaken = false;
            try
            {
                Monitor.Enter(_snapshotLock, ref lockTaken);
                _snapshotEvent.Reset();
                _requestSnapshot = true;
                Monitor.Exit(_snapshotLock);
                lockTaken = false;

                if (!_snapshotEvent.Wait(TimeSpan.FromSeconds(2)))
                {
                    return false;
                }

                lock (_snapshotLock)
                {
                    if (_snapshotBuffer != null)
                    {
                        try
                        {
                            if (!_snapshotBuffer.Empty())
                            {
                                localFrame = _snapshotBuffer;
                                _snapshotBuffer = null;
                            }
                        }
                        catch
                        {
                            _snapshotBuffer?.Dispose();
                            _snapshotBuffer = null;
                        }
                    }
                }
                if (localFrame != null)
                {
                    string dir = Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    bool ret = false;
                    try { ret = localFrame.SaveImage(savePath, new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)); } catch { }
                    localFrame.Dispose();
                    return ret;
                }
            }
            catch { }
            finally
            {
                lock (_snapshotLock)
                {
                    _requestSnapshot = false;
                }
                if (lockTaken) Monitor.Exit(_snapshotLock);
            }
            return false;
        }

        public void Dispose()
        {
            DiagLog("Dispose called");
            FlushDiagLog();
            Stop();
            _cts?.Dispose();
        }

        public string GetDiagLogPath()
        {
            InitDiagLog();
            return _diagLogFile;
        }

        public void ForceGarbageCollection()
        {
            DiagLog("ForceGC: Before GC");
            LogMemoryStatus("ForceGC Before");
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            LogMemoryStatus("ForceGC After");
            DiagLog("ForceGC: After GC");
            FlushDiagLog();
        }

        public void ResetDiagnostics()
        {
            FlushDiagLog();
            _diagLogBuffer.Clear();
            DiagLog($"=== Diag Log Reset ===");
            LogMemoryStatus("ResetDiag");
        }
        #endregion

        #region 私有辅助方法
        private long GetNetworkBytesReceived()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                long totalBytes = 0;
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var stats = ni.GetIPv4Statistics();
                        totalBytes += stats.BytesReceived;
                    }
                }
                return totalBytes;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsSocketAbort(SocketException ex)
        {
            return ex != null && ex.ErrorCode == 995;
        }

        private void InitDiagLog()
        {
            try
            {
                if (string.IsNullOrEmpty(_diagLogFile))
                {
                    string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                    _diagLogFile = Path.Combine(logDir, $"diag_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    DiagLog($"=== Diag Log Started: {_diagLogFile} ===");
                    DiagLog($"Process ID: {Environment.ProcessId}");
                    DiagLog($"Thread Count: {Environment.ProcessorCount}");
                }
            }
            catch { }
        }

        public static void DiagLog(string message)
        {
            try
            {
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId}] {message}";
                lock (_diagLogLock)
                {
                    _diagLogBuffer.Add(logLine);
                    if (_diagLogBuffer.Count >= 50 || string.IsNullOrEmpty(_diagLogFile))
                    {
                        FlushDiagLog();
                    }
                }
            }
            catch { }
        }

        public static void FlushDiagLog()
        {
            if (string.IsNullOrEmpty(_diagLogFile) || _diagLogBuffer.Count == 0) return;
            try
            {
                File.AppendAllLines(_diagLogFile, _diagLogBuffer);
                _diagLogBuffer.Clear();
            }
            catch { }
        }

        private void LogMemoryStatus(string context)
        {
            try
            {
                long workingSet = Process.GetCurrentProcess().WorkingSet64;
                long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
                long mbUsed = workingSet / (1024 * 1024);
                long mbPrivate = privateBytes / (1024 * 1024);
                int threadCount = Process.GetCurrentProcess().Threads.Count;
                DiagLog($"MEM [{context}]: WS={mbUsed}MB, Private={mbPrivate}MB, Threads={threadCount}");
            }
            catch { }
        }

        /// <summary>
        /// 启动FFmpeg实时码率监控（使用ffmpeg计算平均码率）
        /// </summary>
        /// <summary>
        /// 杀掉系统中其他ffmpeg进程，避免资源竞争
        /// </summary>
        private void KillOtherFfmpegProcesses()
        {
            try
            {
                var killProcess = new Process();
                killProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/F /IM ffmpeg.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                killProcess.Start();
                killProcess.WaitForExit(2000);
                killProcess.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// 从ffmpeg输出中提取视频大小并计算码率
        /// </summary>
        private long? ExtractVideoSizeFromFfmpeg(string output, int duration, double overheadRatio)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(output))
                    return null;

                long videoKb = 0;
                double actualTimeSec = duration;

                // 优先匹配 "video:1234kB" 格式
                var videoMatch = System.Text.RegularExpressions.Regex.Match(output, @"video:(\d+)kB");
                if (videoMatch.Success)
                {
                    videoKb = long.Parse(videoMatch.Groups[1].Value);
                }

                // 如果没匹配到，尝试 "size=1234kB" 格式（RTMP常用）
                if (videoKb <= 0)
                {
                    var sizeMatch = System.Text.RegularExpressions.Regex.Match(output, @"size\s*(\d+)kB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (sizeMatch.Success)
                    {
                        videoKb = long.Parse(sizeMatch.Groups[1].Value);
                    }
                }

                // RTMP可能使用KB而不是kB
                if (videoKb <= 0)
                {
                    var sizeMatchKB = System.Text.RegularExpressions.Regex.Match(output, @"size\s*(\d+)KB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (sizeMatchKB.Success)
                    {
                        videoKb = long.Parse(sizeMatchKB.Groups[1].Value);
                    }
                }

                if (videoKb <= 0)
                    return null;

                int frameCount = 0;
                var frameMatch = System.Text.RegularExpressions.Regex.Match(output, @"frame\s*=\s*(\d+)");
                if (frameMatch.Success)
                {
                    int.TryParse(frameMatch.Groups[1].Value, out frameCount);
                }

                var timeMatches = System.Text.RegularExpressions.Regex.Matches(output, @"time=(\d+):(\d+):(\d+\.?\d*)");
                if (timeMatches.Count > 0)
                {
                    var lastMatch = timeMatches[timeMatches.Count - 1];
                    int hours = 0, minutes = 0;
                    double seconds = 0;

                    int.TryParse(lastMatch.Groups[1].Value, out hours);
                    int.TryParse(lastMatch.Groups[2].Value, out minutes);
                    double.TryParse(lastMatch.Groups[3].Value, out seconds);

                    actualTimeSec = hours * 3600 + minutes * 60 + seconds;
                }

                if (actualTimeSec < 0.5)
                    actualTimeSec = duration;

                if (videoKb <= 0)
                {
                    _lastFFmpegError = $"No video data found: video={videoKb}kB";
                    return null;
                }

                long bitrate = (long)((videoKb * 8.0) / actualTimeSec * overheadRatio);
                if (bitrate <= 0)
                {
                    _lastFFmpegError = $"Invalid bitrate calc: video={videoKb}kB time={actualTimeSec:F2}s";
                    return null;
                }

                _lastFFmpegError = $"FFmpeg bitrate calc: video={videoKb}kB time={actualTimeSec:F2}s ratio={overheadRatio} result={bitrate}kbps frame={frameCount}";
                return bitrate;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 尝试使用FFmpeg进程方式启动HTTP FLV流
        /// </summary>
        private bool TryStartFFmpegProcess(string url)
        {
            try
            {
                _ffmpegProcess?.Dispose();

                // 查找ffmpeg.exe
                string[] ffmpegPaths = new[]
                {
                    "ffmpeg.exe",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                    @"C:\ffmpeg\bin\ffmpeg.exe",
                    @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
                };
                string ffmpegExe = ffmpegPaths.FirstOrDefault(File.Exists) ?? "ffmpeg.exe";

                string tempDir = Path.GetTempPath();
                _tempFlvFile = Path.Combine(tempDir, $"flv_stream_{Guid.NewGuid():N}.flv");

                string arguments = $"-fflags discardcorrupt -i \"{url}\" -c copy -f flv \"{_tempFlvFile}\"";
                _lastFFmpegError = $"Trying ffmpeg: {ffmpegExe}\nArgs: {arguments}";

                _ffmpegProcess = new Process();
                _ffmpegProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                bool started = _ffmpegProcess.Start();
                _lastFFmpegError += $"\nProcess started: {started}";

                // 等待文件创建
                Thread.Sleep(2000);

                bool fileExists = File.Exists(_tempFlvFile);
                long fileSize = fileExists ? new FileInfo(_tempFlvFile).Length : 0;

                if (fileExists && fileSize > 1000 && !_ffmpegProcess.HasExited)
                {
                    _backendInfo = "FLV-File";
                    return true;
                }
                else if (_ffmpegProcess.HasExited)
                {
                    _lastFFmpegError = $"FFmpeg exited early. File: {fileExists}, Size: {fileSize}";
                }

                // 清理失败进程
                try { _ffmpegProcess.Kill(entireProcessTree: true); } catch { }
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
                if (File.Exists(_tempFlvFile))
                {
                    try { File.Delete(_tempFlvFile); } catch { }
                    _tempFlvFile = "";
                }
            }
            catch (Exception ex)
            {
                _lastFFmpegError = $"Exception: {ex.Message}";
            }
            return false;
        }

        /// <summary>
        /// 从临时文件读取帧（FFmpeg进程模式）
        /// </summary>
        private Mat ReadFrameFromFFmpeg(int width, int height)
        {
            if (string.IsNullOrEmpty(_tempFlvFile) || !File.Exists(_tempFlvFile))
                return null;

            if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
                return null;

            try
            {
                var fileInfo = new FileInfo(_tempFlvFile);
                long fileSize = fileInfo.Length;
                if (fileSize < 10240) // 至少10KB再尝试打开
                    return null;

                // 首次打开或重新打开
                if (_flvCapture == null)
                {
                    try { _flvCapture?.Dispose(); } catch { }
                    Thread.Sleep(500);
                    _flvCapture = new VideoCapture(_tempFlvFile, VideoCaptureAPIs.FFMPEG);
                }

                try
                {
                    if (!_flvCapture.IsOpened())
                    {
                        try { _flvCapture?.Dispose(); } catch { }
                        _flvCapture = null;
                        return null;
                    }
                }
                catch
                {
                    try { _flvCapture?.Dispose(); } catch { }
                    _flvCapture = null;
                    return null;
                }

                Mat frame = new Mat();
                try
                {
                    if (_flvCapture.Read(frame) && !frame.Empty())
                    {
                        _lastFilePosition = fileSize;
                        return frame;
                    }
                    frame.Dispose();
                }
                catch
                {
                    try { frame?.Dispose(); } catch { }
                    try { _flvCapture?.Dispose(); } catch { }
                    _flvCapture = null;
                    return null;
                }

                return null;
            }
            catch
            {
                try { _flvCapture?.Dispose(); } catch { }
                _flvCapture = null;
                return null;
            }
        }

        private bool TryReopenCapture(string url, CancellationToken token, int waitMs)
        {
            bool semAcquired = false;
            try
            {
                if (_connectionSemaphore.Wait(waitMs, token))
                {
                    semAcquired = true;
                    lock (_captureLock)
                    {
                        try { _capture?.Dispose(); } catch { }
                        _capture = null;

                        string options = GetCaptureOptions(url);
                        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", options);

                        try
                        {
                            _capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);
                            if (_capture.IsOpened())
                            {
                                _capture.Set(VideoCaptureProperties.BufferSize, 8);
                                return true;
                            }
                        }
                        catch
                        {
                            try { _capture?.Dispose(); } catch { }
                            _capture = null;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (semAcquired)
                {
                    try { _connectionSemaphore.Release(); } catch { }
                }
            }
            return false;
        }

        private void PlayLoop(string url, bool decodeFrames, CancellationToken token)
        {
            // 错峰启动
            Thread.Sleep(Math.Abs(GetHashCode()) % 300);

            InitDiagLog();
            DiagLog($"=== PlayLoop START: URL={url}, decodeFrames={decodeFrames} ===");
            LogMemoryStatus("PlayLoop Start");

            _capture = null;
            _flvCapture = null;
            int retryCount = 0;
            long lastUiUpdateMs = 0;
            _isRendering = false;
            _lastMemoryCheckTick = 0;
            _memoryLeakSampleCount = 0;

            using (Mat currentFrame = new Mat())
            using (Mat smallFrame = new Mat())
            {
                bool isFlv = url.Contains(".flv", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("flv", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("rtmp", StringComparison.OrdinalIgnoreCase) ||
                             url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                // 对于HTTP FLV流和RTMP流，先用ffprobe探测（可选）
                if (isFlv || url.Contains("rtmp", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var probeTask = Task.Run(async () =>
                        {
                            using var probe = new Process();
                            probe.StartInfo = new ProcessStartInfo
                            {
                                FileName = _ffprobePath,
                                Arguments = $"-v quiet -print_format json -show_streams \"{url}\"",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            probe.Start();
                            var output = await probe.StandardOutput.ReadToEndAsync();
                            await probe.WaitForExitAsync();
                            return output;
                        });

                        if (probeTask.Wait(5000))
                        {
                            _lastProbeOutput = probeTask.Result;
                        }
                    }
                    catch { }
                }

                try
                {
                    // ========== 1. 初始连接 ==========
                    bool isConnected = false;
                    bool useProcessMode = false;

                    // 传统VideoCapture方式（优先，直接打开FLV流）
                    while (!token.IsCancellationRequested && IsPlaying)
                    {
                        if (TryReopenCapture(url, token, 10000))
                        {
                            _backendInfo = isFlv ? "FLVStream" : "StreamOnly";
                            isConnected = true;
                            _useFFmpegProcess = false;
                        }

                        if (isConnected) break;

                        retryCount++;
                        if (retryCount > 10) { IsPlaying = false; return; }
                        Thread.Sleep(1000);
                    }

                    if (!isConnected) return;

                    // 连接成功后立即用ffprobe获取流信息（优先获取配置码率）
                    var ffprobeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    ffprobeCts.CancelAfter(15000);
                    Task.Run(async () =>
                    {
                        try
                        {
                            DiagLog($"FFprobe task started for URL: {url}");
                            bool isRtmp = url.Contains("rtmp", StringComparison.OrdinalIgnoreCase);
                            using var probe = new Process();
                            string probeArgs;

                            // RTMP流需要特殊参数：增加缓冲和超时
                            if (isRtmp)
                            {
                                probeArgs = $"-rtmp_live live -fflags flush_packets -rw_timeout 20000000 -buffer_size 4096000 -i \"{url}\" -v quiet -print_format json -show_streams -show_format";
                            }
                            else
                            {
                                probeArgs = $"-v quiet -print_format json -show_streams -show_format \"{url}\"";
                            }

                            probe.StartInfo = new ProcessStartInfo
                            {
                                FileName = _ffprobePath,
                                Arguments = probeArgs,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            probe.Start();
                            DiagLog($"FFprobe process started, PID: {probe.Id}");

                            try { await probe.WaitForExitAsync(ffprobeCts.Token); } catch (OperationCanceledException) { DiagLog("FFprobe cancelled"); }
                            catch { }

                            if (ffprobeCts.Token.IsCancellationRequested)
                            {
                                DiagLog("FFprobe cancelled before completion");
                                try { probe.Kill(entireProcessTree: true); } catch { }
                                return;
                            }

                            // 确保进程结束
                            if (!probe.HasExited)
                            {
                                try { probe.Kill(entireProcessTree: true); } catch { }
                                try { await Task.Run(() => probe.WaitForExit(3000)); } catch { }
                            }

                            string output = "", error = "";
                            try { output = probe.StandardOutput.ReadToEnd(); } catch { }
                            try { error = probe.StandardError.ReadToEnd(); } catch { }
                            DiagLog($"FFprobe completed. Output length: {output?.Length ?? 0}");

                            // 检查输出是否有效（如果流配置改变，可能返回空或错误）
                            if (string.IsNullOrWhiteSpace(output) || output.Contains("Invalid data found") || output.Contains("Server returned"))
                            {
                                _lastFFmpegError = $"FFprobe returned invalid/empty output. Will use ManualBitrate: {ManualBitrateKbps}k";
                                DiagLog($"FFprobe invalid output: {output?.Substring(0, Math.Min(200, output?.Length ?? 0))}");
                                
                                // 如果ffprobe失败但有手动码率，使用手动码率
                                if (ManualBitrateKbps > 0)
                                {
                                    lock (_bitrateLock)
                                    {
                                        _rawBitrateKbps = ManualBitrateKbps;
                                        _currentBitrateKbps = ManualBitrateKbps;
                                        _lastValidBitrateKbps = ManualBitrateKbps;
                                    }
                                }
                                return;
                            }

                            // 保存ffprobe输出用于调试
                            _lastProbeOutput = output;
                            _lastProbeError = error;
                            _lastFFmpegError = "BitrateMonitor: FFprobe completed, preparing to start";

                            // 优先解析流的bit_rate（编码器配置码率）
                            long? configuredBitrate = null;
                            var bitrateMatches = System.Text.RegularExpressions.Regex.Matches(output, "\"bit_rate\":\\s*(\\d+)");
                            foreach (System.Text.RegularExpressions.Match m in bitrateMatches)
                            {
                                if (long.TryParse(m.Groups[1].Value, out long br) && br > 0)
                                {
                                    configuredBitrate = br / 1000;
                                    _lastFFmpegError += $"\nFound bit_rate: {br} -> {configuredBitrate}k";
                                    break;
                                }
                            }

                            // 如果没有bit_rate，尝试从format中获取overall_bit_rate
                            if (configuredBitrate == null)
                            {
                                var formatBitrateMatch = System.Text.RegularExpressions.Regex.Match(output, "\"bit_rate\":\\s*(\\d+)");
                                if (formatBitrateMatch.Success && long.TryParse(formatBitrateMatch.Groups[1].Value, out long formatBitrate))
                                {
                                    configuredBitrate = formatBitrate / 1000;
                                    _lastFFmpegError += $"\nFound format bit_rate: {formatBitrate} -> {configuredBitrate}k";
                                }
                            }

                            // 如果还是没有，尝试从tags中获取
                            if (configuredBitrate == null)
                            {
                                var tagBitrateMatch = System.Text.RegularExpressions.Regex.Match(output, "\"BPS\"[:\\s]+(\\d+)");
                                if (tagBitrateMatch.Success && long.TryParse(tagBitrateMatch.Groups[1].Value, out long tagBitrate))
                                {
                                    configuredBitrate = tagBitrate / 1000;
                                    _lastFFmpegError += $"\nFound BPS tag: {tagBitrate} -> {configuredBitrate}k";
                                }
                            }

                            // RTMP流专用：尝试从视频流的tags中获取视频码率
                            if (configuredBitrate == null)
                            {
                                var rtmpVideoMatch = System.Text.RegularExpressions.Regex.Match(output, @"""codec_name"":\s*""\w+"".*?""bit_rate"":\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Singleline);
                                if (rtmpVideoMatch.Success && long.TryParse(rtmpVideoMatch.Groups[1].Value, out long vbr))
                                {
                                    configuredBitrate = vbr / 1000;
                                    _lastFFmpegError += $"\nFound RTMP video bit_rate: {vbr} -> {configuredBitrate}k";
                                }
                            }

                            // RTMP流备用：尝试从format获取整体码率
                            if (configuredBitrate == null)
                            {
                                var formatBitrateMatch = System.Text.RegularExpressions.Regex.Match(output, @"""bit_rate"":\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Singleline);
                                if (formatBitrateMatch.Success && long.TryParse(formatBitrateMatch.Groups[1].Value, out long formatBitrate))
                                {
                                    configuredBitrate = formatBitrate / 1000;
                                    _lastFFmpegError += $"\nFound format overall bit_rate: {formatBitrate} -> {configuredBitrate}k";
                                }
                            }

                            if (configuredBitrate.HasValue && configuredBitrate.Value > 0)
                            {
                                lock (_bitrateLock)
                                {
                                    _rawBitrateKbps = configuredBitrate.Value;
                                    _currentBitrateKbps = configuredBitrate.Value;
                                    _lastValidBitrateKbps = configuredBitrate.Value;
                                    _bitrateFromProbe = true;
                                }
                                DiagLog($"FFprobe bitrate: {configuredBitrate.Value}k");
                            }
                            else if (ManualBitrateKbps > 0)
                            {
                                lock (_bitrateLock)
                                {
                                    _rawBitrateKbps = ManualBitrateKbps;
                                    _currentBitrateKbps = ManualBitrateKbps;
                                    _lastValidBitrateKbps = ManualBitrateKbps;
                                }
                                DiagLog($"FFprobe no bitrate, using ManualBitrate: {ManualBitrateKbps}k");
                            }
                            else
                            {
                                DiagLog("FFprobe no bitrate and no ManualBitrate");
                            }

                            // 解析帧率 - 优先用r_frame_rate
                            var fpsMatch = System.Text.RegularExpressions.Regex.Match(output, "\"r_frame_rate\":\\s*\"(\\d+)/(\\d+)\"");
                            if (fpsMatch.Success)
                            {
                                if (int.TryParse(fpsMatch.Groups[1].Value, out int num) && int.TryParse(fpsMatch.Groups[2].Value, out int den) && den > 0)
                                {
                                    double fps = (double)num / den;
                                    _lastFFmpegError += $"\nFound r_frame_rate: {num}/{den} = {fps}";
                                    if (fps > 0 && fps <= 120)
                                    {
                                        _streamFpsFromMeta = fps;
                                        _smoothedFps = (float)fps;
                                        _lastValidFps = (float)fps;
                                        _currentFps = (float)fps;
                                        DiagLog($"FFprobe FPS: {fps}");
                                    }
                                }
                            }

                            // 也尝试解析avg_frame_rate
                            var avgFpsMatch = System.Text.RegularExpressions.Regex.Match(output, "\"avg_frame_rate\":\\s*\"(\\d+)/(\\d+)\"");
                            if (avgFpsMatch.Success && _streamFpsFromMeta == 0)
                            {
                                if (int.TryParse(avgFpsMatch.Groups[1].Value, out int num) && int.TryParse(avgFpsMatch.Groups[2].Value, out int den) && den > 0)
                                {
                                    double fps = (double)num / den;
                                    _lastFFmpegError += $"\nFound avg_frame_rate: {num}/{den} = {fps}";
                                    if (fps > 0 && fps <= 120)
                                    {
                                        _streamFpsFromMeta = fps;
                                        _smoothedFps = (float)fps;
                                        _lastValidFps = (float)fps;
                                        _currentFps = (float)fps;
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { _lastFFmpegError = "FFprobe error: " + ex.Message; }
                    });

                    Stopwatch sw = Stopwatch.StartNew();
                    long lastStatTime = 0;
                    int frameCount = 0;

                    // 初始化统计变量
                    lock (_bitrateLock)
                    {
                        if (ManualBitrateKbps > 0)
                        {
                            _currentBitrateKbps = ManualBitrateKbps;
                            _rawBitrateKbps = ManualBitrateKbps;
                            _lastValidBitrateKbps = ManualBitrateKbps;
                        }
                        else
                        {
                            _currentBitrateKbps = 0;
                            _rawBitrateKbps = 0;
                            _lastValidBitrateKbps = 0;
                        }
                        _startTime = sw.ElapsedMilliseconds;
                        _lastBitrateUpdateTime = _startTime;
                        _fpsSampleStartTime = sw.ElapsedTicks;
                        _fpsSampleFrameCount = 0;
                    }

                    // 获取元数据帧率
                    int width = 1920, height = 1080;
                    if (!_useFFmpegProcess && _capture != null)
                    {
                        double metaFps = _capture.Get(VideoCaptureProperties.Fps);
                        if (metaFps > 0 && metaFps < 120)
                            _streamFpsFromMeta = metaFps;
                        width = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                        height = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                    }
                    else if (_useFFmpegProcess)
                    {
                        // 从ffprobe结果解析（简化处理）
                        _streamFpsFromMeta = 60;
                    }

                    if (_streamFpsFromMeta > 0 && _streamFpsFromMeta < 120)
                    {
                        _currentFps = (float)_streamFpsFromMeta;
                        _smoothedFps = (float)_streamFpsFromMeta;
                    }

                    // ========== 2. 播放主循环 ==========
                    while (!token.IsCancellationRequested && IsPlaying)
                    {
                        try
                        {
                            long nowMs = sw.ElapsedMilliseconds;
                            long nowTicks = sw.ElapsedTicks;

                            bool frameGrabbed = false;

                            if (_useFFmpegProcess)
                            {
                                // 进程模式：从临时文件读帧
                                Mat frame = ReadFrameFromFFmpeg(width, height);
                                if (frame != null && !frame.Empty())
                                {
                                    frame.CopyTo(currentFrame);
                                    frame.Dispose();
                                    frameGrabbed = true;
                                }
                                else
                                {
                                    // 没读到帧，短暂等待后继续
                                    Thread.Sleep(10);
                                    continue;
                                }
                            }
                            else
                            {
                                // 传统模式：Grab
                                if (_capture == null || !_capture.IsOpened())
                                {
                                    Thread.Sleep(500);
                                    TryReopenCapture(url, token, 5000);
                                    continue;
                                }
                                try
                                {
                                    bool grabbed = false;
                                    lock (_captureLock)
                                    {
                                        if (_capture != null && _capture.IsOpened())
                                        {
                                            grabbed = _capture.Grab();
                                        }
                                    }

                                    if (!grabbed)
                                    {
                                        // 重连逻辑
                                        lock (_captureLock)
                                        {
                                            try { _capture?.Dispose(); } catch { }
                                            _capture = null;
                                        }
                                        Thread.Sleep(500);
                                        TryReopenCapture(url, token, 5000);
                                        continue;
                                    }
                                    frameGrabbed = true;
                                }
                                catch (SocketException ex) when (IsSocketAbort(ex))
                                {
                                    Thread.Sleep(50);
                                    continue;
                                }
                                catch
                                {
                                    lock (_captureLock)
                                    {
                                        try { _capture?.Dispose(); } catch { }
                                        _capture = null;
                                    }
                                    Thread.Sleep(100);
                                    continue;
                                }
                            }

                            if (!frameGrabbed) continue;

                            // 每成功抓取一帧，增加计数
                            frameCount++;

                            // ===== 帧率计算（始终使用实际帧数）=====
                            lock (_bitrateLock)
                            {
                                _fpsSampleFrameCount++;
                                long elapsedTicks = nowTicks - _fpsSampleStartTime;
                                if (elapsedTicks >= TimeSpan.TicksPerSecond / 2) // 500ms采样
                                {
                                    double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
                                    double instantFps = _fpsSampleFrameCount / elapsedSeconds;
                                    if (_smoothedFps == 0)
                                        _smoothedFps = (float)instantFps;
                                    else if (instantFps > 0)
                                        _smoothedFps = _smoothedFps * 0.7f + (float)instantFps * 0.3f;

                                    if (_smoothedFps > 0)
                                        _lastValidFps = _smoothedFps;
                                    _currentFps = _lastValidFps > 0 ? (float)Math.Round(_smoothedFps, 1) : _lastValidFps;

                                    _fpsSampleStartTime = nowTicks;
                                    _fpsSampleFrameCount = 0;
                                }
                            }

                            // ===== 码率更新（每500ms）=====
                            lock (_bitrateLock)
                            {
                                if (nowMs - _lastBitrateUpdateTime >= 500)
                                {
                                    if (_rawBitrateKbps > 0)
                                    {
                                        if (_lastValidBitrateKbps > 0 && _rawBitrateKbps < _lastValidBitrateKbps * 0.1)
                                        {
                                            _currentBitrateKbps = _currentBitrateKbps * 0.7f + _rawBitrateKbps * 0.3f;
                                        }
                                        else
                                        {
                                            _currentBitrateKbps = _currentBitrateKbps * 0.5f + _rawBitrateKbps * 0.5f;
                                        }
                                        _lastValidBitrateKbps = _rawBitrateKbps;
                                    }
                                    else if (_lastValidBitrateKbps > 0)
                                    {
                                        _currentBitrateKbps = _lastValidBitrateKbps;
                                    }
                                    else if (ManualBitrateKbps > 0 && _currentBitrateKbps == 0)
                                    {
                                        _currentBitrateKbps = ManualBitrateKbps;
                                        _lastValidBitrateKbps = ManualBitrateKbps;
                                    }
                                    _lastBitrateUpdateTime = nowMs;
                                }
                            }

                            // ===== 内存和CPU监控（每10秒）=====
                            long elapsedSinceMemoryCheck = nowMs - _lastMemoryCheckTick;
                            if (elapsedSinceMemoryCheck >= 10000)
                            {
                                _lastMemoryCheckTick = nowMs;
                                try
                                {
                                    long workingSet = Process.GetCurrentProcess().WorkingSet64;
                                    long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
                                    long mbUsed = workingSet / (1024 * 1024);
                                    long mbPrivate = privateBytes / (1024 * 1024);

                                    DiagLog($"MEM_CHECK: WS={mbUsed}MB, Private={mbPrivate}MB, _memoryLeakSampleCount={_memoryLeakSampleCount}");

                                    if (mbUsed > 3000)
                                    {
                                        _memoryLeakSampleCount++;
                                        if (_memoryLeakSampleCount >= 3)
                                        {
                                            _lastFFmpegError = $"High memory usage detected: {mbUsed}MB, triggering GC";
                                            DiagLog($"MEM_GC: High memory trigger GC at {mbUsed}MB");
                                            _memoryLeakSampleCount = 0;
                                            GC.Collect(2, GCCollectionMode.Forced, true, true);
                                            GC.WaitForPendingFinalizers();
                                            GC.Collect(2, GCCollectionMode.Forced, true, true);
                                            LogMemoryStatus("After GC");
                                        }
                                    }
                                    else
                                    {
                                        _memoryLeakSampleCount = 0;
                                    }

                                    if (mbUsed > 2000)
                                    {
                                        _lastFFmpegError = $"Critical memory: {mbUsed}MB, stopping playback to prevent crash";
                                        DiagLog($"MEM_CRITICAL: Stopping playback at {mbUsed}MB");
                                        IsPlaying = false;
                                        break;
                                    }
                                }
                                catch { }
                            }

                            // 纯拉流模式（不显示、不截图）则跳过后续解码和显示
                            if (!decodeFrames && !_requestSnapshot)
                            {
                                Thread.Sleep(1);
                                continue;
                            }

                            // 传统模式需要Retrieve获得图像；进程模式已经获得currentFrame
                            if (!_useFFmpegProcess)
                            {
                                if (_capture == null || !_capture.IsOpened())
                                {
                                    Thread.Sleep(100);
                                    continue;
                                }
                                try
                                {
                                    if (!_capture.Retrieve(currentFrame) || currentFrame.Empty())
                                        continue;
                                }
                                catch (SocketException ex) when (IsSocketAbort(ex))
                                {
                                    Thread.Sleep(50);
                                    continue;
                                }
                                catch
                                {
                                    Thread.Sleep(100);
                                    continue;
                                }
                            }

                            // 截图处理
                            if (_requestSnapshot)
                            {
                                if (currentFrame != null && !currentFrame.Empty())
                                {
                                    lock (_snapshotLock)
                                    {
                                        _snapshotBuffer?.Dispose();
                                        try { _snapshotBuffer = currentFrame.Clone(); } catch { _snapshotBuffer = null; }
                                        _requestSnapshot = false;
                                        _snapshotEvent.Set();
                                        Monitor.PulseAll(_snapshotLock);
                                    }
                                }
                            }

                            if (!decodeFrames) continue;

                            // 显示逻辑（限流）
                            if (_isRendering) continue;
                            if (nowMs - lastUiUpdateMs < UI_INTERVAL_MS) continue;
                            if (_pictureBox.IsDisposed || !_pictureBox.Visible || _pictureBox.Width <= 0)
                            {
                                Thread.Sleep(50);
                                continue;
                            }

                            lastUiUpdateMs = nowMs;

                            // 预缩放
                            int w = _pictureBox.Width;
                            int h = _pictureBox.Height;
                            if (currentFrame == null || currentFrame.Empty() || currentFrame.Width <= 0 || currentFrame.Height <= 0)
                            {
                                Thread.Sleep(10);
                                continue;
                            }
                            double scale = Math.Min((double)w / currentFrame.Width, (double)h / currentFrame.Height);
                            if (scale < 0.9)
                            {
                                Cv2.Resize(currentFrame, smallFrame, new OpenCvSharp.Size(currentFrame.Width * scale, currentFrame.Height * scale), 0, 0, InterpolationFlags.Nearest);
                            }
                            else
                            {
                                try { currentFrame.CopyTo(smallFrame); } catch { continue; }
                            }

                            Bitmap bmp = null;
                            bool bmpCreated = false;
                            try
                            {
                                if (smallFrame != null && !smallFrame.Empty() && smallFrame.Width > 0 && smallFrame.Height > 0)
                                {
                                    bmp = BitmapConverter.ToBitmap(smallFrame);
                                    bmpCreated = true;
                                }
                            }
                            catch
                            {
                                bmpCreated = false;
                            }

                            if (bmpCreated && bmp != null)
                            {
                                _isRendering = true;
                                _pictureBox.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        if (_pictureBox.IsDisposed || !IsPlaying) { bmp.Dispose(); return; }
                                        var old = _pictureBox.Image;
                                        _pictureBox.Image = bmp;
                                        old?.Dispose();
                                    }
                                    catch { bmp?.Dispose(); }
                                    finally { _isRendering = false; }
                                }));
                            }
                        }
                        catch
                        {
                            _isRendering = false;
                            Thread.Sleep(10);
                        }
                    }
                }
                finally
                {
                    DiagLog("=== PlayLoop END ===");
                    LogMemoryStatus("PlayLoop End");
                    FlushDiagLog();
                    
                    try
                    {
                        if (_capture != null && _capture.IsOpened()) { _capture.Release(); }
                    }
                    catch { }
                    try { _capture?.Dispose(); _capture = null; } catch { }
                    try
                    {
                        if (_flvCapture != null && _flvCapture.IsOpened()) { _flvCapture.Release(); }
                    }
                    catch { }
                    try { _flvCapture?.Dispose(); _flvCapture = null; } catch { }
                }
            }
        }
    }
}
        #endregion
