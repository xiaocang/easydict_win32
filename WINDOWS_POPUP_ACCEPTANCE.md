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
