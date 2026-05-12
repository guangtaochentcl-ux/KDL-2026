using DirectShowLib; //这个库作用视频捕获和处理,作用提供对Windows DirectShow API的访问，用于视频设备枚举、视频捕获、格式处理等
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emgu.CV;
namespace VideoCapture_uvc
{
    public static class MediaSubTypeHEVC
    {
        //Guid 是 C# 里用来表示全局唯一 ID 的类型。
        //你代码里的 HEVC 和 H265 是预定义的固定 Guid，用来告诉系统 “这是 H.265 视频编码格式”
        public static readonly Guid HEVC = new Guid("{43564548-0000-0010-8000-00AA00389B71}");
        public static readonly Guid H265 = new Guid("{35363248-0000-0010-8000-00AA00389B71}");
    }

    public class VideoCapturer : IDisposable  //继承这个方便释放非托管资源
    {
        private const int WM_GRAPHNOTIFY = 0x8000 + 1;
        private CameraInfo m_CameraInfo;

        // DirectShow 核心
        private IGraphBuilder m_GraphBuilder = null;
        private ICaptureGraphBuilder2 m_CaptureGraphBuilder = null;
        private IMediaControl m_MediaControl = null;
        private DsROTEntry m_Rot = null;
        private IMediaEventEx m_MediaEventEx = null;

        // === 组件 ===
        private ISampleGrabber m_SampleGrabber = null; // 截图核心
        private IBaseFilter m_SampleGrabberFilter = null;
        private IVMRWindowlessControl9 m_vmr9Control = null; // 显示核心
        private IBaseFilter m_RendererFilter = null; // 渲染器(VMR9 或 Null)

        private int m_PreviewWidth = 0;
        private int m_PreviewHeight = 0;
        private IntPtr m_DisplayWindow = IntPtr.Zero;
        private int m_DisplayWidth = 0;
        private int m_DisplayHeight = 0;

        public VideoCapturer() { }

        public void SetPreviewSize(int width, int height)
        {
            m_PreviewWidth = width;
            m_PreviewHeight = height;
        }
        public void SetDisplayWindow(IntPtr window) { m_DisplayWindow = window; }
        public void SetDisplaySize(int width, int height)
        {
            m_DisplayWidth = width;
            m_DisplayHeight = height;
            ResizeVideoWindow();
        }

        public static List<CameraInfo> GetCameraInfos()
        {
            var list = new List<CameraInfo>();
            try
            {
                var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice); //获取所有摄像机列表
                foreach (var d in devs)
                {
                    string instanceId = ExtractInstanceIdFromDevicePath(d.DevicePath);
                    list.Add(new CameraInfo(d.ClassID, d.Name, d.DevicePath, instanceId));
                }
            }
            catch { }
            return list;
        }

        private static string ExtractInstanceIdFromDevicePath(string devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath)) return string.Empty;

            string normalized = devicePath.Trim();
            const string pnpPrefix = "@device:pnp:";

            if (normalized.StartsWith(pnpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(pnpPrefix.Length);
            }

            normalized = Regex.Replace(normalized, @"^\\\\\?\\", "", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\\global$", "", RegexOptions.IgnoreCase);

            int interfaceGuidIndex = normalized.IndexOf("#{", StringComparison.OrdinalIgnoreCase);
            if (interfaceGuidIndex >= 0)
            {
                normalized = normalized.Substring(0, interfaceGuidIndex);
            }

            normalized = normalized.Replace('#', '\\').Trim('\\');
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

            return normalized.ToUpperInvariant();
        }

        // =================================================================================
        // 核心启动逻辑 (统一链路架构)
        // =================================================================================
        public async Task<bool> StartupCapture(CameraInfo cameraInfo, int cameraId, string streamType, bool renderToScreen = true)
        {
            if (0 == m_PreviewWidth || 0 == m_PreviewHeight || IntPtr.Zero == m_DisplayWindow) return false;
            m_CameraInfo = cameraInfo;

            try
            {
                CloseInterfaces();

                m_GraphBuilder = (IGraphBuilder)new FilterGraph();
                m_CaptureGraphBuilder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                m_MediaControl = (IMediaControl)m_GraphBuilder;
                m_MediaEventEx = (IMediaEventEx)m_GraphBuilder;

                m_CaptureGraphBuilder.SetFiltergraph(m_GraphBuilder);

                // 1. 添加源
                IBaseFilter source = GetCaptureDevice(cameraInfo, cameraId);
                m_GraphBuilder.AddFilter(source, "Video Capture");

                // 2. 配置分辨率
                SetConfigParams(m_CaptureGraphBuilder, source, 30, m_PreviewWidth, m_PreviewHeight, streamType);

                // 3. 准备 SampleGrabber (截图核心，必须存在)
                // 无论是否显示，数据都必须流经这里，这样截图才稳定
                SetupSampleGrabber();

                // 4. 准备解码器 (如果需要)
                IBaseFilter decoder = null;
                bool needDecoder = (streamType == "H264" || streamType == "H265" || streamType == "MJPG");

                if (needDecoder)
                {
                    if (streamType == "MJPG")
                    {
                        decoder = CreateFilter("MJPEG Decompressor", new Guid("301056D0-6D65-11D4-BED5-00C04F02298B"));
                        if (decoder == null) decoder = CreateLAVVideoDecoder();
                    }
                    else
                    {
                        decoder = CreateLAVVideoDecoder();
                    }

                    if (decoder != null)
                    {
                        m_GraphBuilder.AddFilter(decoder, "Decoder");
                        // 尝试连接: Source -> Decoder
                        if (ConnectFilters(m_GraphBuilder, source, decoder) < 0)
                        {
                            m_GraphBuilder.RemoveFilter(decoder);
                            Marshal.ReleaseComObject(decoder);
                            decoder = null;
                        }
                    }
                }

                IBaseFilter upstream = (decoder != null) ? decoder : source;

                // 5. 准备渲染器 (VMR9 或 Null)
                if (renderToScreen)
                {
                    Console.WriteLine("📺 模式: 显示 (Grabber -> VMR9)");
                    // 初始化 VMR9
                    m_RendererFilter = (IBaseFilter)new VideoMixingRenderer9();
                    IVMRFilterConfig9 cfg = (IVMRFilterConfig9)m_RendererFilter;
                    cfg.SetRenderingMode(VMR9Mode.Windowless);
                    m_GraphBuilder.AddFilter(m_RendererFilter, "VMR9");

                    m_vmr9Control = (IVMRWindowlessControl9)m_RendererFilter;
                    m_vmr9Control.SetVideoClippingWindow(m_DisplayWindow);
                    m_vmr9Control.SetAspectRatioMode(VMR9AspectRatioMode.LetterBox);

                    // 设置消息通知
                    m_MediaEventEx.SetNotifyWindow(m_DisplayWindow, WM_GRAPHNOTIFY, IntPtr.Zero);
                    ResizeVideoWindow();
                }
                else
                {
                    Console.WriteLine("👻 模式: 静默 (Grabber -> Null)");
                    m_RendererFilter = CreateFilter("Null Renderer", new Guid("C1F400A0-3F08-11D3-9F0B-006008039E37"));
                    if (m_RendererFilter != null) m_GraphBuilder.AddFilter(m_RendererFilter, "Null Renderer");
                }

                // 6. === 核心连接 ===
                // 链路: Upstream -> SampleGrabber -> Renderer
                // DirectShow 会自动在中间插入 Color Space Converter (因为 Grabber 强制 RGB24)

                // 6.1 连接 Upstream -> SampleGrabber
                int hr = m_CaptureGraphBuilder.RenderStream(null, MediaType.Video, upstream, null, m_SampleGrabberFilter);
                if (hr < 0) throw new Exception($"Grabber 连接失败: 0x{hr:X}");

                // 6.2 连接 SampleGrabber -> Renderer
                if (m_RendererFilter != null)
                {
                    // 获取 Grabber 输出脚和 Renderer 输入脚进行连接
                    IPin grabOut = DsFindPin.ByDirection(m_SampleGrabberFilter, PinDirection.Output, 0);
                    IPin rendIn = DsFindPin.ByDirection(m_RendererFilter, PinDirection.Input, 0);

                    if (grabOut != null && rendIn != null)
                    {
                        hr = m_GraphBuilder.Connect(grabOut, rendIn);
                        if (hr < 0) Console.WriteLine($"渲染器连接警告: 0x{hr:X}");
                    }
                    if (grabOut != null) Marshal.ReleaseComObject(grabOut);
                    if (rendIn != null) Marshal.ReleaseComObject(rendIn);
                }

                // 7. 激活缓冲
                m_SampleGrabber.SetBufferSamples(true);
                m_SampleGrabber.SetOneShot(false);

                // 8. 运行
                m_Rot = new DsROTEntry(m_GraphBuilder);
                m_MediaControl.Run();

                Marshal.ReleaseComObject(source);
                if (decoder != null) Marshal.ReleaseComObject(decoder);

                Console.WriteLine($"✅ 启动成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动异常: {ex.Message}");
                CloseInterfaces();
                return false;
            }
        }

        // =================================================================================
        // 截图：统一从 SampleGrabber 获取 (最稳健)
        // =================================================================================
        public bool Snapshot(string outputPath, int targetW = 0, int targetH = 0)
        {
            if (m_SampleGrabber == null) return false;

            try
            {
                int bufferSize = 0;
                // 获取当前缓冲区大小
                if (m_SampleGrabber.GetCurrentBuffer(ref bufferSize, IntPtr.Zero) != 0 || bufferSize <= 0)
                    return false;

                IntPtr pBuffer = Marshal.AllocCoTaskMem(bufferSize);
                try
                {
                    // 拷贝数据
                    if (m_SampleGrabber.GetCurrentBuffer(ref bufferSize, pBuffer) == 0)
                    {
                        AMMediaType mt = new AMMediaType();
                        m_SampleGrabber.GetConnectedMediaType(mt);
                        VideoInfoHeader header = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                        int w = header.BmiHeader.Width;
                        int h = header.BmiHeader.Height;
                        int stride = w * 3;
                        if (stride % 4 != 0) stride += (4 - (stride % 4)); // 4字节对齐

                        using (Bitmap bmp = new Bitmap(w, h, stride, PixelFormat.Format24bppRgb, pBuffer))
                        {
                            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);

                            // 统一的保存与缩放逻辑
                            SaveWithResize(bmp, outputPath, targetW, targetH);
                        }
                        DsUtils.FreeAMMediaType(mt);
                        return true;
                    }
                }
                finally { Marshal.FreeCoTaskMem(pBuffer); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"截图异常: {ex.Message}");
            }
            return false;
        }

        private void SaveWithResize(Bitmap src, string path, int w, int h)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (w > 0 && h > 0 && (src.Width != w || src.Height != h))
            {
                using (Bitmap resized = new Bitmap(w, h))
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, w, h);
                    resized.Save(path, ImageFormat.Png);
                }
                Console.WriteLine($"截图成功(缩放): {path}");
            }
            else
            {
                src.Save(path, ImageFormat.Png);
                Console.WriteLine($"截图成功(原图): {path}");
            }
        }

        // =================================================================================
        // 辅助方法
        // =================================================================================

        private void SetupSampleGrabber()
        {
            m_SampleGrabber = new SampleGrabber() as ISampleGrabber;
            m_SampleGrabberFilter = m_SampleGrabber as IBaseFilter;
            AMMediaType mediaType = new AMMediaType();
            mediaType.majorType = MediaType.Video;
            mediaType.subType = MediaSubType.RGB24; // 强制 RGB24
            mediaType.formatType = FormatType.VideoInfo;
            m_SampleGrabber.SetMediaType(mediaType);
            DsUtils.FreeAMMediaType(mediaType);
            m_GraphBuilder.AddFilter(m_SampleGrabberFilter, "Sample Grabber");
        }

        private void CloseInterfaces()
        {
            try
            {
                if (m_MediaControl != null) m_MediaControl.StopWhenReady();

                if (m_vmr9Control != null) { Marshal.ReleaseComObject(m_vmr9Control); m_vmr9Control = null; }
                if (m_RendererFilter != null) { Marshal.ReleaseComObject(m_RendererFilter); m_RendererFilter = null; }

                if (m_SampleGrabber != null) { Marshal.ReleaseComObject(m_SampleGrabber); m_SampleGrabber = null; }
                if (m_SampleGrabberFilter != null) { Marshal.ReleaseComObject(m_SampleGrabberFilter); m_SampleGrabberFilter = null; }

                if (m_Rot != null) { m_Rot.Dispose(); m_Rot = null; }
                if (m_MediaControl != null) { Marshal.ReleaseComObject(m_MediaControl); m_MediaControl = null; }
                if (m_MediaEventEx != null) { Marshal.ReleaseComObject(m_MediaEventEx); m_MediaEventEx = null; }
                if (m_GraphBuilder != null) { Marshal.ReleaseComObject(m_GraphBuilder); m_GraphBuilder = null; }
                if (m_CaptureGraphBuilder != null) { Marshal.ReleaseComObject(m_CaptureGraphBuilder); m_CaptureGraphBuilder = null; }
            }
            catch { }
        }

        public void ResizeVideoWindow()
        {
            if (m_vmr9Control != null)
            {
                DsRect rect = new DsRect(0, 0, m_DisplayWidth, m_DisplayHeight);
                m_vmr9Control.SetVideoPosition(null, rect);
            }
        }

        private IBaseFilter GetCaptureDevice(CameraInfo cameraInfo, int cameraId)
        {
            object source = null;
            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            DsDevice selectedDevice = null;

            if (!string.IsNullOrWhiteSpace(cameraInfo?.InstanceId))
            {
                foreach (var d in devices)
                {
                    string deviceInstanceId = ExtractInstanceIdFromDevicePath(d.DevicePath);
                    if (deviceInstanceId.Equals(cameraInfo.InstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedDevice = d;
                        break;
                    }
                }
            }

            if (selectedDevice == null)
            {
                List<DsDevice> targetDevices = new List<DsDevice>();
                foreach (var d in devices) if (d.Name == cameraInfo.Name) targetDevices.Add(d);

                if (cameraId >= 0 && cameraId < targetDevices.Count)
                {
                    selectedDevice = targetDevices[cameraId];
                }
            }

            if (selectedDevice != null)
            {
                Guid iid = typeof(IBaseFilter).GUID;
                selectedDevice.Mon.BindToObject(null, null, ref iid, out source);
            }

            if (source == null) throw new Exception("Camera not found");
            return (IBaseFilter)source;
        }

        private void SetConfigParams(ICaptureGraphBuilder2 cg, IBaseFilter cf, int fps, int w, int h, string type)
        {
            int hr; object config; AMMediaType mt = null; IntPtr pSCC = IntPtr.Zero;
            hr = cg.FindInterface(PinCategory.Capture, MediaType.Video, cf, typeof(IAMStreamConfig).GUID, out config);
            IAMStreamConfig streamConfig = config as IAMStreamConfig;
            if (streamConfig == null) return;
            int count, size;
            streamConfig.GetNumberOfCapabilities(out count, out size);

            int bestIdx = -1;
            for (int i = 0; i < count; i++)
            {
                pSCC = Marshal.AllocCoTaskMem(size);
                streamConfig.GetStreamCaps(i, out mt, pSCC);
                VideoInfoHeader v = new VideoInfoHeader();
                Marshal.PtrToStructure(mt.formatPtr, v);
                // 添加这行日志
                Console.WriteLine($"设备能力 {i}: {v.BmiHeader.Width}x{v.BmiHeader.Height}, subtype={mt.subType}");

                bool matchFormat = false;
                if (type == "MJPG" && mt.subType == MediaSubType.MJPG) matchFormat = true;
                else if (type == "H264" && mt.subType == MediaSubType.H264) matchFormat = true;
                else if (type == "H265" && (mt.subType == MediaSubTypeHEVC.HEVC || mt.subType == MediaSubTypeHEVC.H265)) matchFormat = true;
                else if (type == "YUY2" && mt.subType == MediaSubType.YUY2) matchFormat = true;
                else if (type == "NV12" && mt.subType == MediaSubType.NV12) matchFormat = true;

                if (v.BmiHeader.Width == w && v.BmiHeader.Height == h)
                {
                    if (matchFormat) { bestIdx = i; DsUtils.FreeAMMediaType(mt); Marshal.FreeCoTaskMem(pSCC); break; }
                    else if (bestIdx == -1) bestIdx = i;
                }
                DsUtils.FreeAMMediaType(mt); Marshal.FreeCoTaskMem(pSCC);
            }

            if (bestIdx != -1)
            {
                pSCC = Marshal.AllocCoTaskMem(size);
                streamConfig.GetStreamCaps(bestIdx, out mt, pSCC);
                streamConfig.SetFormat(mt);
                DsUtils.FreeAMMediaType(mt); Marshal.FreeCoTaskMem(pSCC);
            }
        }

        private IBaseFilter CreateFilter(string name, Guid clsid)
        {
            try { return (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)); } catch { return null; }
        }

        private IBaseFilter CreateLAVVideoDecoder()
        {
            return CreateFilter("LAV Video Decoder", new Guid("EE30215D-164F-4A92-A4EB-9D4C13390F9F"));
        }

        private IBaseFilter FindExistingLAVFilter() { return null; }

        private int ConnectFilters(IGraphBuilder graph, IBaseFilter up, IBaseFilter down)
        {
            IPin pOut = DsFindPin.ByDirection(up, PinDirection.Output, 0);
            IPin pIn = DsFindPin.ByDirection(down, PinDirection.Input, 0);
            if (pOut != null && pIn != null)
            {
                int hr = graph.Connect(pOut, pIn);
                Marshal.ReleaseComObject(pOut); Marshal.ReleaseComObject(pIn);
                return hr;
            }
            return -1;
        }

        public void Dispose() { CloseInterfaces(); }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant; }
    }
}
