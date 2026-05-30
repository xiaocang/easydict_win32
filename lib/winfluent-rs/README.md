# winfluent-rs

Windows-only Rust-native Fluent application framework.

The public API exposes stable view, theme, command, task, subscription,
window, accessibility, and platform tokens. Renderer and operating system
details stay behind the framework boundary.

## Crates

- `win_fluent`: public framework API.
- `win_fluent_testkit`: token, theme, accessibility, and layout snapshot helpers.
- `win_fluent_gallery`: small executable that builds representative control trees.

## Design constraints

- Application code imports `win_fluent::prelude::*`.
- Application code returns `View<Message>`, never backend element types.
- Platform concepts are described through framework tokens.
- View trees are inspectable without starting a window.
- The first implementation keeps external dependencies out of the core crate so
  token APIs can stabilize before renderer integration.

