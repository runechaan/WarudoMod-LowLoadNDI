// =============================================================================
// LowLoadNdiModPlugin.cs（プラグイン MOD 版）
//
// Playground 版（../Playground/LowLoadNdiOutput.cs）と同じく、Warudo のメイン出力を NDI で送る。
// uMod では P/Invoke・System.Runtime.InteropServices・System.Reflection(.Emit) が禁止なので、
// NDI SDK を直接は呼べない。代わりに Warudo 同梱の KlakNDI の NdiSender を借りる。
//
// しくみ：
//   NdiSender（Texture モード）は、フレームの終わりに sourceTexture が null でなければ
//   GPU で YUV に変換して読み戻し、NDI へ非同期で送る。内蔵の「NDI出力」はこれを毎フレーム行うので重い。
//   ここでは送信の時刻のフレームだけ sourceTexture に出力映像を入れ、次のフレームで「null 扱いのもの」
//   （作ってすぐ破棄したテクスチャ。Unity の == null が true になる）に戻す。読み戻しは送信 fps の回数だけになる。
//
//   MOD からは Klak.Ndi を参照できないので、NdiSender は prefab（LowLoadNdiSender.prefab）に入れておき、
//   プロパティは SendMessage("set_xxx", 値) で setter を名前で呼ぶ（LED_Screen MOD と同じやり方）。
// =============================================================================

using System.Diagnostics;
using System.Text;
using UnityEngine;
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
        Version = "0.4.1",
        Author = "RUNE.",
        AssetTypes = new System.Type[] { },
        NodeTypes = new System.Type[] { })]
    public class LowLoadNdiPlugin : Plugin {

        // ───────── 設定 ─────────

        [DataInput]
        [Label("有効")]
        [Description("送信の開始と停止。使うときは、設定の内蔵「NDI出力」を「いいえ」にする。")]
        public bool OutputEnabled = false;

        [DataInput]
        [Label("NDI名")]
        [Description("受信側に表示される名前。")]
        public string NdiName = "Warudo LowLoad";

        [DataInput]
        [Label("送信fps")]
        [Description("NDI へ送る回数（毎秒）。Warudo の描画 fps とは別に決める。モニター用なら 30 などに下げると、さらに軽くなる。")]
        public NdiFrameRate SendRate = NdiFrameRate.Fps59_94;   // v0.3.0 までの float の SendFps とは別の名前にして、古い値は読まない

        [DataInput]
        [Label("アルファ")]
        [Description("透過背景のときに使う。")]
        public bool Alpha = false;

        [Markdown]
        public string Status = "";

        // ツールバーのアイコンから、この設定ページを開く
        [Mixin]
        public ToolbarItemMixin ToolbarItem;

        const string ToolbarIcon =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<rect x=\"2\" y=\"5\" width=\"14\" height=\"14\" rx=\"2\"/><path d=\"M16 10l6-3v10l-6-3z\"/></svg>";

        const string SenderPrefabPath = "Assets/MOD/LowLoadNDI/LowLoadNdiSender.prefab";

        // ───────── 内部の状態 ─────────

        bool running;
        GameObject sender;
        Texture nullTexture;    // 破棄済み。NdiSender の sourceTexture != null を false にするために渡す
        bool armed;             // このフレームで sourceTexture を入れた
        double nextSendTime;
        double averageFrameTime;    // 描画 1 フレームの時間（ならした値）。送信の時刻の判定の余裕に使う
        string lastError;

        float statusTimer;
        int requestCount;
        readonly Stopwatch mainWatch = new Stopwatch();
        long mainTicks;
        int mainFrames;

        // ───────── ライフサイクル ─────────

        protected override void OnCreate() {
            base.OnCreate();
            ToolbarItem.SetEnabled(true);
            ToolbarItem.SetTooltip("Low-Load NDI Output");
            ToolbarItem.SetIcon(ToolbarIcon);
            ToolbarItem.OnTrigger = () => Context.Service.NavigateToPlugin(PluginId, null);

            Watch(nameof(OutputEnabled), ApplyEnabled);
            Watch(nameof(NdiName), () => { if (running) sender.SendMessage("set_ndiName", SafeName()); });
            Watch(nameof(Alpha), () => { if (running) sender.SendMessage("set_keepAlpha", Alpha); });
            ApplyEnabled();
            UpdateStatus(0f);
        }

        protected override void OnDestroy() {
            StopOutput();
            base.OnDestroy();
        }

        public override void OnPostLateUpdate() {
            base.OnPostLateUpdate();
            var dt = (double)Time.unscaledDeltaTime;
            averageFrameTime = averageFrameTime <= 0.0 ? dt : averageFrameTime + (dt - averageFrameTime) * 0.1;
            if (running) {
                mainWatch.Restart();
                Tick();
                mainWatch.Stop();
                mainTicks += mainWatch.ElapsedTicks;
            }
            mainFrames++;

            statusTimer += Time.unscaledDeltaTime;
            if (statusTimer >= 1f) {
                UpdateStatus(statusTimer);
                statusTimer = 0f;
            }
        }

        void ApplyEnabled() {
            if (OutputEnabled && !running) StartOutput();
            else if (!OutputEnabled && running) StopOutput();
        }

        string SafeName() => string.IsNullOrWhiteSpace(NdiName) ? "Warudo LowLoad" : NdiName;

        // ───────── 開始と停止 ─────────

        void StartOutput() {
            lastError = null;
            var prefab = ModHost != null ? ModHost.Assets.Load<GameObject>(SenderPrefabPath) : null;
            if (prefab == null) {
                lastError = "送信用の prefab を読めませんでした（" + SenderPrefabPath + "）";
                return;
            }
            // Awake で NdiSender が Texture モードになる。sourceTexture は空なので、まだ何も送らない
            sender = Object.Instantiate(prefab);
            sender.name = "Rune LowLoadNdi Sender";
            Object.DontDestroyOnLoad(sender);
            sender.SendMessage("set_ndiName", SafeName());
            sender.SendMessage("set_keepAlpha", Alpha);

            if (nullTexture == null) {
                var t = new Texture2D(1, 1);
                Object.DestroyImmediate(t);
                nullTexture = t;   // C# の参照は残るが、Unity の == null では null
            }

            armed = false;
            nextSendTime = Time.realtimeSinceStartupAsDouble;
            running = true;
            Debug.Log($"[LowLoadNdi] started \"{SafeName()}\" (KlakNDI)");
        }

        void StopOutput() {
            running = false;
            armed = false;
            if (sender != null) {
                Object.Destroy(sender);   // NdiSender の OnDestroy が読み戻しの終了を待って NDI を閉じる
                sender = null;
                Debug.Log("[LowLoadNdi] stopped");
            }
        }

        // ───────── 描画ループ ─────────

        void Tick() {
            if (sender == null) {   // シーンの切り替えなどで消えた
                running = false;
                lastError = "送信用のオブジェクトが消えました。有効をいったん「いいえ」にして戻してください";
                return;
            }

            // 前のフレームで入れた映像を外す（NdiSender はフレームの終わりに 1 回だけ読み戻した）
            if (armed) {
                sender.SendMessage("set_sourceTexture", nullTexture);
                armed = false;
            }

            // 送信の時刻に半フレーム分の余裕を持たせる。ちょうどの時刻より少し早く来たフレームも送るので、
            // Warudo の fps を送信 fps と同じくらいに固定していても、フレームの揺れで 1 枚飛ばすことがない
            var now = Time.realtimeSinceStartupAsDouble;
            var interval = SendRate.Interval();
            var tolerance = System.Math.Min(averageFrameTime, interval) * 0.5;
            if (now < nextSendTime - tolerance) return;
            nextSendTime += interval;
            // 1 回分以上遅れていたら（Warudo の fps が送信 fps より低い、引っかかったなど）、追いつこうとせずに数え直す
            if (nextSendTime < now - interval) nextSendTime = now + interval;

            var source = CopyOutputToTexture.OutputTexture;
            if (source == null) return;
            sender.SendMessage("set_sourceTexture", source);
            armed = true;
            requestCount++;
        }

        // ───────── 状態表示 ─────────

        void UpdateStatus(float elapsed) {
            var fps = elapsed > 0f ? requestCount / elapsed : 0f;
            requestCount = 0;
            var mainUs = mainFrames > 0 ? mainTicks * 1e6 / Stopwatch.Frequency / mainFrames : 0.0;
            mainTicks = 0;
            mainFrames = 0;

            var sb = new StringBuilder();
            if (!running) {
                sb.Append(lastError != null ? "**エラー**：" + lastError : "停止中");
            } else {
                var source = CopyOutputToTexture.OutputTexture;
                sb.Append("送信中（KlakNDI）");
                sb.Append($"\n\n- 送信を依頼した回数：{fps:0.00}／秒");
                sb.Append($"\n- 解像度：{(source != null ? $"{source.width}×{source.height}" : "—")}（{(Alpha ? "UYVA" : "UYVY")}）");
                sb.Append($"\n- メインスレッドで使った時間：{mainUs:0.0} µs／フレーム（KlakNDI の処理は含まない）");
            }
            var core = Context.PluginManager.GetPlugin<CorePlugin>();
            if (core != null && core.NdiOutput) sb.Append("\n\n**注意**：内蔵の「NDI出力」がオンです。負荷を比べるときは「いいえ」にしてください。");

            var text = sb.ToString();
            if (text != Status) SetDataInput(nameof(Status), text, true);
        }
    }

    // ───────── 送信 fps の選択肢 ─────────

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
}
