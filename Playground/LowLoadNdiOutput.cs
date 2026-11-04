// =============================================================================
// LowLoadNdiOutput.cs（Warudo Playground プラグイン）
//
// 内蔵の「NDI出力」の代わりに、Warudo のメイン出力の映像を NDI で送る。
// 内蔵（KlakNDI の NdiSender、Texture モード）は毎フレーム GPU で変換・読み戻しをするので、
// Warudo の fps が高いほど重くなる。こちらは送信 fps のタイミングでだけ読み戻し、
// NDI への送信は別スレッドで行うので、描画ループを待たせない。
//
// 流れ：
//   描画ループ（OnEndOfFrame。描画が終わったあとなので、そのフレームの映像を取れる）
//     送信の時刻（半フレーム分の余裕つき）になったら CopyOutputToTexture.OutputTexture（内蔵 NDI と同じ映像）を
//     上下反転して BGRA の作業用テクスチャへ Blit し、自分のバッファ（NativeArray）へ直接
//     読み戻すよう AsyncGPUReadback.RequestIntoNativeArray で依頼する（待たない）
//   読み戻しの完了（メインスレッドのコールバック）
//     コピーはせず、そのバッファを送信スレッドへ渡すだけ
//   送信スレッド
//     NDIlib_send_send_video_v2（同期）で送る。終わったらバッファを空きに戻す
//
// Playground は unsafe が使えない（エディター以外では AllowUnsafe が false）ので、
// NativeArray のポインターは DynamicMethod で作った小さな関数で IntPtr として取る（NativePointer）。
// NDI は Warudo 同梱の Processing.NDI.Lib.x64.dll を DllImport で直接呼ぶ。
//
// 置き場所：Warudo_Data/StreamingAssets/Playground/LowLoadNdi/
// マスター：B:\Unity\Warudo SDK 0.14.3.5 Modding Project\Playground\LowLoadNdi\
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Plugins;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Mixins;
using Warudo.Plugins.Core.Utils;
using Debug = UnityEngine.Debug;

namespace Rune.LowLoadNdi {

    [PluginType(
        Id = "runechaan.lowloadndi",
        Name = "Low-Load NDI Output",
        Description = "Sends the main output over NDI at its own frame rate without stalling the render loop.",
        Version = "0.2.2",
        Author = "RUNE.",
        AssetTypes = new Type[] { },
        NodeTypes = new Type[] { })]
    public class LowLoadNdiPlugin : Plugin {

        // ───────── 設定 ─────────

        [DataInput]
        [Label("有効")]
        [Description("送信の開始と停止。使うときは、設定の内蔵「NDI出力」を「いいえ」にする。")]
        public bool OutputEnabled = false;

        [DataInput]
        [Label("NDI名")]
        [Description("受信側に表示される名前。変えると送信を作り直す。")]
        public string NdiName = "Warudo LowLoad";

        [DataInput]
        [Label("送信fps")]
        [Description("NDI へ送る回数（毎秒）。Warudo の描画 fps とは別に決める。モニター用なら 30 などに下げると、さらに軽くなる。")]
        public NdiFrameRate SendRate = NdiFrameRate.Fps59_94;   // v0.2.0 までの float の SendFps とは別の名前にして、古い値は読まない

        [DataInput]
        [Label("アルファ")]
        [Description("透過背景のときに使う。いいえなら BGRX（アルファなし）で送る。")]
        public bool Alpha = false;

        [DataInput]
        [Label("受信者がいないときは止める")]
        [Description("受信者が 0 のあいだは読み戻しもしない。")]
        public bool PauseWithoutReceivers = true;

        [Markdown]
        public string Status = "";

        // ツールバーのアイコンから、この設定ページを開く
        [Mixin]
        public ToolbarItemMixin ToolbarItem;

        const string ToolbarIcon =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<rect x=\"2\" y=\"5\" width=\"14\" height=\"14\" rx=\"2\"/><path d=\"M16 10l6-3v10l-6-3z\"/></svg>";

        // ───────── 内部の状態 ─────────

        const int SlotCount = 3;

        sealed class Slot {
            public NativeArray<byte> Buffer;
            public IntPtr Pointer;
            public int Width, Height;
            public int State;       // 0 = 空き、1 = 読み戻し中、2 = 送信待ち・送信中
        }

        // 送信スレッドごとの止め札。止まらなかったスレッドを置き去りにしても、次のスレッドの札とは混ざらない
        sealed class WorkerContext {
            public IntPtr Send;
            public volatile bool Stop;
        }

        bool running;
        IntPtr sendInstance = IntPtr.Zero;
        Thread worker;
        WorkerContext workerContext;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ConcurrentQueue<Slot> queue = new ConcurrentQueue<Slot>();

        Slot[] slots;
        RenderTexture staging;
        bool stagingIsBgra;         // false なら RGBA の作業用テクスチャを BGRA に変換して読み戻す
        int width, height;
        int generation;             // 作り直したら増やす。古い読み戻しの完了を捨てるため

        double nextSendTime;
        double averageFrameTime;    // 描画 1 フレームの時間（ならした値）。送信の時刻の判定の余裕に使う

        // 送信スレッドが読む値（メインスレッドが書く）
        volatile bool alphaFlag;
        volatile int frameRateN = 60000, frameRateD = 1001;

        // 状態表示用
        volatile int connections = -1;
        int sentCount;              // Interlocked
        int droppedCount;
        volatile string lastError;
        float statusTimer;
        int lastSentCount;
        readonly Stopwatch mainWatch = new Stopwatch();
        long mainTicks;             // この 1 秒にメインスレッドで使った時間
        int mainFrames;

        // ───────── ライフサイクル ─────────

        protected override void OnCreate() {
            base.OnCreate();
            ToolbarItem.SetEnabled(true);
            ToolbarItem.SetTooltip("Low-Load NDI Output");
            ToolbarItem.SetIcon(ToolbarIcon);
            ToolbarItem.OnTrigger = () => Context.Service.NavigateToPlugin(PluginId, null);

            Watch(nameof(OutputEnabled), ApplyEnabled);
            Watch(nameof(NdiName), () => { if (running) { StopOutput(); StartOutput(); } });
            Watch(nameof(SendRate), UpdateFrameRate);
            Watch(nameof(Alpha), () => alphaFlag = Alpha);
            UpdateFrameRate();
            alphaFlag = Alpha;
            ApplyEnabled();
            UpdateStatus(0f);
        }

        protected override void OnDestroy() {
            StopOutput();
            base.OnDestroy();
        }

        // 描画が終わったあと（フレームの終わり）に取り込む。描画の前に取り込むと、ひとつ前のフレームの映像になり 1 フレーム遅れる
        public override void OnEndOfFrame() {
            base.OnEndOfFrame();
            if (running) {
                mainWatch.Restart();
                Tick();
                mainWatch.Stop();
                mainTicks += mainWatch.ElapsedTicks;
            }
        }

        public override void OnPostLateUpdate() {
            base.OnPostLateUpdate();
            mainFrames++;

            var dt = (double)Time.unscaledDeltaTime;
            averageFrameTime = averageFrameTime <= 0.0 ? dt : averageFrameTime + (dt - averageFrameTime) * 0.1;

            statusTimer += Time.unscaledDeltaTime;
            if (statusTimer >= 1f) {
                // OnCreate だけだとエディターに届かないことがあるので、ドキュメントの例に合わせて送り続ける
                ToolbarItem.SetEnabled(true);
                UpdateStatus(statusTimer);
                statusTimer = 0f;
            }
        }

        void ApplyEnabled() {
            if (OutputEnabled && !running) StartOutput();
            else if (!OutputEnabled && running) StopOutput();
        }

        void UpdateFrameRate() {
            SendRate.Fraction(out var n, out var d);
            frameRateN = n;
            frameRateD = d;
        }

        // ───────── 開始と停止 ─────────

        void StartOutput() {
            lastError = null;
            try {
                NativePointer.Of(default);   // DynamicMethod が使えない環境なら、ここで分かる
            } catch (Exception e) {
                lastError = "バッファのポインターを取れませんでした：" + (e.InnerException ?? e).Message;
                Debug.LogError("[LowLoadNdi] " + e);
                return;
            }
            try {
                if (!Ndi.NDIlib_initialize()) {
                    lastError = "NDI を初期化できませんでした（CPU が非対応の可能性）";
                    return;
                }
                var settings = new Ndi.SendCreate {
                    NdiName = string.IsNullOrWhiteSpace(NdiName) ? "Warudo LowLoad" : NdiName,
                    Groups = null,
                    ClockVideo = false,   // 間隔はこちらで決めるので、NDI に待たせない
                    ClockAudio = false
                };
                sendInstance = Ndi.NDIlib_send_create(ref settings);
            } catch (Exception e) {
                lastError = "NDI を呼べませんでした：" + e.Message;
                Debug.LogError("[LowLoadNdi] " + e);
                sendInstance = IntPtr.Zero;
            }
            if (sendInstance == IntPtr.Zero) {
                if (lastError == null) lastError = "NDI の送信を作れませんでした";
                return;
            }

            connections = -1;
            nextSendTime = Time.realtimeSinceStartupAsDouble;
            StartWorker();
            running = true;
            Debug.Log($"[LowLoadNdi] started \"{NdiName}\"");
        }

        void StopOutput() {
            running = false;
            var stopped = StopWorker();
            ReleaseBuffers(stopped);
            if (sendInstance != IntPtr.Zero) {
                // 送信スレッドが NDI の中で止まったままなら、送信を壊すと落ちるので残す（Warudo を終了すれば片付く）
                if (stopped) {
                    try { Ndi.NDIlib_send_destroy(sendInstance); }
                    catch (Exception e) { Debug.LogError("[LowLoadNdi] " + e); }
                }
                sendInstance = IntPtr.Zero;
                Debug.Log("[LowLoadNdi] stopped");
            }
            connections = -1;
        }

        void StartWorker() {
            workerContext = new WorkerContext { Send = sendInstance };
            var context = workerContext;
            worker = new Thread(() => WorkerLoop(context)) { IsBackground = true, Name = "LowLoadNdi Sender" };
            worker.Start();
        }

        // 止まったら true。2 秒待っても止まらなければ、そのスレッドは置き去りにして false を返す
        bool StopWorker() {
            if (worker == null) return true;
            workerContext.Stop = true;
            wake.Set();
            var stopped = worker.Join(2000);
            if (!stopped) {
                Debug.LogWarning("[LowLoadNdi] sender thread did not stop in time; leaving its buffers and NDI sender alive");
                lastError = "送信スレッドが止まらなかったので、バッファと NDI の送信を解放せずに残しました（Warudo を再起動すると片付きます）";
            }
            worker = null;
            workerContext = null;
            while (queue.TryDequeue(out _)) { }
            return stopped;
        }

        // ───────── バッファ ─────────

        // 作り直せたら true。送信スレッドが止まらなかったときは、送信ごと止めて false
        bool RebuildBuffers(int w, int h) {
            // 送信スレッドが古いバッファを使っていないことを保証してから作り直す
            var stopped = StopWorker();
            ReleaseBuffers(stopped);
            if (!stopped) {
                // 置き去りのスレッドが同じ NDI の送信を使っているかもしれないので、続けずに止める
                running = false;
                sendInstance = IntPtr.Zero;
                return false;
            }

            width = w;
            height = h;
            stagingIsBgra = SystemInfo.IsFormatSupported(GraphicsFormat.B8G8R8A8_SRGB, FormatUsage.Render);
            staging = new RenderTexture(w, h, 0, stagingIsBgra ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.R8G8B8A8_SRGB) {
                name = "LowLoadNdi Staging",
                hideFlags = HideFlags.HideAndDontSave
            };
            staging.Create();

            slots = new Slot[SlotCount];
            for (var i = 0; i < SlotCount; i++) {
                var buffer = new NativeArray<byte>(w * h * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                slots[i] = new Slot {
                    Buffer = buffer, Pointer = NativePointer.Of(buffer),
                    Width = w, Height = h, State = 0
                };
            }

            StartWorker();
            Debug.Log($"[LowLoadNdi] buffers {w}x{h} ({(stagingIsBgra ? "BGRA" : "RGBA→BGRA")})");
            return true;
        }

        // workerStopped が false（送信スレッドがまだバッファを読んでいるかもしれない）なら、バッファは解放せずに手放す
        void ReleaseBuffers(bool workerStopped) {
            generation++;   // 読み戻し中のものは、完了しても捨てる
            if (slots != null) {
                // GPU がまだ書き込んでいるバッファを解放しないよう、読み戻しを全部終わらせる（停止と解像度変更のときだけ）
                AsyncGPUReadback.WaitAllRequests();
                if (workerStopped) {
                    foreach (var s in slots) if (s.Buffer.IsCreated) s.Buffer.Dispose();
                }
                slots = null;
            }
            if (staging != null) {
                staging.Release();
                UnityEngine.Object.Destroy(staging);
                staging = null;
            }
            width = height = 0;
        }

        Slot TakeFreeSlot() {
            foreach (var s in slots) {
                if (Interlocked.CompareExchange(ref s.State, 1, 0) == 0) return s;
            }
            return null;
        }

        // ───────── 描画ループ側 ─────────

        void Tick() {
            var source = CopyOutputToTexture.OutputTexture;
            if (source == null) return;
            if (source.width != width || source.height != height || staging == null) {
                if (!RebuildBuffers(source.width, source.height)) return;
            }

            // 送信の時刻に半フレーム分の余裕を持たせる。ちょうどの時刻より少し早く来たフレームも送るので、
            // Warudo の fps を送信 fps と同じくらいに固定していても、フレームの揺れで 1 枚飛ばすことがない
            var now = Time.realtimeSinceStartupAsDouble;
            var interval = SendRate.Interval();
            var tolerance = Math.Min(averageFrameTime, interval) * 0.5;
            if (now < nextSendTime - tolerance) return;
            nextSendTime += interval;
            // 1 回分以上遅れていたら（Warudo の fps が送信 fps より低い、引っかかったなど）、追いつこうとせずに数え直す
            if (nextSendTime < now - interval) nextSendTime = now + interval;

            if (PauseWithoutReceivers && connections == 0) return;

            var slot = TakeFreeSlot();
            if (slot == null) {   // 送信が追いついていない
                droppedCount++;
                return;
            }

            // 上下反転（Unity のテクスチャは下の行から並ぶが、NDI は上から）と、MSAA の解決を兼ねる
            var previous = RenderTexture.active;
            Graphics.Blit(source, staging, new Vector2(1f, -1f), new Vector2(0f, 1f));
            RenderTexture.active = previous;

            // slot のバッファへ GPU から直接書き込ませる（受け取ったあとのコピーが要らない）
            var gen = generation;
            if (stagingIsBgra) AsyncGPUReadback.RequestIntoNativeArray(ref slot.Buffer, staging, 0, req => OnReadback(req, slot, gen));
            else AsyncGPUReadback.RequestIntoNativeArray(ref slot.Buffer, staging, 0, TextureFormat.BGRA32, req => OnReadback(req, slot, gen));
        }

        void OnReadback(AsyncGPUReadbackRequest req, Slot slot, int gen) {
            if (gen != generation) return;   // 作り直す前の依頼。slot はもう解放済み
            if (!running || req.hasError) {
                if (req.hasError) lastError = "GPU からの読み戻しに失敗しました";
                Volatile.Write(ref slot.State, 0);
                return;
            }
            mainWatch.Restart();
            Volatile.Write(ref slot.State, 2);
            queue.Enqueue(slot);
            wake.Set();
            mainWatch.Stop();
            mainTicks += mainWatch.ElapsedTicks;
        }

        // ───────── 送信スレッド ─────────

        void WorkerLoop(WorkerContext context) {
            var send = context.Send;
            var pollWatch = Stopwatch.StartNew();
            var pollFirst = true;
            while (!context.Stop) {
                wake.WaitOne(250);
                if (context.Stop) break;

                if (pollFirst || pollWatch.ElapsedMilliseconds >= 500) {
                    try { connections = Ndi.NDIlib_send_get_no_connections(send, 0); }
                    catch (Exception e) { lastError = "受信者の数を取れませんでした：" + e.Message; }
                    pollWatch.Restart();
                    pollFirst = false;
                }

                while (!context.Stop && queue.TryDequeue(out var slot)) {
                    var frame = new Ndi.VideoFrame {
                        Width = slot.Width,
                        Height = slot.Height,
                        FourCC = alphaFlag ? Ndi.FourCC_BGRA : Ndi.FourCC_BGRX,
                        FrameRateN = frameRateN,
                        FrameRateD = frameRateD,
                        AspectRatio = (float)slot.Width / slot.Height,
                        FrameFormat = Ndi.FrameFormatProgressive,
                        Timecode = Ndi.TimecodeSynthesize,
                        Data = slot.Pointer,
                        LineStride = slot.Width * 4,
                        Metadata = IntPtr.Zero,
                        Timestamp = 0
                    };
                    try {
                        Ndi.NDIlib_send_send_video_v2(send, ref frame);
                        Interlocked.Increment(ref sentCount);
                    } catch (Exception e) {
                        lastError = "送信に失敗しました：" + e.Message;
                    }
                    Volatile.Write(ref slot.State, 0);
                }
            }
        }

        // ───────── 状態表示 ─────────

        void UpdateStatus(float elapsed) {
            var sent = Volatile.Read(ref sentCount);
            var fps = elapsed > 0f ? (sent - lastSentCount) / elapsed : 0f;
            lastSentCount = sent;
            var mainUs = mainFrames > 0 ? mainTicks * 1e6 / Stopwatch.Frequency / mainFrames : 0.0;
            mainTicks = 0;
            mainFrames = 0;

            var sb = new StringBuilder();
            if (!running) {
                sb.Append(lastError != null ? "**エラー**：" + lastError : "停止中");
            } else {
                var receivers = connections;
                var waiting = PauseWithoutReceivers && receivers == 0;
                sb.Append(waiting ? "受信者を待っています（読み戻しは止めています）" : "送信中");
                sb.Append($"\n\n- 実際の送信fps：{fps:0.00}");
                sb.Append($"\n- 解像度：{(width > 0 ? $"{width}×{height}" : "—")}（{(Alpha ? "BGRA" : "BGRX")}）");
                // NDI が返すのは受信アプリの数ではなく接続の数（Studio Monitor 1 つで 2 になる）
                sb.Append($"\n- NDI の接続数：{(receivers < 0 ? "—" : receivers.ToString())}");
                sb.Append($"\n- 送信が追いつかず飛ばした回数：{droppedCount}");
                sb.Append($"\n- メインスレッドで使った時間：{mainUs:0.0} µs／フレーム");
                if (lastError != null) sb.Append($"\n\n**エラー**：{lastError}");
            }
            var core = Context.PluginManager.GetPlugin<CorePlugin>();
            if (core != null && core.NdiOutput) sb.Append("\n\n**注意**：内蔵の「NDI出力」がオンです。負荷を比べるときは「いいえ」にしてください。");

            var text = sb.ToString();
            if (text != Status) SetDataInput(nameof(Status), text, true);
        }
    }

    // ───────── 送信 fps の選択肢（MOD 版と同じ）─────────

    // 映像でよく使う fps だけを選べるようにする（下限は 10）。
    // ドロップダウンにするため、入れ子でなくトップレベルの enum にする（入れ子の enum は Warudo で表示できない）
    public enum NdiFrameRate {
        [Label("60")] Fps60,
        [Label("59.94")] Fps59_94,
        [Label("50")] Fps50,
        [Label("30")] Fps30,
        [Label("29.97")] Fps29_97,
        [Label("25")] Fps25,
        [Label("24")] Fps24,
        [Label("23.976")] Fps23_976,
        [Label("15")] Fps15,
        [Label("10")] Fps10,
    }

    public static class NdiFrameRateExtensions {
        // NDI に渡す分数（59.94 などは NTSC の 1001 分母）
        public static void Fraction(this NdiFrameRate rate, out int numerator, out int denominator) {
            switch (rate) {
                case NdiFrameRate.Fps60: numerator = 60; denominator = 1; break;
                case NdiFrameRate.Fps50: numerator = 50; denominator = 1; break;
                case NdiFrameRate.Fps30: numerator = 30; denominator = 1; break;
                case NdiFrameRate.Fps29_97: numerator = 30000; denominator = 1001; break;
                case NdiFrameRate.Fps25: numerator = 25; denominator = 1; break;
                case NdiFrameRate.Fps24: numerator = 24; denominator = 1; break;
                case NdiFrameRate.Fps23_976: numerator = 24000; denominator = 1001; break;
                case NdiFrameRate.Fps15: numerator = 15; denominator = 1; break;
                case NdiFrameRate.Fps10: numerator = 10; denominator = 1; break;
                default: numerator = 60000; denominator = 1001; break;   // 59.94
            }
        }

        public static double Interval(this NdiFrameRate rate) {
            rate.Fraction(out var n, out var d);
            return (double)d / n;
        }
    }

    // ───────── NativeArray のポインター ─────────

    // NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks は void* を返すので、unsafe なしでは呼べない。
    // void* と IntPtr は IL の上では同じ native int なので、それを呼んでそのまま返すだけの関数を DynamicMethod で作る。
    static class NativePointer {
        static readonly Func<NativeArray<byte>, IntPtr> getter = Create();

        public static IntPtr Of(NativeArray<byte> array) => getter(array);

        static Func<NativeArray<byte>, IntPtr> Create() {
            var target = typeof(NativeArrayUnsafeUtility)
                .GetMethod(nameof(NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks))
                .MakeGenericMethod(typeof(byte));
            var method = new DynamicMethod("LowLoadNdi_GetPointer", typeof(IntPtr),
                new[] { typeof(NativeArray<byte>) }, typeof(NativePointer).Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            return (Func<NativeArray<byte>, IntPtr>)method.CreateDelegate(typeof(Func<NativeArray<byte>, IntPtr>));
        }
    }

    // ───────── NDI SDK（Processing.NDI.Lib.x64.dll）─────────

    static class Ndi {
        const string Dll = "Processing.NDI.Lib.x64";

        public const int FourCC_BGRA = 0x41524742;   // 'B','G','R','A'
        public const int FourCC_BGRX = 0x58524742;   // 'B','G','R','X'
        public const int FrameFormatProgressive = 1;
        public const long TimecodeSynthesize = long.MaxValue;

        // NDIlib_send_create_t
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct SendCreate {
            [MarshalAs(UnmanagedType.LPStr)] public string NdiName;
            [MarshalAs(UnmanagedType.LPStr)] public string Groups;
            [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
            [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
        }

        // NDIlib_video_frame_v2_t
        [StructLayout(LayoutKind.Sequential)]
        public struct VideoFrame {
            public int Width, Height;
            public int FourCC;
            public int FrameRateN, FrameRateD;
            public float AspectRatio;
            public int FrameFormat;
            public long Timecode;
            public IntPtr Data;
            public int LineStride;
            public IntPtr Metadata;
            public long Timestamp;
        }

        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool NDIlib_initialize();

        [DllImport(Dll)]
        public static extern IntPtr NDIlib_send_create(ref SendCreate settings);

        [DllImport(Dll)]
        public static extern void NDIlib_send_destroy(IntPtr send);

        [DllImport(Dll)]
        public static extern void NDIlib_send_send_video_v2(IntPtr send, ref VideoFrame frame);

        [DllImport(Dll)]
        public static extern int NDIlib_send_get_no_connections(IntPtr send, uint timeoutMs);
    }
}
