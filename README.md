# SnipasteOCR

仿 Snipaste 的截图 OCR 小工具:按热键框选屏幕区域,本地离线识别文字,支持部分选词、复制、保存。

基于 .NET 10 WinForms + NativeAOT,发布后是**单个原生 exe**,无需安装 .NET 运行时;OCR 引擎 [SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 与中文识别模型全部内嵌,识别过程**完全离线**。

## 功能

- **F1** — 截屏 OCR:框选区域后弹出结果窗口
- **F2** — 截屏复制:框选区域直接以 PNG 复制到剪贴板
- 支持鼠标右键托盘菜单触发,热键被占用时会提示
- 识别结果窗口:
  - 按截图原始尺寸 1:1 显示,缩放时窗口跟随图片大小联动
  - 单击选中整个文本块,在文本块内**按住拖动可部分选词**(像选网页文字一样)
  - `Ctrl+C` 复制选中内容(整行或选中的字符范围),`Ctrl+A` 全选
  - `Ctrl+滚轮` / `+` / `-` 缩放,`0` 适应窗口,`Esc` 关闭
  - 一键复制全部 / 保存截图为 PNG

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11 (x64) |
| CPU | 需支持 AVX2(2011 年后的 Intel/AMD 桌面/移动 CPU 基本都支持) |

> AVX2 是 SimdPaddleOCR 在 NativeAOT 下启用 SIMD 内核的硬性要求,否则推理速度会差一个数量级,因此发布时固定编译为 AVX2 指令集。

## 快速开始(发布产物)

发布后的 `SnipasteOcr.exe` 单文件即可运行(根据选择模型不同,约37MB-156MB):

```
SnipasteOcr.exe      # 启动后驻留系统托盘
```

启动后按 **F1** 框选区域,稍等识别完成即可在结果窗口中选择、复制文字。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
# 调试运行
dotnet build SnipasteOcr -c Debug

# AOT 发布 (win-x64 单文件原生 exe)
dotnet publish SnipasteOcr -c Release -r win-x64 --self-contained -p:PublishAot=true
```

产物位于 `SnipasteOcr/bin/Release/net10.0-windows/win-x64/publish/`,其中的语言资源目录(`cs`、`de`、`zh-Hans` 等)可删除,不影响运行。

## 项目结构

```
SnipasteOcr/
├── Program.cs            # 入口: 手动消息循环 + 全局热键注册 (F1/F2)
├── SnipCoordinator.cs    # 截图流程调度
├── SnipOverlayForm.cs    # 全屏截图覆盖层: 框选、遮罩、尺寸提示
├── OcrService.cs         # OCR 引擎封装 (懒加载、独立线程推理)
├── OcrResultForm.cs      # 结果窗口: 框叠加、字符级拖选、缩放联动
├── TrayController.cs     # Win32 托盘图标
└── NativeMethods.cs      # Win32 P/Invoke
```

## 技术要点

- **NativeAOT + 裁剪**: `IsAotCompatible=true`、`IlcInstructionSet=avx2`;OCR 引擎与模型通过程序集嵌入资源加载,需在 `TrimmerRootAssembly` 中显式保留,`PublishAot` 默认开启。
- **Per-Monitor DPI v2**: 截图是物理像素,窗口客户区是逻辑像素,绘制/命中/字符定位三套坐标统一经 DPI 换算,保证 100% 缩放下像素对齐。
- **字符级选词**: PaddleOCR 只输出整行文本与整行检测框(四边形)。选词按字符宽度权重(汉字 1.0 / ASCII 0.6 / 空格 0.35)将字符投影到检测框主轴上,鼠标位置经投影反解出行内字符索引;复制内容直接按字符索引切原文,结果精确。
- **推理不锁图**: 识别前对位图做瞬时 `LockBits` 拷贝,推理在独立字节缓冲上进行,UI 线程可随时重绘。

## 已知限制

- 中文识别为主(中文 V6 Medium 模型),英文等西文字符可用但非最优
- 字符级选区按宽度比例估算,选区边缘与真实字符边界可能有 1~2 像素偏差(不影响复制内容)
- 仅支持 x64 Windows;发布产物与编译机架构绑定,换架构需重新 publish

## 依赖

- [Sdcb.SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 1.4.2 — 纯 C# 的 PaddleOCR 推理引擎
- [Sdcb.SimdPaddleOCR.Models.ChineseV6Medium](https://www.nuget.org/packages/Sdcb.SimdPaddleOCR.Models.ChineseV6Medium) 1.0.0 — 中文 det+rec+cls 模型与字典(嵌入资源)

## License

MIT,(依赖项目开源协议保持原协议)
