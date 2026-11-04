# Low-Load NDI Output for Warudo

Warudo の映像を、描画ループを待たせずに NDI で送るプラグインです。メイン出力と、カメラごとの出力に使えます。
内蔵の「NDI出力」は毎フレーム GPU からの読み戻しを行うため、fps を制限せずに動かしていると、オンにしたとき Warudo の fps が大きく下がります（空のシーンで約 1200 → 570fps）。
このプラグインは、送信 fps のタイミングでだけ非同期に読み戻すので、有効にしても fps はほとんど下がりません。

Sends Warudo's video over NDI without stalling the render loop, for the main output and for individual cameras.
The built-in NDI output reads back from the GPU every frame, which roughly halves Warudo's frame rate when the frame rate is uncapped.
This plugin reads back asynchronously, and only as often as the NDI send rate.

## 便利なところ / Where it helps

**送信 fps を、Warudo の描画 fps とは別に決められます。** 内蔵の NDI は、描画したフレームをすべて送ります。

- **モニタリング用の出力**：配信には 60fps で出しつつ、演者やスタッフが確認するためのモニターには 30fps や 15fps で送る、といった使い分けができます。送る枚数が減るぶん、NDI の圧縮（CPU）、ネットワークの帯域、受信側の負荷が減ります。
- **LED ビジョンやステージの映像**：LED ウォールや会場モニターへ送る映像は、24・25・30fps など、つなぐ機材に合わせた fps で送れます。59.94・29.97・23.976 は NTSC の分数（1001 分母）で送るので、受信側の表記も正しくなります。
- **カメラごとの出力**：内蔵のカメラの NDI 出力をオンにすると、そのカメラは毎フレーム描画・読み戻しされます。このプラグインのカメラ出力アセットは、メインカメラでないカメラを**送信するフレームだけ**描画するので、たとえば 30fps で送るなら描画も毎秒 30 回で済みます。サブカメラを何台も NDI で出したいときほど差が大きくなります。

**You can set the NDI send rate independently of Warudo's render rate.** The built-in output sends every rendered frame.

- **Monitoring feeds**: send 60 fps to the stream but 30 or 15 fps to a performer or crew monitor. Fewer frames means less NDI compression (CPU), less network bandwidth and less work on the receiver.
- **LED walls and stage screens**: send at whatever the equipment expects (24, 25, 30 fps …). 59.94, 29.97 and 23.976 use the NTSC 1001 denominator, so receivers show the right rate.
- **Per-camera outputs**: the built-in per-camera NDI output renders and reads back that camera every frame. The camera output asset renders a non-main camera **only on send frames**, so sending at 30 fps costs 30 renders a second. The more sub-cameras you send, the bigger the difference.

## ~~正直なところ / To be honest~~

~~**Warudo の fps を 60 などに固定して使っているなら、内蔵の「NDI出力」との差はほぼありません。**
内蔵の NDI が重くなるのは「描画したフレームの数だけ読み戻して送る」作りのためで、fps を固定していれば読み戻しも毎秒 60 回前後に収まるからです。
差がはっきり出るのは、fps を制限せずに数百〜1000fps 以上で動かしているときだけです。
つまり、ほとんど自己満足のために作ったプラグインです。~~

~~**If you cap Warudo's frame rate (e.g. to 60 fps), there is almost no difference from the built-in NDI output.**
The built-in output is heavy because it reads back and sends every rendered frame; with a capped frame rate that is only about 60 times a second anyway.
The difference only shows when Warudo runs uncapped at hundreds of fps or more. This plugin is mostly for my own satisfaction.~~

→ メイン出力を 60fps で送るだけなら今もこのとおりですが、送信 fps を下げる使い方とカメラごとの出力では、fps を固定していても差が出ます（上の「便利なところ」）。
→ This still holds for a single 60 fps main output, but lowering the send rate and per-camera outputs make a difference even with a capped frame rate (see above).

## 計測 / Measurements

PresentMon、fps 制限なし、ウィンドウ表示。CPU Busy は 1 フレームあたりの CPU の処理時間。

| 状態 / State | fps | CPU Busy | GPU Busy |
|---|---|---|---|
| 内蔵 NDI オン / Built-in NDI on（空のシーン / empty scene） | 約 570 | 1.66 ms | 0.56 ms |
| 内蔵 NDI オフ / Built-in NDI off（空のシーン / empty scene） | 約 1200 | 0.52 ms | 0.41 ms |
| このプラグイン MOD 版 / This plugin, MOD（ステージのシーン / stage scene） | 1099 | 0.56 ms | 0.55 ms |

## 中身 / Contents

| フォルダ | 内容 |
|---|---|
| `Playground/` | **おすすめ。** Warudo Playground 版（`Warudo_Data/StreamingAssets/Playground/` に置く）。NDI SDK を直接呼び、読み戻しから送信までを自前で行う。メイン出力（設定ページ）とカメラ出力アセットの両方がある。受信者がいないときは読み戻しも止める。送信 fps が受信側に正しく伝わる |
| `Mod/` | プラグイン MOD 版（`.warudo`）。メイン出力だけ。uMod の制限（P/Invoke 禁止など）の中で、Warudo 同梱の KlakNDI を使い、送信するフレームだけ映像を渡す。YUV への変換は GPU。KlakNDI がフレームレートを書かないので、受信側の表記は常に 59.94p になる |

**両方を同時に入れないでください。** プラグイン ID が同じなので、後から読み込んだほうがエラーになります。
Do not install both at once; they share the same plugin ID.

## 設定 / Settings

### メイン出力（設定ページ「Low-Load NDI Output」）/ Main output (plugin settings)

- 有効 / Enabled
- NDI名 / NDI name（既定 `Warudo LowLoad`）
- 送信fps / Send fps：60・59.94・50・30・29.97・25・24・23.976・15・10 から選ぶ（既定 59.94） / Pick from common video rates
- アルファ / Alpha
- 受信者がいないときは止める / Pause without receivers（Playground 版のみ）
- 状態表示 / Status（実際の送信 fps、NDI の接続数など）

ツールバーのビデオカメラ形のアイコンから、この設定ページを開けます。
The video-camera icon in the toolbar opens this settings page.

### カメラ出力（アセット「Low-Load NDI カメラ出力」、Playground 版のみ）/ Camera output asset (Playground only)

- 有効 / Enabled
- カメラ / Camera
- NDI名 / NDI name（空なら `Warudo LowLoad - カメラ名`）
- 送信fps / Send fps（既定 30）
- アルファ / Alpha
- 受信者がいないときは止める / Pause without receivers（受信者が 0 のあいだは描画もしない）
- 状態表示 / Status（Warudo が描画しているか、自分で描画させた回数など）

自分で描画させるのは Built-in RP のときだけです（URP では、メインカメラのときだけ映像が更新されます）。
Rendering on demand works in the Built-in render pipeline only.

使うときは、Warudo の設定の内蔵「NDI出力」と、カメラの内蔵「NDI出力」を「いいえ」にしてください。
Turn off Warudo's built-in "NDI Output" setting and the cameras' own NDI outputs when you use this plugin.

## 動作確認 / Tested with

Warudo Pro 0.15.1（Built-in RP）, Windows 11, RTX 5080
