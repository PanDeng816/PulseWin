# Third-Party Notices

本项目的展示层还原与数据层移植参考了以下开源项目,在此致谢并保留各自许可声明。

## Pulse(qunqin24/Pulse)—— Apache-2.0
展示层(屏幕边缘用量环 rail、卡片、交互规格)的设计还原自
https://github.com/qunqin24/Pulse(macOS 原生应用)。本项目为 Windows/WPF
重写,几何常量与交互规则参考其源码与设计文档,未复制其 Swift 代码。
License: Apache License 2.0,见 https://www.apache.org/licenses/LICENSE-2.0

## GOAT + Go Usage Monitor —— MIT
数据层(Command Code / OpenCode Go 的 API 客户端、凭据发现与 DPAPI 加密存储、
快照缓存)由 https://github.com/ahuud251/goat-go-usage-monitor 移植,
文件位于 `Services/` 目录。

> MIT License
> Copyright (c) 2026 GOAT + Go Usage Monitor contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## 品牌图标
- Command Code / GOAT 图标取自 https://commandcode.ai 官网 favicon,
  仅作个人工具图标使用;Command Code 及其标识归其权利方所有。
- OpenCode 图标取自 Pulse 仓库(lobehub/lobe-icons 风格)。

## 免责声明
本项目为非官方社区工具,与 Command Code、OpenCode 及其运营方没有隶属或
背书关系。各数据接口由上游服务提供,响应格式变化可能导致功能失效。
