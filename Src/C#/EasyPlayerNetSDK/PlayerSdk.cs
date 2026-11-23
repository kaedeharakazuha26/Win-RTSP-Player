using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace EasyPlayerNetSDK
{
    public static class NativeDllLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// Configures the Win32 DLL search path to resolve native SDK dependencies from <c>&lt;baseDirectory&gt;\Lib</c>.
        /// </summary>
        public static void ConfigureLibDirectory(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                throw new ArgumentException("Base directory must be provided.", nameof(baseDirectory));
            }

            string libDirectory = Path.Combine(baseDirectory, "Lib");
            if (!Directory.Exists(libDirectory))
            {
                throw new DirectoryNotFoundException($"Native library directory not found: '{libDirectory}'.");
            }

            string sdkDllPath = Path.Combine(libDirectory, "libEasyPlayer-RTSP.dll");
            if (!File.Exists(sdkDllPath))
            {
                throw new FileNotFoundException($"Required native SDK library not found: '{sdkDllPath}'.", sdkDllPath);
            }

            if (!SetDllDirectory(libDirectory))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetDllDirectory failed for '{libDirectory}'.");
            }
        }
    }

    public class PlayerSdk
    {
        private const string DllName = "libEasyPlayer-RTSP.dll";

        #region 数据结构

        /// <summary>
        /// 编码格式
        /// </summary>
        public enum RENDER_FORMAT
        {
            /// DISPLAY_FORMAT_YV12 -> 842094169
            DISPLAY_FORMAT_YV12 = 842094169,

            /// DISPLAY_FORMAT_YUY2 -> 844715353
            DISPLAY_FORMAT_YUY2 = 844715353,

            /// DISPLAY_FORMAT_UYVY -> 1498831189
            DISPLAY_FORMAT_UYVY = 1498831189,

            /// DISPLAY_FORMAT_A8R8G8B8 -> 21
            DISPLAY_FORMAT_A8R8G8B8 = 21,

            /// DISPLAY_FORMAT_X8R8G8B8 -> 22
            DISPLAY_FORMAT_X8R8G8B8 = 22,

            /// DISPLAY_FORMAT_RGB565 -> 23
            DISPLAY_FORMAT_RGB565 = 23,

            /// DISPLAY_FORMAT_RGB555 -> 25
            DISPLAY_FORMAT_RGB555 = 25,

            /// DISPLAY_FORMAT_RGB24_GDI -> 26
            DISPLAY_FORMAT_RGB24_GDI = 26,
        }

        /// <summary>
        /// 帧结构信息
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct EASY_FRAME_INFO
        {
            public uint codec;                  /* 音视频格式 */

            public uint type;                   /* 视频帧类型 */
            public byte fps;                    /* 视频帧率 */
            public ushort width;                /* 视频宽 */
            public ushort height;               /* 视频高 */

            public uint reserved1;              /* 保留参数1 */
            public uint reserved2;              /* 保留参数2 */

            public uint sample_rate;            /* 音频采样率 */
            public uint channels;               /* 音频声道数 */
            public uint bits_per_sample;        /* 音频采样精度 */

            public uint length;                 /* 音视频帧大小 */
            public uint timestamp_usec;         /* 时间戳,微妙 */
            public uint timestamp_sec;          /* 时间戳 秒 */

            public float bitrate;               /* 比特率 */
            public float losspacket;            /* 丢包率 */
        }

        /// <summary>
        /// 坐标
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            /// LONG->int
            public int x;

            /// LONG->int
            public int y;
        }

        /// <summary>
        /// 像素信息
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct tagRECT
        {
            public int left;

            public int top;

            public int right;

            public int bottom;
        }

        #endregion

        /// <summary>
        /// 流回调
        /// </summary>
        /// <param name="_channelId">通道ID，EasyPlayer_OpenStream函数返回值.通道ID.</param>
        /// <param name="_channelPtr">通道指针.</param>
        /// <param name="_frameType">帧数据类型.</param>
        /// <param name="pBuf">数据指针.</param>
        /// <param name="_frameInfo">帧数据结构体.</param>
        /// <returns>System.Int32.</returns>
        public delegate int MediaSourceCallBack(int _channelId, IntPtr _channelPtr, int _frameType, IntPtr pBuf, ref EASY_FRAME_INFO _frameInfo);

        /// <summary>
        /// EasyPlayer初始化.
        /// </summary>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_Init@@YAHPAD@Z")]
        public static extern int EasyPlayer_Init(string key = "EasyPlayer is Free!");

        /// <summary>
        /// 资源释放.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_Release@@YAXXZ")]
        public static extern void EasyPlayer_Release();

        /// <summary>
        /// 开始进行流播放.
        /// </summary>
        /// <param name="url">媒体地址.</param>
        /// <param name="hWnd">窗口句柄.</param>
        /// <param name="renderFormat">编码格式.</param>
        /// <param name="rtpovertcp">拉取流的传输模式，0=udp,1=tcp.</param>
        /// <param name="username">The username.</param>
        /// <param name="password">The password.</param>
        /// <param name="callback">数据回调.</param>
        /// <param name="userPtr">用户自定义指针.</param>
        /// <param name="bHardDecode">硬件解码1=是，0=否.</param>
        /// <param name="startTime">回放开始时间,直播流填null.</param>
        /// <param name="endTime">回放结束时间，直播流填null.</param>
        /// <param name="fScale">回放倍率，直播流无效.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_OpenStream@@YAHPBDPAUHWND__@@W4__RENDER_FORMAT@@H00P6GHHPAHHPADPAUtagEASY_FRAME_INFO@@@ZPAX_N44M@Z")]
        public static extern int EasyPlayer_OpenStream(
            string url,
            IntPtr hWnd,
            RENDER_FORMAT renderFormat,
            int rtpovertcp,
            string username,
            string password,
            MediaSourceCallBack? callback,
            IntPtr userPtr,
            bool bHardDecode = false,
            string? startTime = null,
            string? endTime = null,
            float fScale = 1.0f);

        /// <summary>
        /// 关闭流.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_CloseStream@@YAXH@Z")]
        public static extern void EasyPlayer_CloseStream(int channelId);

        /// <summary>
        /// 设置当前流播放缓存帧数.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <param name="cache">缓存的视频帧数.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_SetFrameCache@@YAHHH@Z")]
        public static extern int EasyPlayer_SetFrameCache(int channelId, int cache);

        /// <summary>
        /// 播放器按比例进行显示.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <param name="shownToScale">0=整个窗口区域显示，1=按比例显示.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_SetShownToScale@@YAHHH@Z")]
        public static extern int EasyPlayer_SetShownToScale(int channelId, int shownToScale);

        /// <summary>
        /// 设置解码类型
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值</param>
        /// <param name="decodeKeyframeOnly">0=所有帧解码，1=只解码关键帧.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_SetDecodeType@@YAHHH@Z")]
        public static extern int EasyPlayer_SetDecodeType(int channelId, int decodeKeyframeOnly);

        /// <summary>
        /// 设置视频显示时渲染区域.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <param name="lpSrcRect">设置渲染区域的矩形结构体.</param>
        /// <returns>System.Int32.</returns>
        public static int EasyPlayer_SetRenderRect(int channelId, Rectangle lpSrcRect)
        {
            tagRECT rect = new tagRECT
            {
                left = lpSrcRect.Left,
                top = lpSrcRect.Top,
                right = lpSrcRect.Right,
                bottom = lpSrcRect.Bottom
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<tagRECT>());
            try
            {
                Marshal.StructureToPtr(rect, ptr, false);
                return EasyPlayer_SetRenderRect(channelId, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 设置视频显示时渲染区域.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <param name="lpSrcRect">设置渲染区域的矩形结构体.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_SetRenderRect@@YAHHPAUtagRECT@@@Z")]
        private static extern int EasyPlayer_SetRenderRect(int channelId, IntPtr lpSrcRect);

        /// <summary>
        /// 设置是否显示码流信息.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <param name="show">0=不显示，1=显示</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_ShowStatisticalInfo@@YAHHH@Z")]
        public static extern int EasyPlayer_ShowStatisticalInfo(int channelId, int show);

        /// <summary>
        /// 开始播放音频.
        /// </summary>
        /// <param name="channelId">通道ID，EasyPlayer_OpenStream函数返回值.</param>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_PlaySound@@YAHH@Z")]
        public static extern int EasyPlayer_PlaySound(int channelId);

        /// <summary>
        /// 停止播放音频.
        /// </summary>
        /// <returns>System.Int32.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "?EasyPlayer_StopSound@@YAHXZ")]
        public static extern int EasyPlayer_StopSound();
    }
}
