# Low-Load NDI Output for Warudo

Warudo のメイン出力を、描画ループを待たせずに NDI で送るプラグインです。
内蔵の「NDI出力」は毎フレーム GPU からの読み戻しを行うため、オンにすると Warudo の fps が大きく下がります（空のシーンで約 1200 → 570fps）。
このプラグインは、送信 fps のタイミングでだけ非同期に読み戻すので、有効にしても fps はほとんど下がりません。

Sends Warudo's main output over NDI without stalling the render loop.
The built-in NDI output reads back from the GPU every frame, which roughly halves Warudo's frame rate.
This plugin reads back asynchronously, and only as often as the NDI send rate.

## 中身 / Contents

| フォルダ | 内容 |
|---|---|
| `Playground/` | Warudo Playground 版（`Warudo_Data/StreamingAssets/Playground/` に置く）。NDI SDK を直接呼び、読み戻しから送信までを自前で行う |
| `Mod/` | プラグイン MOD 版（試作中）。uMod の制限内で、Warudo 同梱の KlakNDI を使う |

## 設定 / Settings

- 有効 / Enabled
- NDI名 / NDI name（既定 `Warudo LowLoad`）
- 送信fps / Send fps（既定 59.94）
- アルファ / Alpha
- 状態表示 / Status（実際の送信 fps など）

使うときは、Warudo の設定で内蔵の「NDI出力」を「いいえ」にしてください。
Turn off Warudo's built-in "NDI Output" setting when you use this plugin.

## 動作確認 / Tested with

Warudo Pro 0.15.1（Built-in RP）, Windows 11, RTX 5080
