# Low-Load NDI Output for Warudo

Warudo のメイン出力を、描画ループを待たせずに NDI で送るプラグインです。
内蔵の「NDI出力」は毎フレーム GPU からの読み戻しを行うため、fps を制限せずに動かしていると、オンにしたとき Warudo の fps が大きく下がります（空のシーンで約 1200 → 570fps）。
このプラグインは、送信 fps のタイミングでだけ非同期に読み戻すので、有効にしても fps はほとんど下がりません。

Sends Warudo's main output over NDI without stalling the render loop.
The built-in NDI output reads back from the GPU every frame, which roughly halves Warudo's frame rate when the frame rate is uncapped.
This plugin reads back asynchronously, and only as often as the NDI send rate.

## 正直なところ / To be honest

**Warudo の fps を 60 などに固定して使っているなら、内蔵の「NDI出力」との差はほぼありません。**
内蔵の NDI が重くなるのは「描画したフレームの数だけ読み戻して送る」作りのためで、fps を固定していれば読み戻しも毎秒 60 回前後に収まるからです。
差がはっきり出るのは、fps を制限せずに数百〜1000fps 以上で動かしているときだけです。
つまり、ほとんど自己満足のために作ったプラグインです。

**If you cap Warudo's frame rate (e.g. to 60 fps), there is almost no difference from the built-in NDI output.**
The built-in output is heavy because it reads back and sends every rendered frame; with a capped frame rate that is only about 60 times a second anyway.
The difference only shows when Warudo runs uncapped at hundreds of fps or more. This plugin is mostly for my own satisfaction.

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
| `Mod/` | プラグイン MOD 版（`.warudo`）。uMod の制限（P/Invoke 禁止など）の中で、Warudo 同梱の KlakNDI を使い、送信するフレームだけ映像を渡す。YUV への変換は GPU |
| `Playground/` | Warudo Playground 版（`Warudo_Data/StreamingAssets/Playground/` に置く）。NDI SDK を直接呼び、読み戻しから送信までを自前で行う。受信者がいないときは読み戻しも止める |

**両方を同時に入れないでください。** プラグイン ID が同じなので、後から読み込んだほうがエラーになります。
Do not install both at once; they share the same plugin ID.

## 設定 / Settings

- 有効 / Enabled
- NDI名 / NDI name（既定 `Warudo LowLoad`）
- 送信fps / Send fps（既定 59.94）
- アルファ / Alpha
- 状態表示 / Status（送信の回数など）

使うときは、Warudo の設定で内蔵の「NDI出力」を「いいえ」にしてください。
Turn off Warudo's built-in "NDI Output" setting when you use this plugin.

## 動作確認 / Tested with

Warudo Pro 0.15.1（Built-in RP）, Windows 11, RTX 5080
