# Low-Load NDI Output for Warudo

Warudo の映像を、描画ループを待たせずに NDI で送るプラグインです。メイン出力と、カメラごとの出力に使えます。

Sends Warudo's video over NDI without stalling the render loop, for the main output and for individual cameras.

## Warudo の内蔵 NDI 出力の問題点 / Problems with Warudo's built-in NDI output

Warudo Pro 0.15.1 のコード（`CorePlugin.UpdateNdiOutput`、`CameraAsset.UpdateNdiOutputs`、`CameraAsset.ShouldRender`）と、同梱の KlakNDI（`NdiSender`）で確認した内容です。

1. **描画したフレームをすべて読み戻して送る**：GPU で YUV に変換し、読み戻して NDI に渡す処理を、描画のたびに行います。送る回数を間引く処理はありません。fps を制限せずに動かしていると、オンにしただけで空のシーンでも fps が約半分になります（約 1200 → 570fps、CPU Busy 0.52 → 1.66ms）。
2. **前の送信が終わるまで、メインスレッドで待つ**：NDI への送信は非同期ですが、次のフレームを渡すときに前の送信の終わりを待ちます。この待ちが描画ループの中で起きます。
3. **送信 fps を選べない**：Warudo の描画 fps のまま送ります。モニター用に 30fps だけ送る、といった使い分けができません。
4. **受信側の fps 表記が常に 59.94p**：送るフレームにフレームレートを書いていないので、実際に何 fps で届いていても受信側には 59.94p と表示されます。宣言された fps に合わせて出力する機材では、困ることがあります。
5. **アルファを常に付けて送る**：透過背景を使わないときも、アルファ付き（UYVA）で変換・送信します。
6. **NDI 名が `Warudo` で固定**：変えられません。
7. **受信者がいなくても送り続ける**：誰も受信していなくても、毎フレームの変換と読み戻しは止まりません。
8. **カメラごとの出力はもっと重い**：カメラアセットの「NDI出力」をオンにすると、そのカメラは**毎フレーム描画され**（メインカメラでなくても）、毎フレーム読み戻されます。オンにしたカメラの数だけ、描画と読み戻しが増えます。

Warudo の fps を 60 に固定してメイン出力を 60fps で送るだけなら、1 の読み戻しも毎秒 60 回なので、負荷の差は小さくなります。それ以外の問題は、fps を固定していても残ります。

Confirmed in Warudo Pro 0.15.1's code and its bundled KlakNDI (`NdiSender`).

1. **Every rendered frame is read back and sent.** It converts to YUV on the GPU and reads back on every render, with no rate limiting. Uncapped, just turning it on halves the frame rate of an empty scene (about 1200 → 570 fps, CPU Busy 0.52 → 1.66 ms).
2. **The main thread waits for the previous send** before handing over the next frame.
3. **The send rate cannot be chosen**; it follows the render rate.
4. **Receivers always see 59.94p**, because no frame rate is written into the frames.
5. **Alpha is always sent** (UYVA), even when you don't need it.
6. **The NDI name is fixed** to `Warudo`.
7. **It keeps sending with no receivers.**
8. **Per-camera outputs are heavier still**: a camera's own "NDI Output" makes that camera **render every frame** (even when it is not the main camera) and read back every frame.

With Warudo capped at 60 fps and only the main output sent at 60 fps, problem 1 costs little, but the others remain.

## このプラグインでできること / What this plugin does

- **送信 fps のタイミングでだけ読み戻す**：読み戻しは非同期で、NDI への送信は別スレッドで行うので、描画ループを待たせません。有効にしても fps はほとんど下がりません（CPU Busy 0.56ms）。
- **送信 fps を選べる**：60・59.94・50・30・29.97・25・24・23.976・15・10 から選びます。59.94・29.97・23.976 は NTSC の分数（1001 分母）で送るので、受信側の表記も正しくなります（Playground 版）。
- **アルファ、NDI 名を選べる**。受信者がいないあいだは読み戻しも止められます（Playground 版）。
- **カメラごとの出力**（Playground 版）：アセット「Low-Load NDI カメラ出力」を置きます。メインカメラでないカメラは**送信するフレームだけ**描画するので、30fps で送るなら描画も毎秒 30 回で済みます。

- **Reads back only at the send rate**, asynchronously, and sends on a worker thread, so the render loop never waits (CPU Busy 0.56 ms with it on).
- **Choose the send rate** from common video rates; 59.94, 29.97 and 23.976 use the 1001 denominator, so receivers show the right rate (Playground version).
- **Choose alpha and the NDI name**, and stop reading back while nobody is connected (Playground version).
- **Per-camera outputs** (Playground version): a non-main camera is rendered **only on send frames**.

### 使いどころ / Where it helps

- **モニタリング用の出力**：配信には 60fps で出しつつ、演者やスタッフが確認するためのモニターには 30fps や 15fps で送る、といった使い分けができます。送る枚数が減るぶん、NDI の圧縮（CPU）、ネットワークの帯域、受信側の負荷が減ります。
- **LED ビジョンやステージの映像**：LED ウォールや会場モニターへ送る映像は、24・25・30fps など、つなぐ機材に合わせた fps で送れます。
- **サブカメラをいくつも NDI で出す**：内蔵のカメラ出力は台数ぶん毎フレーム描画・読み戻しが増えますが、このプラグインは送信 fps の回数だけです。台数が多いほど差が大きくなります。

- **Monitoring feeds**: send 60 fps to the stream but 30 or 15 fps to a performer or crew monitor, saving NDI compression (CPU), network bandwidth and receiver work.
- **LED walls and stage screens**: send at whatever the equipment expects (24, 25, 30 fps …).
- **Several sub-cameras over NDI**: the built-in output adds a full render and readback per camera every frame; this plugin only renders and reads back at the send rate.

## 内蔵 NDI との比較 / Comparison with the built-in NDI output

| | 内蔵「NDI出力」 | このプラグイン（Playground 版） | このプラグイン（MOD 版） |
|---|---|---|---|
| 送る部品 | KlakNDI の `NdiSender`（Texture モード） | NDI SDK を直接呼ぶ | KlakNDI の `NdiSender` を借りる |
| 読み戻す回数 | **描画したフレームすべて** | 送信 fps の回数だけ | 送信 fps の回数だけ |
| 送信 fps | 選べない（描画 fps のまま） | 60〜10 の 10 種類から選ぶ | 60〜10 の 10 種類から選ぶ |
| 受信側の fps 表記 | 常に 59.94p（KlakNDI がフレームレートを書かない） | 選んだ fps | 常に 59.94p |
| 取り込むタイミング | フレームの終わり | フレームの終わり（内蔵と同じフレーム） | フレームの終わり（内蔵と同じフレーム） |
| YUV への変換 | GPU（UYVA） | NDI が CPU で行う（BGRA／BGRX で渡す） | GPU（UYVY／UYVA） |
| アルファ | 常にあり | 選べる | 選べる |
| 送信の待ち | 前の送信が終わるまで、メインスレッドで待つ | 別スレッドで送るので、描画ループは待たない | メインスレッドで待つ（送信 fps の回数だけ） |
| 受信者がいないとき | 送り続ける | 読み戻しも止める（設定で切り替え） | 送り続ける |
| NDI 名 | `Warudo` 固定 | 設定で決める | 設定で決める |
| カメラごとの出力 | カメラの「NDI出力」。そのカメラが毎フレーム描画・読み戻しされる | アセット「Low-Load NDI カメラ出力」。メインカメラ以外は送信フレームだけ描画 | なし |
| ツールバーのアイコン | なし | あり | あり |

- **遅延**：どれも同じフレームの映像を取り込み、GPU からの読み戻しの仕組みも同じ。Playground 版は YUV への変換を CPU で行うぶん、数ミリ秒遅くなる可能性がある（未計測）。送信 fps を下げると、次に送るまでのあいだ受信側の映像は前のフレームのままになる。
- **Latency**: all three capture the same end-of-frame image with the same GPU readback. The Playground version converts to YUV on the CPU, which may add a few ms (not measured).

## 計測 / Measurements

PresentMon、fps 制限なし、ウィンドウ表示。CPU Busy は 1 フレームあたりの CPU の処理時間。

| 状態 / State | fps | CPU Busy | GPU Busy |
|---|---|---|---|
| 内蔵 NDI オン / Built-in NDI on（空のシーン / empty scene） | 約 570 | 1.66 ms | 0.56 ms |
| 内蔵 NDI オフ / Built-in NDI off（空のシーン / empty scene） | 約 1200 | 0.52 ms | 0.41 ms |
| このプラグイン MOD 版 / This plugin, MOD（ステージのシーン / stage scene） | 1099 | 0.56 ms | 0.55 ms |

## ~~正直なところ / To be honest~~

~~**Warudo の fps を 60 などに固定して使っているなら、内蔵の「NDI出力」との差はほぼありません。**
内蔵の NDI が重くなるのは「描画したフレームの数だけ読み戻して送る」作りのためで、fps を固定していれば読み戻しも毎秒 60 回前後に収まるからです。
差がはっきり出るのは、fps を制限せずに数百〜1000fps 以上で動かしているときだけです。
つまり、ほとんど自己満足のために作ったプラグインです。~~

~~**If you cap Warudo's frame rate (e.g. to 60 fps), there is almost no difference from the built-in NDI output.**
The built-in output is heavy because it reads back and sends every rendered frame; with a capped frame rate that is only about 60 times a second anyway.
The difference only shows when Warudo runs uncapped at hundreds of fps or more. This plugin is mostly for my own satisfaction.~~

→ メイン出力を 60fps で送るだけなら今もこのとおりですが、送信 fps を下げる使い方とカメラごとの出力では、fps を固定していても差が出ます（上の「使いどころ」）。
→ This still holds for a single 60 fps main output, but lowering the send rate and per-camera outputs make a difference even with a capped frame rate (see above).

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

メインカメラ以外のカメラは、送るフレームだけカメラを有効にして、Warudo のふつうの描画で描かせます。そのためポストプロセスもふつうにかかります。モーションブラーは「前に送ったときからの動き」でブレるので、送信 fps が低いほどブレが長くなります（本物のカメラで fps を下げたときと同じ）。Built-in RP で確認済み、URP は未確認です。
A non-main camera is enabled only on send frames and rendered through Warudo's normal pipeline, so post-processing works as usual. Motion blur covers the motion since the previous send, so it gets longer at lower send rates, like a real camera. Tested in the Built-in render pipeline; URP is untested.

使うときは、Warudo の設定の内蔵「NDI出力」と、カメラの内蔵「NDI出力」を「いいえ」にしてください。
Turn off Warudo's built-in "NDI Output" setting and the cameras' own NDI outputs when you use this plugin.

## 動作確認 / Tested with

Warudo Pro 0.15.1（Built-in RP）, Windows 11, RTX 5080
