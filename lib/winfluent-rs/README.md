# winfluent-rs

Windows-only Rust-native Fluent application framework.

The public API exposes stable view, theme, command, task, subscription,
window, accessibility, and platform tokens. Renderer and operating system
details stay behind the framework boundary.

## Crates

- `win_fluent`: public framework API.
- `win_fluent_backend_iced`: internal Iced renderer adapter.
- `win_fluent_platform_win`: internal Windows platform adapter.
- `win_fluent_testkit`: token, theme, accessibility, and layout snapshot helpers.
- `win_fluent_gallery`: small executable that builds representative control trees.

## Demo binaries

- `win_fluent_iced_demo`: opens a general token-driven Iced demo window.
- `win_fluent_mini_demo`: opens a Mini Window reference demo with topmost,
  skip-taskbar, stateful text editor, and simulated streaming output.

## Design constraints

- Application code imports `win_fluent::prelude::*`.
- Application code returns `View<Message>`, never backend element types.
- Platform concepts are described through framework tokens.
- View trees are inspectable without starting a window.
- External renderer and platform dependencies stay out of the core crate so
  token APIs can stabilize independently.
