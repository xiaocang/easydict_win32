# Windows Selection Pop-up Acceptance / Windows 划词浮标验收

## English

- **Review date:** 2026-10-25 UTC
- **Candidate:** `artifacts/ui-review-o4x-focus-styles/Easydict.WinUI.dll`
**SHA-256:** `3F418ED41A10D31F59ECB54EB27DB88EAFEEE60DCE3CFFA23D4B47BE5F106D9B`

### Passed in the isolated checkerboard preview

- The pop-up surface is transparent; the checkerboard remains visible around the icon/action buttons, with no visible backing plate or outer frame.
- The main icon click was received once; an additional action click was received once.
- Showing the pop-up left the foreground HWND unchanged.
- `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` were successfully set and read back; the final extended style was `0x08000088`.
- The preview closed normally. Its action handlers only updated counters and did not translate text or open URLs.
- Magpie icon attribution and MIT license text, plus the WinUIEx MIT notice for the adapted backdrop approach, remain in the distributable source/package.

### Not verified by this preview

- End-to-end selection through the global mouse hook in a real third-party application.
- Every monitor arrangement, DPI/scaling value, and Windows theme.
- Requests to a real Magpie gateway or model.

## Follow-up: hover and icon size review

- **Review date:** 2026-10-26 UTC
- **Candidate:** `artifacts/ui-review-o4y-hover-icon/Easydict.WinUI.exe`
- **SHA-256 of `Easydict.WinUI.dll`:** `80CC4AC6757C71B45238EE3C3E5C9589D938CB0214C66F4E849AAA5AEBB652CD`

### Passed in the isolated checkerboard preview

- Main and additional-action buttons showed no backing plate or frame during hover, after pointer exit, or after click release.
- The main icon was increased from 19 DIP to 25 DIP; no clipping was observed during its float animation.
- Main-icon and `preview-web` action counters each incremented once.
- Pressed-state background and border resources are transparent in source, but a held-press frame was not captured independently.
- The isolated preview closed normally.

### 中文：悬停与图标尺寸复核

- **复核日期：** 2026-10-26 UTC
- **候选版本：** `artifacts/ui-review-o4y-hover-icon/Easydict.WinUI.exe`
- **`Easydict.WinUI.dll` SHA-256：** `80CC4AC6757C71B45238EE3C3E5C9589D938CB0214C66F4E849AAA5AEBB652CD`

#### 隔离棋盘预览已通过

- 主按钮和附加动作按钮在悬停、移出恢复及点击释放后都没有底板或边框。
- 主图标从 19 DIP 增至 25 DIP；浮动动画中未观察到图标裁切。
- 主图标和 `preview-web` 附加动作的点击计数均为 1。
- 源码中的按下态背景与边框资源已设为透明，但没有单独捕获持续按住时的画面。
- 隔离预览已正常关闭。

## 中文

- **复核日期：** 2026-10-25 UTC
- **候选版本：** `artifacts/ui-review-o4x-focus-styles/Easydict.WinUI.dll`
**SHA-256：** `3F418ED41A10D31F59ECB54EB27DB88EAFEEE60DCE3CFFA23D4B47BE5F106D9B`

### 隔离棋盘预览已通过

- 浮窗表面透明，图标和动作按钮周围可以看到棋盘底图；没有可见底板或外围边框。
- 主图标点击事件计数为 1；附加动作点击事件计数为 1。
- 显示浮窗前后，前景 HWND 保持不变。
- `WS_EX_NOACTIVATE` 与 `WS_EX_TOOLWINDOW` 均成功写入并读回；最终扩展样式为 `0x08000088`。
- 预览正常关闭。预览动作只更新计数，不会翻译文本或打开 URL。
- Magpie 图标来源说明与 MIT 许可证，以及浮窗背景实现参考 WinUIEx 所附 MIT 通知，均保留在可分发源码/包中。

### 本次预览未验证

- 在真实第三方应用中通过全局鼠标钩子完成端到端划词。
- 所有显示器布局、DPI/缩放值及 Windows 主题组合。
- 向真实 Magpie 网关或模型发送请求。
